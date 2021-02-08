using System.Web.SessionState;

namespace DodoBrands.CosmosDbSessionProvider
{
    internal static class CustomSessionUtil
    {
        public static SessionStateValue ExtractDataForStorage(this SessionStateStoreData item)
        {
            var items = item.Items.Count > 0 ? item.Items : null;
            var staticObjects = item.StaticObjects.NeverAccessed ? null : item.StaticObjects;

            if (item.Items.Count > 0)
            {
                items = item.Items;
            }

            if (!item.StaticObjects.NeverAccessed)
            {
                staticObjects = item.StaticObjects;
            }

            var state = new SessionStateValue((SessionStateItemCollection) items, staticObjects, item.Timeout);
            return state;
        }
    }
}