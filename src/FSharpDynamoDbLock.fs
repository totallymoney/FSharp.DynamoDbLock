module FSharpDynamoDbLock

open System
open System.Net
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model
open Serilog

open Serilog.Events

module Seq =
    let toDictionary xs =
        xs |> dict |> (fun d -> System.Collections.Generic.Dictionary(d))

type TimeProvider = unit -> System.DateTimeOffset

type Settings =
    { Client : AmazonDynamoDBClient
      TableName : string }

type WaitForLock =
    { Until : DateTimeOffset
      CheckDelay : TimeSpan }

type LockSettings =
    { LockExpiry : DateTimeOffset
      WaitForLock : WaitForLock option }

type LockError =
    | BadPutRequest of int
    | BadDeleteRequest of int
    | RequestFailed of Exception
    | KeyNotAvailable
    | WaitForLockExpired
    | LockNotAvailableToRelease

type LockId =
    { ClientId : Guid
      LockKey : string }

type AcquireMode =
    | Retry
    | Immediate

let private tryAcquireLock' (client : AmazonDynamoDBClient)
                            tableName
                            (logger : ILogger)
                            (lockExpiry : DateTimeOffset)
                            (now : TimeProvider)
                            acquireMode
                            { ClientId = clientId; LockKey = key }
                            : Async<Result<LockId, LockError>> =

    let successLevel =
        match acquireMode with
        | Retry -> LogEventLevel.Verbose
        | Immediate -> LogEventLevel.Verbose

    let failLevel =
        match acquireMode with
        | Retry -> LogEventLevel.Debug
        | Immediate -> LogEventLevel.Error

    let sw = System.Diagnostics.Stopwatch.StartNew()
    let expiryInUnixSeconds = lockExpiry.ToUnixTimeSeconds()
    let expireInUnixMs = lockExpiry.ToUnixTimeMilliseconds()
    let nowUnixTimeInMs = (now ()).ToUnixTimeMilliseconds()
    let data =
        [ ("LockKey", AttributeValue(S = key))
          ("ClientId", AttributeValue(S = clientId.ToString()))
          // We need 2 expiry numbers. One for dynamo db's expire feature which
          // uses unix seconds, and one for our own use which we want to be more
          // precise (we use milliseconds in this case)
          ("ExpireInUnixSeconds", AttributeValue(N = (expiryInUnixSeconds.ToString())))
          ("ExpireInUnixMs", AttributeValue(N = (expireInUnixMs.ToString())))]
        |> Seq.toDictionary

    let request = PutItemRequest(tableName, data)
    request.ExpressionAttributeNames <-
        [ ("#lockKey", "LockKey")
          ("#expireInUnixMs", "ExpireInUnixMs") ] |> Seq.toDictionary
    request.ExpressionAttributeValues <-
        [ (":now", AttributeValue(N = nowUnixTimeInMs.ToString())) ] |> Seq.toDictionary
    // We can add the key if one doesn't exist, OR it expired before now
    request.ConditionExpression <- "attribute_not_exists(#lockKey) or :now > #expireInUnixMs"

    async {
        try
            let! result = client.PutItemAsync(request) |> Async.AwaitTask
            if result.HttpStatusCode >= HttpStatusCode.BadRequest
            then
                logger.Error("{Category}: Bad http status response code {StatusCode} when attempting to acquire lock for '{LockKey}'",
                             "AcquireDynamoLock", (int result.HttpStatusCode), key)
                return Error (BadPutRequest (int result.HttpStatusCode))
            else
                logger.Write(successLevel,
                             "{Category}: Success in acquiring lock for '{LockKey}', in {Elapsed} ms",
                             "AcquireDynamoLock", key, sw.ElapsedMilliseconds)
                return Ok { ClientId = clientId; LockKey = key }
        with
        | :? AggregateException as ex ->
            match ex.InnerExceptions.[0] with
            | :? ConditionalCheckFailedException ->
                logger.Write(failLevel,
                             "{Category}: Failed to acquire lock. '{LockKey}' is currently in use. Took {Elapsed} ms",
                              "AcquireDynamoLock", key, sw.ElapsedMilliseconds)
                return (Error KeyNotAvailable)
            | ex ->
                logger.Error(ex, "{Category}: Error while attempting to acquire lock '{LockKey}'. Took {Elapsed} ms",
                                 "AcquireDynamoLock", key, sw.ElapsedMilliseconds)
                return (Error (RequestFailed ex))
        | :? ConditionalCheckFailedException ->
            logger.Write(failLevel,
                         "{Category}: Failed to acquire lock. '{LockKey}' is currently in use. Took {Elapsed} ms",
                         "AcquireDynamoLock", key, sw.ElapsedMilliseconds)
            return Error KeyNotAvailable
        | ex ->
            logger.Error(ex, "{Category}: Error while attempting to acquire lock '{LockKey}'. Took {Elapsed} ms",
                             "AcquireDynamoLock", key, sw.ElapsedMilliseconds)
            return (Error (RequestFailed ex))
    }

