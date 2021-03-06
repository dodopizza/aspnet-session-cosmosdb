using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;

namespace Dodo.AspNet.SessionProviders.CosmosDb
{
    internal class CosmosSessionDatabase : ISessionDatabase
    {
        private const string LifetimeExtensionItemKey =
            "Dodo.AspNet.SessionProviders.CosmosDb.CosmosSessionDatabase.LifetimeExtensionItemKey";

        private static readonly TraceSource Trace =
            new TraceSource("Dodo.AspNet.SessionProviders.CosmosDb");

        internal static TraceSource TraceSource => Trace;

        private readonly bool _compressionEnabled;
        private readonly string _connectionString;
        private readonly string _databaseId;

        private readonly HashSet<HttpStatusCode> _goodStatusCodes = new HashSet<HttpStatusCode>
            {HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.OK};

        private readonly object _locker = new object();
        private readonly int _lockTtlSeconds;

        private CosmosClient _client;
        private bool _isInitialized;

        private Container _container;

        private string _lockSpBody = LoadLockSpBody();
        private string _tryLockSpName;
        private readonly ConsistencyLevel _consistencyLevel;

        public CosmosSessionDatabase(string connectionString, string databaseId, int lockTtlSeconds,
            bool compressionEnabled, ConsistencyLevel consistencyLevel)
        {
            _databaseId = databaseId;
            _connectionString = connectionString;
            _lockTtlSeconds = lockTtlSeconds;
            _compressionEnabled = compressionEnabled;
            _consistencyLevel = consistencyLevel;
        }

        public async Task<(SessionStateValue state, bool isNew)> GetSessionAsync(HttpContextBase context,
            string sessionId)
        {
            var partitionKey = new PartitionKey(sessionId);
            var itemRequestOptions = new ItemRequestOptions
            {
                ConsistencyLevel = _consistencyLevel,
            };
            try
            {
                var storedState = await _container.ReadItemAsync<SessionStateRecord>(
                    sessionId, partitionKey, itemRequestOptions);

                TraceRequestCharge(storedState, "GetSessionAsync: ReadItemAsync");

                RegisterForLifespanExtension(context, storedState.Resource);

                var resourcePayload = storedState.Resource.Payload;

                var compressionSw = Stopwatch.StartNew();
                var sessionStateValue = resourcePayload?.ReadSessionState(storedState.Resource.Compressed);
                compressionSw.Stop();

                Trace.TraceEvent(TraceEventType.Verbose, 4,
                    $"{DateTime.UtcNow.ToString("o")} GetSessionAsync: Deserialize. Size: {resourcePayload?.Length}, Elapsed: {compressionSw.Elapsed.TotalSeconds.ToString()}");

                return (sessionStateValue,
                    storedState.Resource.IsNew == "yes");
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                TraceRequestCharge(e, "GetSessionAsync: ReadItemAsync");
                return (null, false);
            }
            catch (CosmosException e)
            {
                throw new Exception($"GetSessionAsync. Bad status code returned: {e.StatusCode}", e);
            }
        }

        private void RegisterForLifespanExtension(HttpContextBase context,
            SessionStateRecord storedStateResource)
        {
            context.Items[LifetimeExtensionItemKey] = storedStateResource;
        }

        private void DeregisterLifetimeExtension(HttpContextBase context)
        {
            context.Items.Remove(LifetimeExtensionItemKey);
        }

        private SessionStateRecord TryGetLifetimeExtensionItem(HttpContextBase context)
        {
            if (!context.Items.Contains(LifetimeExtensionItemKey))
            {
                return null;
            }

            return (SessionStateRecord) context.Items[LifetimeExtensionItemKey];
        }

