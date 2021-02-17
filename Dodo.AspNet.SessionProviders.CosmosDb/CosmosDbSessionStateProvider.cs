using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.SessionState;
using Microsoft.AspNet.SessionState;
using Microsoft.Azure.Cosmos;

namespace Dodo.AspNet.SessionProviders.CosmosDb
{
    /// <summary>
    /// Azure CosmosDB SessionState provider for async SessionState module
    /// </summary>
    /// <inheritdoc cref="SessionStateStoreProviderAsyncBase"/>
    // ReSharper disable once UnusedType.Global
    public sealed class CosmosDbSessionStateProvider : SessionStateStoreProviderAsyncBase
    {
        private ISessionDatabase _store;

        [SuppressMessage("ReSharper", "EmptyConstructor")]
        public CosmosDbSessionStateProvider()
        {
        }

        /// <summary>
        /// Databases stores one backend for each named provider.
        /// </summary>
        /// <remarks>
        /// SessionStateModule creates multiple provider instances, so we are using named singletons here.
        /// </remarks>
        private static readonly ConcurrentDictionary<string, Lazy<ISessionDatabase>> Databases =
            new ConcurrentDictionary<string, Lazy<ISessionDatabase>>();

        public override void Initialize(string name, NameValueCollection config)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = "CosmosDb session state provider";
            }

            base.Initialize(name, config);

            // Don't ask me why it is prefixed with "x", if you name it just lockTtlSeconds it would fail without an error message.
            var lockTtlSeconds = config.GetInt32("xLockTtlSeconds", 30);

            var consistencyLevel = config.GetEnum("consistencyLevel", ConsistencyLevel.Strong);

            var databaseId = config.GetRequiredString("databaseId");

            var compressionEnabled = config.GetBoolean("compressionEnabled", true);

            var connectionString = config.GetRequiredString("connectionString");

            // ReSharper disable once HeapView.CanAvoidClosure
            _store = Databases.GetOrAdd(name, n => new Lazy<ISessionDatabase>(
                    () =>
                    {
                        var db = new CosmosSessionDatabase(connectionString, databaseId,
                            lockTtlSeconds, compressionEnabled, consistencyLevel);
                        db.Initialize();
                        return db;
                    },
                    LazyThreadSafetyMode.PublicationOnly))
                .Value;
        }

        public override Task CreateUninitializedItemAsync(
            HttpContextBase context,
            string id,
            int timeout,
            CancellationToken cancellationToken)
        {
            return _store.WriteContents(context, id, new SessionStateValue(null, null, timeout), true);
        }

        public override void Dispose()
        {
        }

        public override async Task EndRequestAsync(HttpContextBase context)
        {
            await _store.ExtendLifetime(context);
        }

        public override Task<GetItemResult> GetItemAsync(HttpContextBase context, string id,
            CancellationToken cancellationToken)
        {
            return DoGetAsync(context, id, false);
        }

        public override Task<GetItemResult> GetItemExclusiveAsync(HttpContextBase context, string id,
            CancellationToken cancellationToken)
        {
            return DoGetAsync(context, id, true);
        }

        public override void InitializeRequest(HttpContextBase context)
        {
        }

        public override Task ReleaseItemExclusiveAsync(
            HttpContextBase context,
            string id,
            object lockId,
            CancellationToken cancellationToken)
        {
            AssertIdValid(id);

            return _store.TryReleaseLock(id, lockId);
        }

        public override Task RemoveItemAsync(
            HttpContextBase context,
            string id,
            object lockId,
            SessionStateStoreData item,
            CancellationToken cancellationToken)
        {
            AssertIdValid(id);

            return _store.Remove(context, id);
        }

        /// <summary>
        /// ResetItemTimeoutAsync is a no-op in this implementation, because it is extended during Get operation based on
        /// half-expiration of the session timeout.
        /// </summary>
        public override Task ResetItemTimeoutAsync(HttpContextBase context, string id,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// SetAndReleaseItemExclusiveAsync saves the item to db and releases lock.
        /// </summary>
        /// <remarks>
        /// In this implementation, the lock is stored as a separate item in db.
        /// If the lock failed to release, db cleaning routine should remove it
        /// according to the timestamp and TTL values as soon as possible.
        /// </remarks>
        public override async Task SetAndReleaseItemExclusiveAsync(
            HttpContextBase context,
            string id,
            SessionStateStoreData item,
            object lockId,
            bool newItem,
            CancellationToken cancellationToken)
        {
            AssertIdValid(id);

            Trace.WriteLine($"SetAndReleaseItemExclusiveAsync. items.Dirty: {item.Items.Dirty}");

            var state = item.ExtractDataForStorage();

            try
            {
                await _store.WriteContents(context, id, state, false);
            }
            finally
            {
                if (!newItem)
                {
                    await _store.TryReleaseLock(id, lockId);
                }
            }
        }

        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        private async Task<GetItemResult> DoGetAsync(HttpContextBase context, string id, bool exclusive)
        {
            object lockId = null;

            if (exclusive)
            {
                DateTime lockDate;
                bool lockTaken;
                (lockTaken, lockDate, lockId) = await _store.TryAcquireLock(id);

                if (!lockTaken)
                {
                    var lockAge = DateTime.UtcNow - lockDate;
                    return new GetItemResult(null, true, lockAge, lockId, SessionStateActions.None);
                }
            }

            var (state, isNew) = await _store.GetSessionAsync(context, id);

            if (state == null)
            {
                if (exclusive)
                {
                    await _store.TryReleaseLock(id, lockId);
                }

                return null;
            }

            var sessionStateStoreData =
                CreateLegitStoreData(context, state.SessionItems, state.StaticObjects, state.Timeout);

            return new GetItemResult(
                sessionStateStoreData, exclusive, TimeSpan.Zero, lockId,
                isNew ? SessionStateActions.InitializeItem : SessionStateActions.None);
        }

        private SessionStateStoreData CreateLegitStoreData(
            HttpContextBase context,
            ISessionStateItemCollection sessionItems,
            HttpStaticObjectsCollection staticObjects,
            int timeout)
        {
            if (sessionItems == null)
            {
                sessionItems = new SessionStateItemCollection();
            }

            if (staticObjects == null && context != null)
            {
                staticObjects = SessionStateUtility.GetSessionStaticObjects(context.ApplicationInstance.Context);
            }

            return new SessionStateStoreData(sessionItems, staticObjects, timeout);
        }

        public override SessionStateStoreData CreateNewStoreData(HttpContextBase context, int timeout)
        {
            return CreateLegitStoreData(context, null, null, timeout);
        }

        private static void AssertIdValid(string id)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            if (id.Length > SessionIDManager.SessionIDMaxLength)
            {
                throw new ArgumentOutOfRangeException(id, id, StringResources.Session_id_too_long);
            }
        }
    }
}