let rec private tryAcquireLockRepeatedly
    (client : AmazonDynamoDBClient)
    tableName
    (logger : ILogger)
    lockExpiry
    wait
    (now : TimeProvider)
    ({ LockKey = key } as lockId)
    (sw : System.Diagnostics.Stopwatch)
    attemptCount = async {

    if now() > wait.Until then
        logger.Warning("{Category}: Failed to acquire lock for '{LockKey}'. Timed out after {Attempts} attempts, in {Elapsed} ms total",
                       "AcquireDynamoLock", key, attemptCount, sw.ElapsedMilliseconds)
        return (Error WaitForLockExpired)
    else
        let! result = tryAcquireLock' client
                                      tableName
                                      logger
                                      lockExpiry
                                      now
                                      AcquireMode.Retry
                                      lockId
        match result with
        | Ok _ ->
            logger.Verbose("{Category}: Success in acquiring lock for '{LockKey}' after {Attempts} attempts, in {Elapsed} ms total",
                           "AcquireDynamoLock", key, attemptCount + 1, sw.ElapsedMilliseconds)
            return result
        | Error KeyNotAvailable ->
            logger.Verbose("{Category}: Waiting for attempt {Attempt} to acquire lock for '{LockKey}'. {Elapsed} ms elapsed so far",
                           "AcquireDynamoLock", attemptCount + 1, key, sw.ElapsedMilliseconds)
            do! Async.Sleep (int wait.CheckDelay.TotalMilliseconds)
            // tail recursive in async
            return! tryAcquireLockRepeatedly client
                                             tableName
                                             logger
                                             lockExpiry
                                             wait
                                             now
                                             lockId
                                             sw
                                             (attemptCount + 1)
        | Error _ ->
            // for a normal error we can rely on AWSs internal retry code
            // and just return immediately
            return result
    }

let tryAcquireLock client
                   tableName
                   (logger : ILogger)
                   (now : TimeProvider)
                   lockSettings
                   key =
    let clientId = Guid.NewGuid()
    let lockId = { ClientId = clientId; LockKey = key }
    let logger = logger.ForContext("ClientId", clientId.ToString())
    match lockSettings.WaitForLock with
    | None ->
        tryAcquireLock'
            client tableName logger lockSettings.LockExpiry now AcquireMode.Immediate lockId
    | Some wait ->
        let sw = System.Diagnostics.Stopwatch.StartNew()
        tryAcquireLockRepeatedly
            client tableName logger lockSettings.LockExpiry wait now lockId sw 0

let releaseLock (client : AmazonDynamoDBClient)
                tableName
                (logger : ILogger)
                { ClientId = clientId; LockKey = key } =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let logger = logger.ForContext("ClientId", clientId.ToString())
    let dynamoKey =
        [ ("LockKey", AttributeValue(S = key)) ]
        |> Seq.toDictionary

    let request = DeleteItemRequest(tableName, dynamoKey)
    request.ExpressionAttributeNames <-
        [ ("#lockKey", "LockKey")
          ("#clientId", "ClientId") ] |> Seq.toDictionary
    request.ExpressionAttributeValues <-
        [ (":clientId", AttributeValue(S = clientId.ToString())) ] |> Seq.toDictionary
    // We should only release the lock if we were the one that added it!
    request.ConditionExpression <- "attribute_exists(#lockKey) and :clientId = #clientId"

    async {
        try
            let! result = client.DeleteItemAsync(request) |> Async.AwaitTask
            if result.HttpStatusCode >= HttpStatusCode.BadRequest
            then
                logger.Error("{Category}: Bad http status response code {StatusCode} when attempting to release lock for '{LockKey}', after {Elapsed} ms",
                             "AcquireDynamoLock", (int result.HttpStatusCode), key, sw.ElapsedMilliseconds)
                return Error (BadDeleteRequest (int result.HttpStatusCode))
            else
                logger.Verbose("{Category}: Success in releasing lock for '{LockKey}', in {Elapsed} ms",
                               "ReleaseDynamoLock", key, sw.ElapsedMilliseconds)
                return Ok ()

        with
        | :? AggregateException as ex ->
            match ex.InnerExceptions.[0] with
            | :? ConditionalCheckFailedException ->
                logger.Information("{Category}: Lock for '{LockKey}' could not be released. It's no longer valid. Took {Elapsed} ms",
                                   "ReleaseDynamoLock", key, sw.ElapsedMilliseconds)
                return (Error LockNotAvailableToRelease)
            | ex ->
                logger.Error(ex, "{Category}: Error while attempting to release lock '{LockKey}'. Took {Elapsed} ms",
                                 "ReleaseDynamoLock", key, sw.ElapsedMilliseconds)
                return (Error (RequestFailed ex))
        | :? ConditionalCheckFailedException ->
            logger.Information("{Category}: Lock for '{LockKey}' could not be released. It's no longer valid. Took {Elapsed} ms",
                               "ReleaseDynamoLock", key, sw.ElapsedMilliseconds)
            return (Error LockNotAvailableToRelease)
        | ex ->
            logger.Error(ex, "{Category}: Error while attempting to release lock '{LockKey}'. Took {Elapsed} ms",
                             "ReleaseDynamoLock", key, sw.ElapsedMilliseconds)
            return (Error (RequestFailed ex))
    }
