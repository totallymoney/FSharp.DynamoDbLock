module FSharpDynamoDbLock

open System
open System.Net
open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.Model

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
    | BadPutRequest of lockKey:string * statusCode:int
    | BadDeleteRequest of lockKey:string * statusCode:int
    | RequestFailed of lockKey:string * Exception
    | KeyNotAvailable of lockKey:string
    | WaitForLockExpired of lockKey:string * attempts:int * elapsedMs:int64
    | LockNotAvailableToRelease of lockKey:string

type LockId =
    { ClientId : Guid
      LockKey : string }

module LockError =
    let toString = function
        | BadPutRequest (lockKey, statusCode) ->
            $"Bad http status response code {statusCode} when attempting to acquire lock for '{lockKey}'"
        | BadDeleteRequest (lockKey, statusCode) ->
            $"Bad http status response code {statusCode} when attempting to release lock for '{lockKey}'"
        | RequestFailed (lockKey, ex) ->
            $"Error while attempting to acquire lock '{lockKey}'. {ex.ToString()}"
        | KeyNotAvailable lockKey ->
            $"Failed to acquire lock. '{lockKey}' is currently in use"
        | WaitForLockExpired (lockKey, attempts, elapsedMs) ->
            $"Failed to acquire lock for '{lockKey}'. Timed out after {attempts} attempts, in {elapsedMs} ms total"
        | LockNotAvailableToRelease lockKey ->
            $"Lock for '{lockKey}' could not be released. It's no longer valid"

let private tryAcquireLock' (client : AmazonDynamoDBClient)
                            tableName
                            (lockExpiry : DateTimeOffset)
                            (now : TimeProvider)
                            ({ ClientId = clientId; LockKey = key } as lockId)
                            : Async<Result<LockId, LockError>> =

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
            then return Error (BadPutRequest (key, (int result.HttpStatusCode)))
            else return Ok lockId
        with
        | :? AggregateException as ex ->
            match ex.InnerExceptions.[0] with
            | :? ConditionalCheckFailedException ->
                return Error (KeyNotAvailable key)
            | ex ->
                return Error (RequestFailed (key, ex))
        | :? ConditionalCheckFailedException ->
            return Error (KeyNotAvailable key)
        | ex ->
            return Error (RequestFailed (key, ex))
    }

[<TailCall>]
let rec private tryAcquireLockRepeatedly
    (client : AmazonDynamoDBClient)
    tableName
    lockExpiry
    wait
    (now : TimeProvider)
    ({ LockKey = key } as lockId)
    (sw : System.Diagnostics.Stopwatch)
    attemptCount = async {

    if now() > wait.Until then
        return Error (WaitForLockExpired (key, attemptCount, sw.ElapsedMilliseconds))
    else
        let! result =
            tryAcquireLock' client tableName lockExpiry now lockId
        match result with
        | Ok _ -> return result
        | Error (KeyNotAvailable _) ->
            do! Async.Sleep (int wait.CheckDelay.TotalMilliseconds)
            // tail recursive in async
            return! tryAcquireLockRepeatedly client
                                             tableName
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
                   (now : TimeProvider)
                   lockSettings
                   key =
    let clientId = Guid.NewGuid()
    let lockId = { ClientId = clientId; LockKey = key }
    match lockSettings.WaitForLock with
    | None ->
        tryAcquireLock' client tableName lockSettings.LockExpiry now lockId
    | Some wait ->
        let sw = System.Diagnostics.Stopwatch.StartNew()
        tryAcquireLockRepeatedly
            client tableName lockSettings.LockExpiry wait now lockId sw 0

let releaseLock (client : AmazonDynamoDBClient)
                tableName
                { ClientId = clientId; LockKey = key } =
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
            then return Error (BadDeleteRequest (key, int result.HttpStatusCode))
            else return Ok ()

        with
        | :? AggregateException as ex ->
            match ex.InnerExceptions.[0] with
            | :? ConditionalCheckFailedException ->
                return Error (LockNotAvailableToRelease key)
            | ex ->
                return Error (RequestFailed (key, ex))
        | :? ConditionalCheckFailedException ->
            return Error (LockNotAvailableToRelease key)
        | ex ->
            return Error (RequestFailed (key, ex))
    }
