using System.Web.Mvc;
using System.Web.SessionState;

namespace Dodo.AspNet.SessionProviders.CosmosDb.TestWeb.Controllers
{
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            return View();
        }
    }
}