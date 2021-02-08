# Azure Cosmos DB ASP.NET Session State Provider

Azure Cosmos DB is a highly scalable database with strong SLA guarantees.
This is an alternative session state provider with Cosmos DB as a backend storage.
It is suitable for applications demanding high scalability and low latency.

This implementation scores few benefits over the oficial implementation:

* Lowered request unit consumption
* GC friendly, making use of RecyclableMemoryStreams
* Simpler code

Last but not least comes the fact that it does not have known issues (right yet).

## Motivation

ASP.NET Session State for Mvc applications requires a reliable locking mechanism.
Usually it is implemented using an ACID database, such as SQL Server or a NoSql key value store, such as Redis, but those backends have their constraints. Redis does not offer scalability out of the box and in addition Redis trades availability for durability. Sql Server does not offer scalability in writes.

Azure Cosmos DB offers best from both worlds. It provides guaranteed low latency and scalability at scale.

## Getting started

You might wish to take a look at the sample application for the details on configuration.

You need both module registration and provider registration sections in your config file.

## Configuration

```XML
<connectionStrings>
    <add name="cosmosSessionConnectionString" connectionString="Env:COSMOS_CONNECTION_STRING" />
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
    ...
    <sessionState cookieless="false" regenerateExpiredSessionId="false" mode="Custom" customProvider="myProvider"
                    timeout="20" compressionEnabled="true">
        <providers>
            <add name="myProvider" type="DodoBrands.AspNet.SessionProviders.Cosmos.CosmosDbSessionStateProvider"
                    xLockTtlSeconds="10" databaseId="testdb"
                    connectionStringName="cosmosSessionConnectionString" />
        </providers>
    </sessionState>
</system.web>
```

Notice the `Env:COSMOS_CONNECTION_STRING` construct. It allows to specify the connection string in environment variable.

Another possibility is to specify the connection string directly in the provider registration.

```XML
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
    ...
    <sessionState cookieless="false" regenerateExpiredSessionId="false" mode="Custom" customProvider="myProvider"
                    timeout="20" compressionEnabled="true">
        <providers>
            <add name="myProvider" type="DodoBrands.AspNet.SessionProviders.Cosmos.CosmosDbSessionStateProvider"
                    xLockTtlSeconds="10" databaseId="testdb"
                    connectionStringName="cosmosSessionConnectionString" />
        </providers>
    </sessionState>
</system.web>
```

In future versions, a possibility to specify a connection string separately in the connectionStrings section might be added.

### Parameters

`databaseId` specifies the name of the database in CosmosDB which you are going to be using got sessions. The provider will create two containers in this database on first execution: `locks` and `contents`. You should not use the database for any other purpose.

`xLockTtlSeconds` - specifies the TTL for the locks in seconds. Basically it is a tradeoff between consistency and availability. The time period in seconds specified by this parameter should be long enough to encompass any possible request duration that requires session write consistency.

> **WARNING!**
If this TTL values is too short, longer requests might experience inconsistency in session writes. The risk here is that one request might overwrite session contents out of turn and user session data might be lost this way.
On the other hand, if TTL is too long, session might be stuck in locked state after an application crash, so choose long `xLockTtlSeconds` parameter only if your application is stable enough.

`timeout` specifies sliding expiration of session in minutes.

`compressionEnabled` if set to true, session contents will be GZip-compressed, saving request units on storage. Use wisely, as it unilizes more CPU. This parameter can be changed between application restarts, because this flag is stored with every session content item in the database, so the engine knows to decode the contents if it was compressed before and.

`DodoBrands.CosmosDbSessionProvider.Cosmos.CosmosDbSessionStateProvider` name of the trace source which can be used for tracing.
Here is an example of tacing configuration which writes to the console. This configuration is useful for debugging:

```XML
<system.diagnostics>
    <trace autoflush="true"></trace>
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
```

## Design
This implementation uses alternative architecture. It uses separate collections for locks and content in two containers: `locks`, `content`. Such a separation is benefitial in terms of storage, network traffic and request units.

### Locks container
Locks container stores minimal records to provide distributed locking semantics for the session. Taking a lock involves creation of an item with session key as unique ID, so the other servers in the farm can not create a lock item with the same ID until the lock record is not deleted.

Items in this container are deleted as soon as the lock is released. In addition, Cosmos DB engine is configured to automaticall delete lock items upon short TTL expiration in case an app fails to delete the lock item when it releases the lock. This could happen for example when the application server goes down.

### Contents container
Contens container stores session content itself. The data can only be stored by a web application after a lock is taken, so the content in effect is protected by a distributed lock.
Items in the contents container need to be renewed, so they are not expired when a user uses an item, but does not update the item (aka read only session). In this case the provider needs to overwrite a record with the same content by the same id but with extended lifetime, so the item is not deleted automatically any time soon an item was accessed. This is an expensive operation, and our provider implementation uses a period, that is shorter than the session expiration TTL to update the item. This allows us to use an expensive write operation for content lifetime extension fewer times than the item is being accessed.

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
* delete lock

Browser request 3, 4, 5 (intention to just read with no locking):
* read content (0)
* ...
* time t1
* ...
* read content (1)
* ...
* time t2
* ...
* read content (2)

### Implementation Details

Provider is Implemented in asynchronous manner thanks to the https://github.com/aspnet/AspNetSessionState asynchronous model.

Uses RecyclableMemoryStreams thanks to the https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream making it suitable for highly loaded scenario with high request rate and lowered memory consumpltion, avoiding LOH allocations.

Uses GZip compression for maximum economy of request units in Azure Cosmos DB, even if your session objects are large.

## Contributing

Contributions are welcome, you can start by creating an issue on GitHub and opening a pull request after discussing the issue.
