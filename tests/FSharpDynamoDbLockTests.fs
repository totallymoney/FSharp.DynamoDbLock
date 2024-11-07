module FSharpDynamoDbLockTests

open System
open Amazon.DynamoDBv2
open Serilog
open Expecto
open Expecto.Flip

open FSharpDynamoDbLock

let now = (fun () -> DateTimeOffset.Now)

module Result =
    let get = function
        | Ok x -> x
        | Error e -> failwithf "Result Error: [%A]" (e.ToString())

    let toErrorOption xR =
        match xR with
        | Ok _ -> None
        | Error err -> Some err


let (|>>) r m = Result.map m r

let dynamoDBUrl =
    let env = Environment.GetEnvironmentVariables()
    if env.Contains("DYNAMO_LOCAL_URL")
    then string env.["DYNAMO_LOCAL_URL"]
    else "http://localhost:8808"


let getClient () =
    let config = AmazonDynamoDBConfig(ServiceURL = dynamoDBUrl)
    new AmazonDynamoDBClient("access", "secret", config)

let locksTableName = "LocksTestTable"

let lockSettings =
    { LockExpiry = now().AddSeconds(20.0)
      WaitForLock = Some { Until = now().AddSeconds(12.0)
                           CheckDelay = TimeSpan.FromMilliseconds(50.0) } }

