# FSharpDynamoDbLock

An F# library for distributed locks via AWS DynamoDB.

This library has been in use at TotallyMoney for several years. We have 5
million+ customers, and this code is hit across multiple microservices. We're
fairly confident in it's capabilities.

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