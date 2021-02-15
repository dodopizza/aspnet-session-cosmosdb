using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using NUnit.Framework;

namespace Dodo.AspNet.SessionProviders.CosmosDb
{
    public class CosmosSessionDatabaseTests
    {
        private const int LockTtlSeconds = 2;

        static CosmosSessionDatabaseTests()
        {
            var traceSource = CosmosSessionDatabase.TraceSource;
            traceSource.Listeners.Add(new TestTraceListener());
            traceSource.Switch.Level = SourceLevels.All;
        }

        [Test]
        public async Task LockCanBeAcquired()
        {
            var id = Guid.NewGuid().ToString("N");

            var result = await Db.TryAcquireLock(id);
            TestContext.WriteLine(result);

            Assert.IsTrue(result.lockTaken);
        }

        [Test]
        public async Task LockCanBeAcquiredOnlyOnce()
        {
            var id = Guid.NewGuid().ToString("N");
            await Db.TryAcquireLock(id);

            var result = await Db.TryAcquireLock(id);

            Assert.IsFalse(result.lockTaken);
        }

        [Test]
        public async Task LockCanBeReleased()
        {
            var id = Guid.NewGuid().ToString("N");

            var (_, _, lockId) = await Db.TryAcquireLock(id);

            await Db.TryReleaseLock(id, lockId);
        }

        [Test]
        public async Task LockCanBeAcquiredAgainAfterReleasing()
        {
            var id = Guid.NewGuid().ToString("N");
            var (_, _, lockId) = await Db.TryAcquireLock(id);
            await Db.TryReleaseLock(id, lockId);

            var (lockTaken, _, _) = await Db.TryAcquireLock(id);

            Assert.IsTrue(lockTaken);
        }

        /// <summary>
        /// LockCanBeAcquiredAfterLockTtl checks that db releases lock based on TTL.
        /// </summary>
        /// <remarks>
        /// It is surprising, how fast DB catches up. Just one second after the TTL is enough.
        /// </remarks>
        [Test]
        public async Task LockCanBeAcquiredAfterLockTtl()
        {
            var id = Guid.NewGuid().ToString("N");
            await Db.TryAcquireLock(id);
            await Task.Delay(TimeSpan.FromSeconds(LockTtlSeconds + 1));

            var (lockTaken, _, _) = await Db.TryAcquireLock(id);

            Assert.IsTrue(lockTaken);
        }

        private ISessionDatabase Db => _db.Value;

        private static CosmosSessionDatabase CreateDatabase()
        {
            var emulatorConnectionString =
                @"AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
            var db = new CosmosSessionDatabase(emulatorConnectionString, "testdb", LockTtlSeconds, true,
                ConsistencyLevel.Session);
            db.Initialize();
            return db;
        }

        private Lazy<ISessionDatabase> _db =
            new Lazy<ISessionDatabase>(CreateDatabase, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}