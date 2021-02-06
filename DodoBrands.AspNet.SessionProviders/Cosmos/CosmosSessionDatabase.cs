using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Azure.Cosmos;

namespace DodoBrands.CosmosDbSessionProvider.Cosmos
{
    internal class CosmosSessionDatabase : ISessionContentsDatabase
    {
        private readonly string _databaseId;
        private readonly string _connectionString;
        private readonly int _lockTtlSeconds;
        private readonly bool _compressionEnabled;

        private object _locker = new object();
        private bool _isInitialized = false;

        private CosmosClient _client;

        public CosmosSessionDatabase(string databaseId, string connectionString, int lockTtlSeconds,
            bool compressionEnabled)
        {
            _databaseId = databaseId;
            _connectionString = connectionString;
            _lockTtlSeconds = lockTtlSeconds;
            _compressionEnabled = compressionEnabled;
        }

        private readonly HashSet<HttpStatusCode> _goodStatusCodes = new HashSet<HttpStatusCode>
            {HttpStatusCode.Created, HttpStatusCode.Accepted, HttpStatusCode.OK};

        private Container _locks;
        private Container _contents;

        private Response<T> CheckValidResponse<T>(Response<T> response)
        {
            if (_goodStatusCodes.Contains(response.StatusCode))
            {
                return response;
            }

            throw new Exception(response.Diagnostics.ToString());
        }

        private async Task InitializeAsync()
        {
            _client = new CosmosClient(_connectionString, new CosmosClientOptions()
            {
                RequestTimeout = TimeSpan.FromSeconds(_lockTtlSeconds / 2),
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(_lockTtlSeconds / 2)
            });
            var database = CheckValidResponse(await _client.CreateDatabaseIfNotExistsAsync(_databaseId));
            var db = _client.GetDatabase(_databaseId);

            var indexNone = new IndexingPolicy
            {
                Automatic = false,
                IndexingMode = IndexingMode.Consistent,
                ExcludedPaths = {new ExcludedPath {Path = "/*"}}
            };

            CheckValidResponse(await db.CreateContainerIfNotExistsAsync(
                new ContainerProperties("locks", "/id")
                {
                    IndexingPolicy = indexNone,
                    DefaultTimeToLive = 300
                }));

            CheckValidResponse(await db.CreateContainerIfNotExistsAsync(
                new ContainerProperties("contents", "/id")
                {
                    IndexingPolicy = indexNone,
                    DefaultTimeToLive = 300
                }));

            _locks = db.GetContainer("locks");

            _contents = db.GetContainer("contents");
        }

        public async Task<(SessionStateValue state, bool isNew)> GetSessionAsync(string sessionId)
        {
            var partitionKey = new PartitionKey(sessionId);
            var itemRequestOptions = new ItemRequestOptions
            {
                ConsistencyLevel = ConsistencyLevel.Strong,
            };
            try
            {
                var storedState = await _contents.ReadItemAsync<SessionStateRecord>(
                    sessionId, partitionKey, itemRequestOptions);
                _ = ExtendLifespan(storedState.Resource);
                return (storedState.Resource.Payload?.ReadSessionState(storedState.Resource.Compressed),
                    storedState.Resource.IsNew == "yes");
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, false);
            }
            catch (CosmosException e)
            {
                throw new Exception($"GetSessionAsync. Bad status code returned: {e.StatusCode}", e);
            }
        }

        private Task ExtendLifespan(SessionStateRecord state)
        {
            var now = DateTime.UtcNow;

            var proximityFactor = 1.0 / 3.0;

            var secondsBeforeExpiration = (state.CreatedDate - now + TimeSpan.FromSeconds(state.TtlSeconds))
                .TotalSeconds;

            var toleratedSecondsBeforeExpiration = state.TtlSeconds * (1.0 - proximityFactor);

            if (secondsBeforeExpiration > toleratedSecondsBeforeExpiration)
            {
                return Task.CompletedTask;
            }

            return _contents.ReplaceItemAsync(state, state.SessionId, new PartitionKey(state.SessionId),
                new ItemRequestOptions
                {
                    ConsistencyLevel = ConsistencyLevel.Eventual,
                    IfMatchEtag = state.ETag,
                    EnableContentResponseOnWrite = false
                });
        }

        public async Task Remove(string sessionId)
        {
            var response = await _contents.DeleteItemAsync<SessionStateRecord>(sessionId, new PartitionKey(sessionId),
                new ItemRequestOptions
                {
                    ConsistencyLevel = ConsistencyLevel.Strong,
                    EnableContentResponseOnWrite = false
                });

            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new Exception("Failed to delete session.");
            }
        }

        public async Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId)
        {
            var now = DateTime.UtcNow;
            try
            {
                var createLockResponse = await _locks.CreateItemAsync(new SessionLockRecord
                {
                    SessionId = sessionId,
                    CreatedDate = DateTime.UtcNow,
                    TtlSeconds = _lockTtlSeconds
                }, new PartitionKey(sessionId), new ItemRequestOptions
                {
                    EnableContentResponseOnWrite = true,
                    ConsistencyLevel = ConsistencyLevel.Strong
                });
                return (true, now, createLockResponse.ETag);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                try
                {
                    var existingLock = await _locks.ReadItemAsync<SessionLockRecord>(sessionId,
                        new PartitionKey(sessionId),
                        new ItemRequestOptions
                        {
                            ConsistencyLevel = ConsistencyLevel.Strong
                        });
                    return (false, existingLock.Resource.CreatedDate, existingLock.ETag);
                }
                catch
                {
                    return (false, now.AddSeconds(-1), "1");
                }
            }
        }

        public async Task TryReleaseLock(string sessionId, object lockId)
        {
            if ((string) lockId == "1")
            {
                return;
            }

            await _locks.DeleteItemAsync<SessionLockRecord>(sessionId, new PartitionKey(sessionId),
                new ItemRequestOptions
                {
                    ConsistencyLevel = ConsistencyLevel.Strong,
                    EnableContentResponseOnWrite = false,
                    IfMatchEtag = (string) lockId
                });
        }

        public async Task WriteContents(string sessionId, SessionStateValue stateValue,
            bool isNew)
        {
            var now = DateTime.UtcNow;
            await _contents.UpsertItemAsync(new SessionStateRecord
                {
                    SessionId = sessionId,
                    CreatedDate = now,
                    TtlSeconds = stateValue.Timeout * 60,
                    IsNew = isNew ? "yes" : null,
                    Payload = stateValue.Write(_compressionEnabled),
                    Compressed = _compressionEnabled
                }, new PartitionKey(sessionId),
                new ItemRequestOptions
                {
                    ConsistencyLevel = ConsistencyLevel.Strong,
                    EnableContentResponseOnWrite = false
                });
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
    }
}