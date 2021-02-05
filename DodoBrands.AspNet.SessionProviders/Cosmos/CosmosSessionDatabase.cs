using System;
using System.Threading.Tasks;

namespace DodoBrands.CosmosDbSessionProvider.Cosmos
{
    internal class CosmosSessionDatabase : ISessionContentsDatabase
    {
        public Task<(SessionStateValue state, bool isNew)> GetSessionAsync(string sessionId)
        {
            throw new NotImplementedException();
        }

        public Task Remove(string sessionId)
        {
            throw new NotImplementedException();
        }

        public Task ResetTimeout(string sessionId)
        {
            throw new NotImplementedException();
        }

        public Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId)
        {
            throw new NotImplementedException();
        }

        public Task TryReleaseLock(string sessionId, object lockId)
        {
            throw new NotImplementedException();
        }

        public Task WriteContents(string sessionId, SessionStateValue stateValue,
            bool isNew)
        {
            throw new NotImplementedException();
        }
    }
}