using System;
using System.Threading.Tasks;

namespace DodoBrands.AspNet.SessionProviders.CosmosDb
{
    internal interface ISessionDatabase
    {
        Task<(SessionStateValue state, bool isNew)> GetSessionAsync(string sessionId, bool extendLifespan);
        Task Remove(string sessionId);
        Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId);
        Task TryReleaseLock(string sessionId, object lockId);
        Task WriteContents(string sessionId, SessionStateValue stateValue, bool isNew);
    }
}