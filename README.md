# FSharpDynamoDbLock

An F# library for distributed locks via AWS DynamoDB.

This library has been in use at TotallyMoney for several years. We have 5
million+ customers, and this code is hit across multiple microservices. We're
fairly confident in it's capabilities.

## Example

Below is an example of what the acquiring and releasing a lock might look like in practice, along with using `FsToolkit.ErrorHandling`'s `asyncResult` computation expression.

```fsharp
open FsToolkit.ErrorHandling

// Setup
let now = (fun () -> DateTimeOffset.Now)

let lockSettings =
    { LockExpiry = now().AddSeconds 20.0
      WaitForLock =
        Some { Until = now().AddSeconds 18.0
               CheckDelay = TimeSpan.FromMilliseconds 20.0 } }

let dynamoClient = new AmazonDynamoDBClient(RegionEndpoint.EUWest1)

// Partially apply functions for easier use
let tryAcquireLock =
    tryAcquireLock dynamoClient "LocksDynamoDBTable" now lockSettings
    
let releaseLock = releaseLock dynamoClient "LocksDynamoDBTable"

// call myImportantCode, via a distributed lock
asyncResult {
    let! lockId = tryAcquireLock "myCustomerId"
    try
        let! result = myImportantCode ()
        do! releaseLock lockId
        return result
    with ex ->
        do! releaseLock lockId
        reraise ()
}

```


# Install/Setup

The easiest way to use this is to copy + paste the `src/FSharpDynamoDbLock.fs`
file into your solution. There's currently no nuget package.

Before running the code for real you will first need to create a dynamodb table
to store the lock data. See below.

The `AttributeDefinitions`, `KeySchema` and `TimeToLiveSpecification` needs to
remain the same, but feel free to change anything else. 

```
LocksDynamoDBTable:
  Type: AWS::DynamoDB::Table
  DeletionPolicy: Retain
  Properties:
    TableName: MyLocksTable
    BillingMode: PAY_PER_REQUEST
    AttributeDefinitions:
      - AttributeName: LockKey
        AttributeType: S
    KeySchema:
      - AttributeName: LockKey
        KeyType: HASH
    TimeToLiveSpecification:
      AttributeName: ExpireInUnixSeconds
      Enabled: True
```

## Usage

Call `tryAcquireLock` to take a distributed lock. 

The parameters are:
* `client`: with a `AmazonDynamoDBClient` that has already been setup with the correct configuration, credentials and region
* `tableName`: the name of the dynamo db to store the lock. See above for how this table should be defined.
* `now`: A thunk that should return the current time. i.e. `(fun _ -> DateTimeOffest.Now)`
* `lockSettings`: of type `LockSettings` described further down.
* `key`: the "id" you give lock, so only one caller can do something with it at a time. Often this will be something like a _CustomerId_ so no other process can acquire a lock for the same customer while you're performing an operation with it.

Note: `tryAcquireLock` is a async function using F#'s Async type, so you'll need to resolve the Async before the lock will take effect.

This will return a `LockId` (`Async<Result<LockId,LockError>>` to be specific) which you can use to release the lock once you've finished with it.

The `LockId` is made up of two parts, the `key` that you provided earlier, and a random guid `clientId`. The clientId is used to identify who took out the lock. You shouldn't need to deal with it in your code.

If a lock cannot be acquired, it will return a `LockError` DU, with one of 5 different types of errors.
`WaitForLockExpired` is the most standard response and indicates someone else is still holding the lock. The other errors are more infrastructure failures.

This function has quite a few parameters, but in practice you should _partially apply_ all except the `key` before using it.

### Release Lock

Once you've finished with the lock it's critical that you call `releaseLock` to indicate that the client has finished with the lock, and something else can take it.

The parameters are:
* `client`: a valid `AmazonDynamoDBClient` to access the lock table.
* `tableName`: the same table you called `tryAcquireLock` with.
* `lockId`: the `LockId` that `tryAcquireLock` returned.
 
Note: just like `tryAcquireLock`, `releaseLockz is a async function using F#'s Async type, so you'll need to resolve the Async before the lock will take effect.

If you don't call `releaseLock` a client will have to wait around until the lock expires before they can get that resource.

Because it's important that this is always called, you should wrap any work done after calling `tryAcquireLock` with error handling, so that the lock can still be release in the case of an exception or error.

### LockSettings

This library is fairly simple, but there are a few things that can be configured.

The minimal option is to only set the LockExpiry time. i.e.

```fsharp
{ LockExpiry = DateTimeOffset.Now.AddSeconds 20.0
  WaitForLock = None }
```

This will create a lock that will timeout in 20 seconds, but if the lock is already taken it will give up and return immediately.

Note: This library doesn't do anything complex like keep-alives, so you only have a limited time to perform your actions before the lock will be released automatically. It's important to set a good expiry time that will last long enough that to complete your operations. But not so long that a disconnected or aborted client will halt your system when it can't send a lock release .

The `WaitForLock` setting will cause the `tryAcquireLock` request to keep reattempting to acquire the lock if it's not immediately available. e.g.

```fsharp
{ LockExpiry = DateTimeOffset.Now.AddSeconds 20.0
  WaitForLock =
    Some { Until = DateTimeOffset.Now.AddSeconds 18.0
           CheckDelay = TimeSpan.FromMilliseconds 20.0 } }
```  

Looking first at `CheckDelay`, this is how often we should wait before attempting to acquire the lock if we can't acquire it the first time. From the example above this does NOT mean we'll send a request every 100ms. There's the overhead and round trip time of each request which will add on top of this delay. The 100ms in the example an absolute minimum.

The number you want to set this to is going to be dependent on how time sensitive your operation is, how much you want to spend on Dynamo processing costs, and how likley it is to be locked. We regularly use `20ms` in production, as we don't expect to hit locks very often, and we don't want to wait long. I wouldn't suggest going over 100ms unless you expect very long lock times (greater than 60 seconds).

The other setting, `Until` is a limit to how long `tryAcquireLock` will keep attempting to get a lock. There is no queueing mechanism with this library, so if a lock has a lot of contention it could be continuously acquired by other clients while we're trying to acquire it. It's not a good design to allow a process to get stuck indefinitely, we could just build up more and more contention behind the one lock. So it should fail after a while.

For our usages we keep this fairly low since we're using lambdas with their own short timeouts. In our case it's better to give the lambda time to handle the issue than to wait until the lambda itself times out and hold up the rest of the system.


## Development

Run `dotnet tool restore && dotnet paket restore` to setup the solution.

## Testing

Testing requires docker to create a local copy of Dynamo using aws's
`amazon/dynamodb-local` image.

Call `docker compose run integration_tests` to run the tests inside docker.

Or call `docker compose up -d init-dynamo` to setup dynamo inside a container,
then run `dotnet run --project tests` to run the tests locally.


## License
The MIT License (MIT)

Copyright (c) 2024 TotallyMoney

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.