        public async Task Remove(HttpContextBase context, string sessionId)
        {
            DeregisterLifetimeExtension(context);
            try
            {
                var response = await _container.DeleteItemAsync<SessionStateRecord>(sessionId,
                    new PartitionKey(sessionId),
                    new ItemRequestOptions
                    {
                        ConsistencyLevel = _consistencyLevel,
                        EnableContentResponseOnWrite = false,
                    });

                TraceRequestCharge(response, "Remove: DeleteItemAsync");
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                TraceRequestCharge(e, "Remove: DeleteItemAsync (content)");
                Trace.TraceEvent(TraceEventType.Error, 2, $"Failed to delete content item. Cause: {e.Message}");
            }

            try
            {
                var response = await _container.DeleteItemAsync<SessionLockRecord>(MakeLockKey(sessionId),
                    new PartitionKey(MakeLockKey(sessionId)),
                    new ItemRequestOptions
                    {
                        ConsistencyLevel = _consistencyLevel,
                        EnableContentResponseOnWrite = false,
                    });

                TraceRequestCharge(response, "Remove: DeleteItemAsync");
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                TraceRequestCharge(e, "Remove: DeleteItemAsync (lock)");
                Trace.TraceEvent(TraceEventType.Error, 2, $"Failed to delete content lock. Cause: {e.Message}");
            }
        }

        public async Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId)
        {
            try
            {
                var optimisticTry = await AcquireLockOptimistic();
                return optimisticTry;
            }
            catch (CosmosException optimisticFailedException)
                when (IsConflictPointWrite(optimisticFailedException))
            {
                var numberOfRetries = 3;

                var r = new Random(Guid.NewGuid().GetHashCode());

                for (var i = 0; i < numberOfRetries; i++)
                {
                    try
                    {
                        return await AcquireLockPessimistic(i);
                    }
                    catch (CosmosException pessimisticFailedException)
                        when (IsConflictStoredProcedure(pessimisticFailedException))
                    {
                        Trace.TraceEvent(TraceEventType.Warning, 6,
                            $"Retryable exception. Iteration {i} of {numberOfRetries + 1}. Will retry...");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(r.Next(10, 50)));
                }

                return await AcquireLockPessimistic(numberOfRetries);
            }

            bool IsConflictPointWrite(CosmosException cosmosException)
            {
                return cosmosException.StatusCode == HttpStatusCode.Conflict;
            }

            bool IsConflictStoredProcedure(CosmosException e)
            {
                if (e.StatusCode == HttpStatusCode.BadRequest && e.SubStatusCode == (int) HttpStatusCode.Conflict)
                {
                    return true;
                }

                // Microsoft.Azure.Cosmos.CosmosException : Response status code does not indicate success: 449 (449); Substatus: 0; ActivityId: ***; Reason: (Message: {"Errors":["Conflicting request to resource has been attempted. Retry to avoid conflicts."]}
                if ((int) e.StatusCode == 449 && e.SubStatusCode == 0)
                {
                    return true;
                }

                return false;
            }

            async Task<(bool lockTaken, DateTime lockDate, object lockId)> AcquireLockOptimistic()
            {
                var now = DateTime.UtcNow;
                var response = await _container.CreateItemAsync(new SessionLockRecord
                    {
                        LockId = MakeLockKey(sessionId),
                        CreatedDate = now,
                        TtlSeconds = _lockTtlSeconds,
                    }, new PartitionKey(MakeLockKey(sessionId)),
                    new ItemRequestOptions
                    {
                        ConsistencyLevel = _consistencyLevel,
                        EnableContentResponseOnWrite = true
                    });
                TraceRequestCharge(response, $"TryAcquireLock: Optimistic: CreateItemAsync: {_tryLockSpName}");
                var resource = response.Resource;
                return (true, now, resource.ETag);
            }

            async Task<(bool lockTaken, DateTime lockDate, object lockId)> AcquireLockPessimistic(int iteration)
            {
                var now = DateTime.UtcNow;
                var response = await _container.Scripts.ExecuteStoredProcedureAsync<TryLockResponse>(
                    _tryLockSpName, new PartitionKey(MakeLockKey(sessionId)),
                    new dynamic[] {MakeLockKey(sessionId), now, _lockTtlSeconds});
                TraceRequestCharge(response,
                    $"TryAcquireLock: Pessimistic: ExecuteStoredProcedureAsync: {_tryLockSpName}, Iteration: {iteration}");
                var resource = response.Resource;
                return (resource.Locked, resource.CreatedDate, resource.ETag);
            }
        }