[<Tests>]
let dynamoDbLockTests =
    testList "DynamoDbLock" [
        testCase "should successfully add and release lock" <| fun _ ->
            let client = getClient ()
            let key = Guid.NewGuid().ToString()
            let lockId =
                tryAcquireLock client locksTableName Log.Logger now lockSettings key
                |> Async.RunSynchronously

            lockId |>> ( fun x -> x.LockKey )
            |> Expect.equal "" (Ok key)

            Result.get lockId
            |> releaseLock client locksTableName Log.Logger
            |> Async.RunSynchronously
            |> Expect.equal "" (Ok ())

        testCase "should successfully add and release lock in series" <| fun _ ->
            let client = getClient ()
            let key = Guid.NewGuid().ToString()

            Seq.replicate 10 ()
            |> Seq.iter (fun _ ->
                let lockId =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key
                    |> Async.RunSynchronously

                lockId |>> ( fun x -> x.LockKey )
                |> Expect.equal "" (Ok key)

                Result.get lockId
                |> releaseLock client locksTableName Log.Logger
                |> Async.RunSynchronously
                |> Expect.equal "" (Ok ())
            )

        testCase "should successfully add and release locks in parallel with retries" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 6.0
                  WaitForLock = Some { Until = now().AddSeconds 6.0
                                       CheckDelay = TimeSpan.FromMilliseconds 10.0 } }
            let random = Random()
            let key = Guid.NewGuid().ToString()

            // try to acquire and release several locks in parallel
            Seq.replicate 25 ()
            |> Seq.map (fun _ -> async {
                let! lockId =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                // sleep for up to 50ms (simulate work being done)
                do! Async.Sleep (int (random.NextDouble() * 50.0))

                return!
                    match lockId with
                    | Ok lockId -> releaseLock client locksTableName Log.Logger lockId
                    | Error err -> async { return Error err }
            })
            |> Async.Parallel
            |> Async.RunSynchronously
            // and make sure there are no errors
            |> Seq.tryFind Result.isError
            |> (Expect.isNone "")

        testCase "should have timeouts adding and releasing retry locks in parallel without enough time" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 5.0
                  WaitForLock = Some { Until = now().AddSeconds 2.0
                                       CheckDelay = TimeSpan.FromMilliseconds 10.0 } }
            let random = Random()
            let key = Guid.NewGuid().ToString()

            // try to acquire and release several locks in parallel
            let errors =
                Seq.replicate 25 ()
                |> Seq.map (fun _ -> async {
                    let! lockId =
                        tryAcquireLock client locksTableName Log.Logger now lockSettings key

                    // sleep for up to 50ms (simulate work being done)
                    do! Async.Sleep (int (random.NextDouble() * 200.0))

                    return!
                        match lockId with
                        | Ok lockId -> releaseLock client locksTableName Log.Logger lockId
                        | Error err -> async { return Error err }
                })
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Seq.choose Result.toErrorOption

            // make sure all the errors are timeouts
            errors
            |> Seq.distinct
            |> Expect.sequenceEqual "" [ WaitForLockExpired ]

            // and there should be quite a lot of them
            errors
            |> Seq.length
            |> fun x -> Expect.isGreaterThan "" (x, 5)

        testCase "should have errors adding and releasing immediate locks in parallel" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 5.0
                  // Note we will not wait for a lock, we will fail immediately
                  // if we can't get one
                  WaitForLock = None }
            let random = Random()
            let key = Guid.NewGuid().ToString()

            // try to acquire and release several locks in parallel
            let errors =
                Seq.replicate 25 ()
                |> Seq.map (fun _ -> async {
                    // wait for a while before trying to acquire a lock
                    do! Async.Sleep (int (random.NextDouble() * 2000.0))

                    let! lockId =
                        tryAcquireLock client locksTableName Log.Logger now lockSettings key

                    // sleep for up to 50ms (simulate work being done)
                    do! Async.Sleep (int (random.NextDouble() * 50.0))

                    return!
                        match lockId with
                        | Ok lockId -> releaseLock client locksTableName Log.Logger lockId
                        | Error err -> async { return Error err }
                })
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Seq.choose Result.toErrorOption

            // make sure all the errors unavailable keys
            errors
            |> Seq.distinct
            |> Expect.sequenceEqual "" [ KeyNotAvailable ]

            // and there should be quite a lot of them
            errors
            |> Seq.length
            |> fun x -> Expect.isGreaterThan "" (x, 5)

        testCase "should prevent lock being taken immediately if already locked" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 5.0
                  // We will give up if we don't take a lock immediately
                  WaitForLock = None }
            let key = Guid.NewGuid().ToString()

            async {
                let! _ =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                // try to acquire the same lock again - it won't be available
                let! lockId2 =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                return lockId2
            }
            |> Async.RunSynchronously
            |> Expect.equal "" (Error LockError.KeyNotAvailable)

        testCase "should prevent lock being taken with retry if still locked" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 2.0
                  // wait for up to 1 second for lock to become available
                  WaitForLock = Some { Until = now().AddSeconds 1.0
                                       CheckDelay = TimeSpan.FromMilliseconds 100.0 } }
            let key = Guid.NewGuid().ToString()

            async {
                let! _ =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                // try to acquire the same lock again - it won't be available within the 1 second
                let! lockId2 =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                return lockId2
            }
            |> Async.RunSynchronously
            |> Expect.equal "" (Error LockError.WaitForLockExpired)

        testCase "should allow lock to be taken immediately after original expires, even if not released" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 1.0
                  // We will give up if we don't take a lock immediately
                  WaitForLock = None }
            let key = Guid.NewGuid().ToString()

            async {
                let! _ =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                do! Async.Sleep 1001

                let! lockId2 =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                return lockId2
            }
            |> Async.RunSynchronously
            |> Expect.isOk ""

        testCase "should allow lock to be taken with retry after original expires, even if not released" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 1.0
                  // wait for up to 1.5 seconds for lock to become available
                  WaitForLock = Some { Until = now().AddSeconds 1.5
                                       CheckDelay = TimeSpan.FromMilliseconds 100.0 } }
            let key = Guid.NewGuid().ToString()

            async {
                let! _ =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                let! lockId2 =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                return lockId2
            }
            |> Async.RunSynchronously
            |> Expect.isOk ""

        testCase "should not release if locked by other" <| fun _ ->
            let client = getClient ()
            let lockSettings =
                { LockExpiry = now().AddSeconds 1.0
                  // wait for up to 1.5 seconds for lock to become available
                  WaitForLock = Some { Until = now().AddSeconds 1.5
                                       CheckDelay = TimeSpan.FromMilliseconds 100.0 } }
            let key = Guid.NewGuid().ToString()

            async {
                let! lockId1 =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                let! _ =
                    tryAcquireLock client locksTableName Log.Logger now lockSettings key

                // Lock 1 won't have the lock any more at this point,
                // it's expired and been taken over by lock 2
                return!
                    Result.get lockId1
                    |> releaseLock client locksTableName Log.Logger
            }
            |> Async.RunSynchronously
            |> Expect.equal "" (Error LockError.LockNotAvailableToRelease)

        testCase "should not release if no lock" <| fun _ ->
            let client = getClient ()

            let lockId =
                { LockKey = Guid.NewGuid().ToString()
                  ClientId = Guid.NewGuid() }

            releaseLock client locksTableName Log.Logger lockId
            |> Async.RunSynchronously
            |> Expect.equal "" (Error LockError.LockNotAvailableToRelease)
    ]
