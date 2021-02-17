using System;
using System.Threading.Tasks;
using System.Web;

namespace Dodo.AspNet.SessionProviders.CosmosDb
{
    internal interface ISessionDatabase
    {
        Task<(SessionStateValue state, bool isNew)> GetSessionAsync(HttpContextBase context, string sessionId);
        Task Remove(HttpContextBase context, string sessionId);
        Task<(bool lockTaken, DateTime lockDate, object lockId)> TryAcquireLock(string sessionId);
        Task TryReleaseLock(string sessionId, object lockId);
        Task WriteContents(HttpContextBase context, string sessionId, SessionStateValue stateValue, bool isNew);
        Task ExtendLifetime(HttpContextBase context);
    }
}