        /// <summary>
        /// TryReleaseLock releases the lock by deleting the record in db.
        /// </summary>
        /// <remarks>
        /// In case the lock hard-expires by the ttl in db, you might see the "Lock no longer exists" warnings in the trace.
        /// Lock hard-expiration is important to avoid locking the session forever,
        /// however consistency of session write operations might be compromised.
        /// If you see the "Lock no longer exists" warnings, consider extending the lock timeouts or fixing the long requests. 
        /// </remarks>
        public Task TryReleaseLock(string sessionId, object lockId)
        {
            async Task ReleaseLockFireAndForget()
            {
                try
                {
                    var response = await _container.DeleteItemAsync<SessionLockRecord>(MakeLockKey(sessionId),
                        new PartitionKey(MakeLockKey(sessionId)),
                        new ItemRequestOptions
                        {
                            ConsistencyLevel = _consistencyLevel,
                            EnableContentResponseOnWrite = false,
                            IfMatchEtag = (string) lockId,
                        });
                    TraceRequestCharge(response, "TryReleaseLock: DeleteItemAsync");
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    TraceRequestCharge(e, "TryReleaseLock: DeleteItemAsync");
                    Trace.TraceEvent(
                        TraceEventType.Warning,
                        3,
                        "Lock no longer exists. It might be an indication that request takes longer to process than the lock ttl in db. Consider fixing the long requests, or extend the lock timespan.");
                }
                catch (CosmosException e)
                {
                    TraceRequestCharge(e, "TryReleaseLock: DeleteItemAsync");
                    Trace.TraceEvent(
                        TraceEventType.Error,
                        3,
                        $"Fatal error releasing the lock {MakeLockKey(sessionId)}. Unidentified exception: {e}");
                }
                catch (Exception e)
                {
                    Trace.TraceEvent(
                        TraceEventType.Error,
                        3,
                        $"Fatal error releasing the lock {MakeLockKey(sessionId)}. Unidentified exception: {e}");
                }
            }

            if (HostingEnvironment.IsHosted)
            {
                HostingEnvironment.QueueBackgroundWorkItem(ct => ReleaseLockFireAndForget());
            }
            else
            {
                _ = ReleaseLockFireAndForget();
            }

            return Task.CompletedTask;
        }

        public async Task WriteContents(HttpContextBase context, string sessionId, SessionStateValue stateValue,
            bool isNew)
        {
            DeregisterLifetimeExtension(context);
            var now = DateTime.UtcNow;

            var compressionSw = Stopwatch.StartNew();
            var payload = stateValue.Write(_compressionEnabled);
            compressionSw.Stop();
            Trace.TraceEvent(TraceEventType.Verbose, 4,
                $"{now.ToString("o")} WriteContents: Serialize. Size: {payload.Length}, Elapsed: {compressionSw.Elapsed.TotalSeconds.ToString()}");

            var response = await _container.UpsertItemAsync(new SessionStateRecord
                {
                    SessionId = sessionId,
                    CreatedDate = now,
                    TtlSeconds = stateValue.Timeout * 60,
                    IsNew = isNew ? "yes" : null,
                    Payload = payload,
                    Compressed = _compressionEnabled,
                }, new PartitionKey(sessionId),
                new ItemRequestOptions
                {
                    ConsistencyLevel = _consistencyLevel,
                    EnableContentResponseOnWrite = false,
                });
            TraceRequestCharge(response, $"WriteContents: UpsertItemAsync. isNew: {isNew}");
        }

        public void Initialize()
        {
            lock (_locker)
            {
                if (_isInitialized)
                {
                    return;
                }

                _isInitialized = true;
            }

            InitializeAsync().Wait();
        }

