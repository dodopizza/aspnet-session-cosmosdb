using System.Web.Mvc;
using System.Web.SessionState;

namespace DodoBrands.AspNet.SessionProviders.TestWeb.Controllers
{
    [SessionState(SessionStateBehavior.ReadOnly)]
    public class HomeController : Controller
    {
        
        public ActionResult Index()
        {
            return View();
        }

        public ActionResult About()
        {
            ViewBag.Message = "Your application description page.";
            return View();
        }

        public ActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";
            return View();
        }
    }
}