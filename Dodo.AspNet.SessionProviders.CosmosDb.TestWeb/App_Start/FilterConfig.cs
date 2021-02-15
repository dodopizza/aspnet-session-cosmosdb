using System.Web.Mvc;

namespace Dodo.AspNet.SessionProviders.CosmosDb.TestWeb
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());
        }
    }
}