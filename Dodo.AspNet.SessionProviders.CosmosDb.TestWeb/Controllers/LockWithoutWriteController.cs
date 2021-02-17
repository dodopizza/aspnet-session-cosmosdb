using System.Web.Mvc;
using System.Web.SessionState;

namespace Dodo.AspNet.SessionProviders.CosmosDb.TestWeb.Controllers
{
    [SessionState(SessionStateBehavior.Required)]
    public class LockWithoutWriteController : Controller
    {
        // GET
        public ActionResult Index()
        {
            ViewBag.Counter = Session["Counter"];
            return View();
        }
    }
}