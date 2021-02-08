using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DodoBrands.AspNet.SessionProviders.Cosmos
{
    public class CosmosSessionDatabaseTests
    {
        private const int LockTtlSeconds = 2;

        [Test]
        public async Task LockCanBeAcquired()
        {
            var id = Guid.NewGuid().ToString("N");
            var db = GetDatabase();
            
            var result = await db.TryAcquireLock(id);
            TestContext.WriteLine(result);
            
            Assert.IsTrue(result.lockTaken);
        }
        
        [Test]
        public async Task LockCanBeAcquiredOnlyOnce()
        {
            var id = Guid.NewGuid().ToString("N");
            var db = GetDatabase();
            await db.TryAcquireLock(id);
            
            var result = await db.TryAcquireLock(id);
            
            Assert.IsFalse(result.lockTaken);
        }
        
        [Test]
        public async Task LockCanBeReleased()
        {
            var id = Guid.NewGuid().ToString("N");
            var db = GetDatabase();
            var (_, _, lockId) = await db.TryAcquireLock(id);
            
            await db.TryReleaseLock(id, lockId);
        }
        
        [Test]
        public async Task LockCanBeAcquiredAgainAfterReleasing()
        {
            var id = Guid.NewGuid().ToString("N");
            var db = GetDatabase();
            var (_, _, lockId) = await db.TryAcquireLock(id);
            await db.TryReleaseLock(id, lockId);

            var (lockTaken, _, _) = await db.TryAcquireLock(id);
            
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
            var db = GetDatabase();
            await db.TryAcquireLock(id);
            await Task.Delay(TimeSpan.FromSeconds(LockTtlSeconds + 1));

            var (lockTaken, _, _) = await db.TryAcquireLock(id);
            
            Assert.IsTrue(lockTaken);
        }

        private ISessionDatabase GetDatabase() => _db.Value;

        private static CosmosSessionDatabase CreateDatabase()
        {
            var connectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");
            if (connectionString.StartsWith(@""""))
            {
                connectionString = connectionString.Substring(1, connectionString.Length - 2);
            }
            var db = new CosmosSessionDatabase(connectionString, "testdb", LockTtlSeconds, true);
            db.Initialize();
            return db;
        }

        private Lazy<ISessionDatabase> _db =
            new Lazy<ISessionDatabase>(CreateDatabase, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}