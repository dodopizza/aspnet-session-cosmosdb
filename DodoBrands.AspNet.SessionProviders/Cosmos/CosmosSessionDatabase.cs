using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;

namespace DodoBrands.CosmosDbSessionProvider.Cosmos
{
    public class CosmosSessionDatabase : ISessionDatabase
    {
        private static readonly TraceSource _trace =
            new TraceSource("DodoBrands.CosmosDbSessionProvider.Cosmos.CosmosDbSessionStateProvider");

        private readonly bool _compressionEnabled;
        private readonly string _connectionString;
        private readonly string _databaseId;

        private readonly HashSet<HttpStatusCode> _goodStatusCodes = new HashSet<HttpStatusCode>
            {HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.OK};

        private readonly object _locker = new object();
        private readonly int _lockTtlSeconds;

        private CosmosClient _client;
        private Container _contents;
        private bool _isInitialized;

        private Container _locks;
        private string _lockSpBody = LoadLockSpBody();
        private string _tryLockSpName;
        private readonly ConsistencyLevel _consistencyLevel;

        public CosmosSessionDatabase(string connectionString, string databaseId, int lockTtlSeconds,
            bool compressionEnabled)
        {
            _databaseId = databaseId;
            _connectionString = connectionString;
            _lockTtlSeconds = lockTtlSeconds;
            _compressionEnabled = compressionEnabled;
            _consistencyLevel = ConsistencyLevel.BoundedStaleness;
        }

        public async Task<(SessionStateValue state, bool isNew)> GetSessionAsync(string sessionId)
        {
            var partitionKey = new PartitionKey(sessionId);
            var itemRequestOptions = new ItemRequestOptions
            {
                ConsistencyLevel = _consistencyLevel,
            };
            try
            {
                var storedState = await _contents.ReadItemAsync<SessionStateRecord>(
                    sessionId, partitionKey, itemRequestOptions);

                TraceRequestCharge(storedState, "GetSessionAsync: ReadItemAsync");

                await ExtendLifespan(storedState.Resource);
                return (storedState.Resource.Payload?.ReadSessionState(storedState.Resource.Compressed),
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

        public async Task Remove(string sessionId)
        {
            try
            {
                var response = await _contents.DeleteItemAsync<SessionStateRecord>(sessionId,
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
                _trace.TraceEvent(TraceEventType.Error, 2, $"Failed to delete content item. Cause: {e.Message}");
            }

            try
            {
                var response = await _locks.DeleteItemAsync<SessionLockRecord>(sessionId,
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
                TraceRequestCharge(e, "Remove: DeleteItemAsync (lock)");
                _trace.TraceEvent(TraceEventType.Error, 2, $"Failed to delete content lock. Cause: {e.Message}");
            }
        }

        public async Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId)
        {
            var now = DateTime.UtcNow;
            var response = await _locks.Scripts.ExecuteStoredProcedureAsync<TryLockResponse>(
                _tryLockSpName, new PartitionKey(sessionId), new dynamic[] {sessionId, now, _lockTtlSeconds});
            TraceRequestCharge(response, $"TryAcquireLock: ExecuteStoredProcedureAsync: {_tryLockSpName}");
            var resource = response.Resource;
            return (resource.Locked, resource.CreatedDate, resource.ETag);
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
        public async Task TryReleaseLock(string sessionId, object lockId)
        {
            try
            {
                var response = await _locks.DeleteItemAsync<SessionLockRecord>(sessionId, new PartitionKey(sessionId),
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
                _trace.TraceEvent(
                    TraceEventType.Warning,
                    3,
                    "Lock no longer exists. It might be an indication that request takes longer to process than the lock ttl in db. Consider fixing the long requests, or extend the lock timespan.");
            }
        }

        public async Task WriteContents(string sessionId, SessionStateValue stateValue,
            bool isNew)
        {
            var now = DateTime.UtcNow;
            var response = await _contents.UpsertItemAsync(new SessionStateRecord
                {
                    SessionId = sessionId,
                    CreatedDate = now,
                    TtlSeconds = stateValue.Timeout * 60,
                    IsNew = isNew ? "yes" : null,
                    Payload = stateValue.Write(_compressionEnabled),
                    Compressed = _compressionEnabled,
                }, new PartitionKey(sessionId),
                new ItemRequestOptions
                {
                    ConsistencyLevel = _consistencyLevel,
                    EnableContentResponseOnWrite = false,
                });
            TraceRequestCharge(response, $"WriteContents: UpsertItemAsync, isNew: {isNew}");
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

        private async Task ExtendLifespan(SessionStateRecord state)
        {
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
                var response = await _contents.ReplaceItemAsync(state, state.SessionId,
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
                _trace.TraceEvent(TraceEventType.Error, 1, "Failed to extend timeout. " + e.Message);
            }
        }

        private async Task InitializeAsync()
        {
            _client = new CosmosClient(_connectionString, new CosmosClientOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(_lockTtlSeconds / 2),
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(_lockTtlSeconds / 2),
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
                new ContainerProperties("locks", "/id")
                {
                    IndexingPolicy = indexNone,
                    DefaultTimeToLive = 300,
                }));

            CheckValidResponse(await db.CreateContainerIfNotExistsAsync(
                new ContainerProperties("contents", "/id")
                {
                    IndexingPolicy = indexNone,
                    DefaultTimeToLive = 300,
                }));

            _locks = db.GetContainer("locks");
            _contents = db.GetContainer("contents");

            await CreateLockSp();
        }

        private static string LoadLockSpBody()
        {
            return new StreamReader(typeof(CosmosSessionDatabase)
                                        .Assembly
                                        .GetManifestResourceStream(
                                            "DodoBrands.CosmosDbSessionProvider.Cosmos.tryLock.js") ??
                                    throw new InvalidOperationException("Failed to load resource file."))
                .ReadToEnd();
        }

        private async Task CreateLockSp()
        {
            var spHash = HashUtil.CreateHashInHex(_lockSpBody, 10);
            var spName = $"tryLock_{spHash}";
            _tryLockSpName = spName;
            try
            {
                await _locks.Scripts.ReadStoredProcedureAsync(_tryLockSpName);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                await _locks.Scripts.CreateStoredProcedureAsync(
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
            _trace.TraceEvent(TraceEventType.Verbose, 0, $"{what}. Request Units spent: {response.RequestCharge}");
        }

        private void TraceRequestCharge(CosmosException exception, string what)
        {
            _trace.TraceEvent(TraceEventType.Verbose, 0,
                $"CosmosException, {what}. Request Units spent: {exception.RequestCharge}");
        }
    }
}