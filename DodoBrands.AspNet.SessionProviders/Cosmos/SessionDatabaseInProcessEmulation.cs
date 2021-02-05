using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DodoBrands.CosmosDbSessionProvider.Cosmos
{
    /// <summary>
    /// SessionDatabaseInProcessEmulation is here for test purposes.
    /// It closely models what should happen in database backend.
    /// </summary>
    public class SessionDatabaseInProcessEmulation : ISessionContentsDatabase
    {
        private const int LockTtlSeconds = 30;
        private static readonly TimeSpan TimeoutResetInterval = TimeSpan.FromMinutes(10);

        private List<SessionStateRecord> _contents =
            new List<SessionStateRecord>();

        private List<SessionLockRecord> _locks =
            new List<SessionLockRecord>();

        private List<(string sessionId, DateTime touchedAt)> _recentlyUpdated =
            new List<(string sessionId, DateTime touchedAt)>();

        private readonly object _locker = new object();

        private static readonly TraceSource _trace = new TraceSource("DodoBrands.CosmosDbSessionProvider.Cosmos.SessionDatabaseInProcessEmulation");

        public SessionDatabaseInProcessEmulation()
        {
            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    try
                    {
                        RemoveOutdated();
                    }
                    catch
                    {
                    }

                    await Task.Delay(5000);
                }
            }, TaskCreationOptions.LongRunning);
        }
        
        private void RemoveOutdated()
        {
            lock (_locker)
            {
                var now = DateTime.UtcNow;
                var countBefore = _contents.Count;
                _contents = _contents
                    .Where(x => x.CreatedDate >= now - TimeSpan.FromSeconds(x.TtlSeconds))
                    .ToList();
                var countAfter = _contents.Count;
                
                _trace.TraceEvent(TraceEventType.Verbose, 0, $"Removing old sessions. Before: {countBefore}, After: {countAfter}");
                
                _locks = _locks
                    .Where(x => x.CreatedDate >= now - TimeSpan.FromSeconds(x.TtlSeconds))
                    .ToList();
            }
        }
    
        public async Task<(SessionStateValue state, bool isNew)> GetSessionAsync(string sessionId)
        {
            await Task.Yield();
            var storedState = _contents
                .SingleOrDefault(x => x.SessionId == sessionId);
            return storedState == null
                ? (null, false)
                : (storedState.Payload?.ReadSessionState(), storedState.IsNew == "yes");
        }

        public async Task Remove(string sessionId)
        {
            await Task.Yield();
            lock (_locker)
            {
                _contents = _contents.Where(x => x.SessionId != sessionId).ToList();
            }
        }

        public async Task ResetTimeout(string sessionId)
        {
            var now = DateTime.UtcNow;
            await Task.Yield();
            lock (_locker)
            {
                var recentlyUpdated = _recentlyUpdated.Any(x =>
                    x.sessionId == sessionId && x.touchedAt >= now - TimeoutResetInterval);

                if (recentlyUpdated)
                {
                    return;
                }
                
                var state = _contents.SingleOrDefault(x => x.SessionId == sessionId);
                if (state == null)
                {
                    return;
                }
                state.CreatedDate = now;
            }
        }

        public async Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId)
        {
            var now = DateTime.UtcNow;
            await Task.Yield();
            lock (_locker)
            {
                var l = _locks.SingleOrDefault(x => x.SessionId == sessionId);
                if (l == null)
                {
                    var eTag = Guid.NewGuid().ToString("N");
                    _locks.Add(new SessionLockRecord
                        {SessionId = sessionId, CreatedDate = now, TtlSeconds = LockTtlSeconds, ETag = eTag});
                    return (true, now, eTag);
                }

                return (false, l.CreatedDate, l.ETag);
            }
        }

        public async Task TryReleaseLock(string sessionId, object lockId)
        {
            await Task.Yield();
            lock (_locker)
            {
                _locks = _locks
                    .Where(l => !(l.SessionId == sessionId && l.ETag == (string) lockId))
                    .ToList();
            }
        }

        public async Task WriteContents(string sessionId, SessionStateValue stateValue,
            bool isNew)
        {
            var now = DateTime.UtcNow;
            await Task.Yield();
            lock (_locker)
            {
                _contents = _contents.Where(x => x.SessionId != sessionId).ToList();
                
                _contents.Add(new SessionStateRecord
                {
                    SessionId = sessionId,
                    CreatedDate = now,
                    TtlSeconds = stateValue.Timeout * 60,
                    IsNew = isNew ? "yes" : null,
                    Payload = stateValue.Write(),
                });
            }
        }
    }
}