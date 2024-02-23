# Azure Cosmos DB ASP.NET Session State Provider

Azure Cosmos DB is a highly scalable database with strong SLA guarantees.
This is an alternative session state provider with Cosmos DB as a backend storage.
It is suitable for applications demanding high scalability and low latency.

This implementation scores few benefits over the oficial implementation:

* Lowered request unit consumption
* GC friendly, making use of RecyclableMemoryStreams
* Simpler code

## Motivation

ASP.NET Session State for Mvc applications requires a reliable locking mechanism.
Usually it is implemented using an ACID database, such as SQL Server or a NoSql key value store, such as Redis, but those backends have their constraints. Redis does not offer scalability out of the box and in addition Redis trades availability for durability. Sql Server does not offer scalability in writes.

Azure Cosmos DB offers best from both worlds. It provides guaranteed low latency and scalability at scale.

## Getting started

You might wish to take a look at the sample application for the details on configuration.

You need both module registration and provider registration sections in your config file.

## Configuration

Following configuration snippet specifies connection string in `connectionStrings` section.

```XML
<config>
    <connectionStrings>
        <add name="cosmosSessionConnectionString" connectionString="AccountEndpoint=https://mycosmosaccount.documents.azure.com:443;AccountKey=**********************************==" />
    </connectionStrings>
    <system.webServer>
            <modules>
                <remove name="Session" />
                <add name="Session"
                     type="Microsoft.AspNet.SessionState.SessionStateModuleAsync, Microsoft.AspNet.SessionState.SessionStateModule, Version=1.1.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"
                     preCondition="integratedMode" />
            </modules>
        </system.webServer>
    <system.web>
        <sessionState
                cookieless="false"
                regenerateExpiredSessionId="false"
                mode="Custom"
                customProvider="myProvider"
                timeout="27">
            <providers>
                <add
                        name="myProvider"
                        type="Dodo.AspNet.SessionProviders.CosmosDb.CosmosDbSessionStateProvider"
                        xLockTtlSeconds="30"
                        databaseId="testdb"
                        compressionEnabled="true"
                        connectionString="AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" />
            </providers>
        </sessionState>
    </system.web>
</config>
```

### Parameters

`customProvider`
-
The value specified in the `sessionState` element must not be empty and must match that of the `name` property in the `add` element for the provider.

`databaseId`
-
Specifies the name of the database in CosmosDB which you are going to be using got sessions.
The provider will create `SessionStore` container in this database on first execution.

`xLockTtlSeconds`
-
Specifies the TTL for the locks in seconds. Basically it is a tradeoff between consistency and error resilience. The time period in seconds specified by this parameter should be long enough to encompass any possible request duration that requires session write consistency.

> :mega: **WARNING!** If this TTL values is too short, longer requests might experience inconsistency in session writes. The risk here is that one request might overwrite session contents out of turn and user session data might be lost this way.
On the other hand, if TTL is too long, session might be stuck in locked state after an application crash, so choose long `xLockTtlSeconds` parameter only if your application is stable enough.

`timeout`
-
Specifies TTL of session in minutes.
Sliding expiration will not be extended until 25% of the TTL has been passed by the time of the request.
This is done to reduce the number of expensive write operations in the database.
So, if you need your session to last at least some value, multiply it by 4/3. For example, if you need your session to last *at least* 20 minutes, you need to specify 20 * 4 / 3 = 27 minutes. This way, session is guaranteed to last for 20 minutes, but it can also live as long as 27 minutes after the last request, depending on the actual request timings.

> :mega: **WARNING!** Effective value of the sliding expiration will be anywhere between 75% and 100% of the specified timeout value.

`compressionEnabled`
-
Default is `true`. When set to true, session contents will be GZip-compressed, saving request units on storage. Use wisely, as it unilizes more CPU. This parameter can be changed between application restarts, because this flag is stored with every session content item in the database, so the engine knows to decode the contents if it was compressed before and.