        public async Task ExtendLifetime(HttpContextBase context)
        {
            var state = TryGetLifetimeExtensionItem(context);
            if (state == null)
            {
                return;
            }

            var now = DateTime.UtcNow;

            var proximityFactor = 1.0 / 3.0;

            var secondsBeforeExpiration = (state.CreatedDate - now + TimeSpan.FromSeconds(state.TtlSeconds))
                .TotalSeconds;

            var toleratedSecondsBeforeExpiration = state.TtlSeconds * (1.0 - proximityFactor);

            if (secondsBeforeExpiration > toleratedSecondsBeforeExpiration)
            {
                return;
            }

            try
            {
                state.CreatedDate = now;
                var response = await _container.ReplaceItemAsync(state, state.SessionId,
                    new PartitionKey(state.SessionId),
                    new ItemRequestOptions
                    {
                        ConsistencyLevel = ConsistencyLevel.Eventual,
                        IfMatchEtag = state.ETag,
                        EnableContentResponseOnWrite = false,
                    });
                TraceRequestCharge(response, "ExtendLifespan: ReplaceItemAsync");
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                TraceRequestCharge(e, "ExtendLifespan: ReplaceItemAsync");
                Trace.TraceEvent(TraceEventType.Error, 1, "Failed to extend timeout. " + e.Message);
            }
        }

        private async Task InitializeAsync()
        {
            _client = new CosmosClient(_connectionString, new CosmosClientOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(_lockTtlSeconds / 2.0),
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(_lockTtlSeconds / 2.0),
            });

            CheckValidResponse(await _client.CreateDatabaseIfNotExistsAsync(_databaseId));
            var db = _client.GetDatabase(_databaseId);
            var indexNone = new IndexingPolicy
            {
                Automatic = false,
                IndexingMode = IndexingMode.Consistent,
                ExcludedPaths = {new ExcludedPath {Path = "/*"}},
            };

            CheckValidResponse(await db.CreateContainerIfNotExistsAsync(
                new ContainerProperties("SessionStore", "/id")
                {
                    IndexingPolicy = indexNone,
                    DefaultTimeToLive = 300,
                }));

            _container = db.GetContainer("SessionStore");

            await CreateLockSp();
        }

        private static string LoadLockSpBody()
        {
            using (var manifestResourceStream = typeof(CosmosSessionDatabase)
                .Assembly
                .GetManifestResourceStream(
                    "Dodo.AspNet.SessionProviders.CosmosDb.tryLock.js"))
            {
                if (manifestResourceStream == null)
                {
                    throw new InvalidOperationException("Failed to load resource file.");
                }

                using (var sr = new StreamReader(manifestResourceStream))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        private async Task CreateLockSp()
        {
            var spHash = HashUtil.CreateHashInHex(_lockSpBody, 10);
            var spName = $"tryLock_{spHash}";
            _tryLockSpName = spName;
            try
            {
                await _container.Scripts.ReadStoredProcedureAsync(_tryLockSpName);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                await _container.Scripts.CreateStoredProcedureAsync(
                    new StoredProcedureProperties(_tryLockSpName, _lockSpBody));
            }

            _lockSpBody = null;
        }

        private void CheckValidResponse<T>(Response<T> response)
        {
            if (_goodStatusCodes.Contains(response.StatusCode))
            {
                return;
            }

            throw new Exception(response.Diagnostics.ToString());
        }

        private void TraceRequestCharge<T>(Response<T> response, string what)
        {
            var now = DateTime.UtcNow;
            Trace.TraceEvent(TraceEventType.Verbose, 0,
                $"{now.ToString("o")} {what}. RU: {response.RequestCharge}. HTTP: {response.StatusCode}. Client Elapsed: {response.Diagnostics.GetClientElapsedTime().TotalSeconds.ToString()}");
        }

        private void TraceRequestCharge(CosmosException exception, string what)
        {
            var now = DateTime.UtcNow;
            Trace.TraceEvent(TraceEventType.Verbose, 0,
                $"{now.ToString("o")} {what}. RU: {exception.RequestCharge}. HTTP: {exception.StatusCode}. Exp: {exception.Message}. Client Elapsed: {exception.Diagnostics.GetClientElapsedTime().TotalSeconds.ToString()}");
        }

        private static string MakeLockKey(string sessionId)
        {
            return sessionId + "_lock";
        }
    }
}