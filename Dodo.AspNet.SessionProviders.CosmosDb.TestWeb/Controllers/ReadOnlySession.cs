using System.Web.Mvc;
using System.Web.SessionState;

namespace Dodo.AspNet.SessionProviders.CosmosDb.TestWeb.Controllers
{
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class ReadOnlySessionController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Counter = Session["Counter"];
            return View();
        }
    }
}