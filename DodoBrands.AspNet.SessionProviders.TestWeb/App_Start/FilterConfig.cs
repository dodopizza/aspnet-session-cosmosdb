using System.Web.Mvc;

namespace DodoBrands.AspNet.SessionProviders.TestWeb
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());
        }
    }
}