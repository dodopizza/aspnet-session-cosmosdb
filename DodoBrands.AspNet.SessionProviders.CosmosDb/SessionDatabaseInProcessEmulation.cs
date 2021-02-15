using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DodoBrands.AspNet.SessionProviders.CosmosDb
{
    /// <summary>
    /// SessionDatabaseInProcessEmulation is here for test purposes.
    /// It closely models what should happen in database backend.
    /// </summary>
    /// <remarks>This in-memory implementation should not be used in production by no means.
    /// It is not optimized, and will suffer algorithmic complexity issues.
    /// </remarks>
    internal class SessionDatabaseInProcessEmulation : ISessionDatabase
    {
        private readonly int _lockTtlSeconds;
        private readonly bool _compress;

        private List<string> _contents =
            new List<string>();

        private List<string> _locks =
            new List<string>();

        private readonly object _locker = new object();

        private static readonly TraceSource _trace =
            new TraceSource("DodoBrands.CosmosDbSessionProvider.Cosmos.SessionDatabaseInProcessEmulation");

        public SessionDatabaseInProcessEmulation(int lockTtlSeconds, bool compress)
        {
            _lockTtlSeconds = lockTtlSeconds;
            _compress = compress;
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
                    .Select(Deserialize<SessionStateRecord>)
                    .Where(x => x.CreatedDate >= now - TimeSpan.FromSeconds(x.TtlSeconds))
                    .Select(Serialize)
                    .ToList();
                var countAfter = _contents.Count;

                _trace.TraceEvent(TraceEventType.Verbose, 0,
                    $"Removing old sessions. Before: {countBefore}, After: {countAfter}");

                _locks = _locks
                    .Select(Deserialize<SessionLockRecord>)
                    .Where(x => x.CreatedDate >= now - TimeSpan.FromSeconds(x.TtlSeconds))
                    .Select(Serialize)
                    .ToList();
            }
        }

        public async Task<(SessionStateValue state, bool isNew)> GetSessionAsync(string sessionId, bool extendLifespan)
        {
            await Task.Yield();

            var storedState = _contents
                .Select(Deserialize<SessionStateRecord>)
                .SingleOrDefault(x => x.SessionId == sessionId);

            if (storedState == null)
            {
                return (null, false);
            }

            _ = ExtendLifespan(sessionId, storedState.CreatedDate, TimeSpan.FromSeconds(storedState.TtlSeconds));

            return (storedState.Payload?.ReadSessionState(storedState.Compressed), storedState.IsNew == "yes");
        }

        public async Task Remove(string sessionId)
        {
            await Task.Yield();
            lock (_locker)
            {
                _contents = _contents
                    .Select(Deserialize<SessionStateRecord>)
                    .Where(x => x.SessionId != sessionId)
                    .Select(Serialize)
                    .ToList();
            }
        }

        private async Task ExtendLifespan(string sessionId, DateTime createdDate, TimeSpan ttl)
        {
            var now = DateTime.UtcNow;

            var proximityFactor = 1.0 / 3.0;

            var secondsBeforeExpiration = (createdDate - now + ttl).TotalSeconds;

            var toleratedSecondsBeforeExpiration = ttl.TotalSeconds * (1.0 - proximityFactor);

            if (secondsBeforeExpiration > toleratedSecondsBeforeExpiration)
            {
                return;
            }

            await Task.Yield();
            lock (_locker)
            {
                _contents = _contents
                    .Select(old =>
                    {
                        var x = Deserialize<SessionStateRecord>(old);
                        if (x.SessionId != sessionId)
                        {
                            return old;
                        }

                        x.CreatedDate = now;
                        return Serialize(x);
                    })
                    .ToList();
            }
        }

        public async Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId)
        {
            var now = DateTime.UtcNow;
            await Task.Yield();
            lock (_locker)
            {
                var l = _locks
                    .Select(Deserialize<SessionLockRecord>)
                    .SingleOrDefault(x => x.SessionId == sessionId);
                if (l == null)
                {
                    var eTag = Guid.NewGuid().ToString("N");
                    _locks.Add(Serialize(new SessionLockRecord
                        {SessionId = sessionId, CreatedDate = now, TtlSeconds = _lockTtlSeconds, ETag = eTag}));
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
                    .Select(Deserialize<SessionLockRecord>)
                    .Where(l => !(l.SessionId == sessionId && l.ETag == (string) lockId))
                    .Select(Serialize)
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
                _contents = _contents
                    .Select(Deserialize<SessionStateRecord>)
                    .Where(x => x.SessionId != sessionId)
                    .Select(Serialize)
                    .ToList();

                _contents.Add(Serialize(new SessionStateRecord
                {
                    SessionId = sessionId,
                    CreatedDate = now,
                    TtlSeconds = stateValue.Timeout * 60,
                    IsNew = isNew ? "yes" : null,
                    Payload = stateValue.Write(_compress),
                    Compressed = _compress,
                }));
            }
        }

        public void Initialize()
        {
        }

        private string Serialize(object record)
        {
            return JsonConvert.SerializeObject(record);
        }

        private T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
    }
}