`consistencyLevel`
-
specifies Azure Cosmos DB consistency level used for operations.
One of the levels specified here: https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.cosmos.consistencylevel?view=azure-dotnet
* `Strong` (default) should be used in multi-region setup.
* `BoundedStaleness` can be used in a single-region setup, it provides the same guarantees in single region.
* `Session` :biohazard: this consistency level can only be used in a test environment when using Cosmos DB Emulator.
> :biohazard: **WARNING!** Don't copy-paste `Session` consistency level from sample application config file, it is not suitable for production. 

See also: https://docs.microsoft.com/en-us/azure/cosmos-db/consistency-levels

`DodoBrands.CosmosDbSessionProvider.Cosmos.CosmosDbSessionStateProvider` name of the trace source which can be used for tracing.
Here is an example of tacing configuration which writes to the console. This configuration is useful for debugging:

```XML
<config>
    <system.diagnostics>
        <trace autoflush="true" />
        <sources>
            <source name="DodoBrands.CosmosDbSessionProvider.Cosmos.CosmosDbSessionStateProvider"
                    switchName="sourceSwitch" switchType="System.Diagnostics.SourceSwitch">
                <listeners>
                    <add name="console" type="System.Diagnostics.ConsoleTraceListener">
                    </add>
                </listeners>
            </source>
        </sources>
        <switches>
            <add name="sourceSwitch" value="All" />
        </switches>
    </system.diagnostics>
</config>
```

## Design
This implementation uses separate items for locks and session content. Such separation allows to optimize writes when locking.

### Lock item
Lock item stores minimal records to provide distributed locking semantics for the session.
Taking a lock involves creation of an item with session key as unique ID,
so the other servers in the farm can not create a lock item with the same ID until the lock record is not deleted.

Lock items are deleted as soon as the lock is released. In addition, Cosmos DB engine is configured to automaticall delete lock items upon short TTL expiration in case an app fails to delete the lock item when it releases the lock. This could happen for example when the application server goes down.

### Contents item
Contents item stores session contents itself.
The data can only be stored by a web application after a lock is taken,
so the content in effect is protected by a distributed lock.
Content items need to be renewed, so they are not expired when a user uses an item,
but does not update the item (aka read only session).
In this case the provider needs to overwrite a record with the same content by the same id but with extended lifetime,
so the item is not deleted automatically any time soon an item was accessed.
This is an expensive operation, and our provider implementation uses a time period
that is shorter than the session expiration TTL to update the item.
This strategy allows us to use an expensive write operation for content lifetime extension fewer times.

## Locking scenario

Two browser requests try to read and write with locking:

Browser request 1 (intention to lock and write):
* time t1
* try to create lock (success)
* read content (0),
* store new content (1),
* delete lock
* time t2

Browser request 2 (intention to lock and write):
* time t1 + dt
* try to create lock (conflict)
* ...
* try to create lock (conflict)
* time t2
* try to create lock (success)
* read content (1)
* store content (2)
* time t3
* delete lock


Browser request 3, 4, 5 (intention to just read with no locking):
* read content (0)
* ...
* time t2
* read content (1)
* ...
* time t3
* read content (2)

### Implementation Details

Provider is Implemented in asynchronous manner thanks to the https://github.com/aspnet/AspNetSessionState asynchronous model.

Uses RecyclableMemoryStreams thanks to the https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream making it suitable for highly loaded scenario with high request rate and lowered memory consumpltion, avoiding LOH allocations.

Uses GZip compression for maximum economy of request units in Azure Cosmos DB, even if your session objects are large.

## Local development

The sample app and tests are configured with Cosmos DB Emulator connection string.
You might wish to install the emulator if you are planning 
https://docs.microsoft.com/en-us/azure/cosmos-db/local-emulator?tabs=cli%2Cssl-netstd21

## Contributing

Contributions are welcome, you can start by creating an issue on GitHub and opening a pull request after discussing the issue.
