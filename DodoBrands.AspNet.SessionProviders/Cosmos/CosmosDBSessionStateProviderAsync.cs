using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Configuration;
using System.Web.SessionState;
using Microsoft.AspNet.SessionState;

namespace DodoBrands.CosmosDbSessionProvider.Cosmos
{
    /// <summary>
    /// Azure CosmosDB SessionState provider for async SessionState module
    /// </summary>
    /// <inheritdoc cref="SessionStateStoreProviderAsyncBase"/>
    // ReSharper disable once UnusedType.Global
    public sealed class CosmosDbSessionStateProviderAsync : SessionStateStoreProviderAsyncBase
    {
        private const string SESSIONSTATE_SECTION_PATH = "system.web/sessionState";

        private ISessionContentsDatabase _store;

        [SuppressMessage("ReSharper", "EmptyConstructor")]
        public CosmosDbSessionStateProviderAsync()
        {
        }

        private static readonly ConcurrentDictionary<string, Lazy<ISessionContentsDatabase>> _databases =
            new ConcurrentDictionary<string, Lazy<ISessionContentsDatabase>>();

        public override void Initialize(string name, NameValueCollection config)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = "CosmosDb session state provider";
            }

            base.Initialize(name, config);

            // Don't ask me why it is prefixed with "x", if you name it just lockTtlSeconds it would fail without an error message.
            var lockTtlSeconds = ConfigHelper.GetInt32(config, "xLockTtlSeconds", 30);

            var ssc = (SessionStateSection) ConfigurationManager.GetSection(SESSIONSTATE_SECTION_PATH);
            var compressionEnabled = ssc.CompressionEnabled;

            _store = _databases.GetOrAdd(name, n => new Lazy<ISessionContentsDatabase>(
                    () => new SessionDatabaseInProcessEmulation(lockTtlSeconds, compressionEnabled),
                    LazyThreadSafetyMode.PublicationOnly))
                .Value;
        }

        public override async Task CreateUninitializedItemAsync(
            HttpContextBase context,
            string id,
            int timeout,
            CancellationToken cancellationToken)
        {
            _store.WriteContents(id, new SessionStateValue(null, null, timeout), isNew: true);
        }

        public override void Dispose()
        {
        }

        public override Task EndRequestAsync(HttpContextBase context)
        {
            return Task.CompletedTask;
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

            return _store.Remove(id);
        }

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

            var state = item.ExtractDataForStorage();

            try
            {
                await _store.WriteContents(id, state, isNew: false);
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
            bool lockTaken = false;
            DateTime lockDate = DateTime.MinValue;

            if (exclusive)
            {
                (lockTaken, lockDate, lockId) = await _store.TryAcquireLock(id);

                if (!lockTaken)
                {
                    // Lock not taken means it's already locked by other user.
                    var lockAge = DateTime.UtcNow - lockDate;
                    return new GetItemResult(null, true, lockAge, lockId, SessionStateActions.None);
                }
            }

            var (state, isNew) = await _store.GetSessionAsync(id);

            if (state == null)
            {
                if (exclusive && lockTaken)
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