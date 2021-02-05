using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Web.SessionState;

namespace DodoBrands.AspNet.SessionProviders.TestWeb.Controllers
{
    [SessionState(SessionStateBehavior.Required)]
    public class WriteToSessionController : Controller
    {
        private AsyncLocal<Random> _random = new AsyncLocal<Random>();

        // GET
        public async Task<ActionResult> Index()
        {
            Random random = _random.Value;
            if (random == null)
            {
                _random.Value = random = new Random();
            }

            await Task.Delay(random.Next(1000, 2000));

            var o = Session["Counter"];
            if (o == null)
            {
                Session["Counter"] = 1;
            }
            else
            {
                Session["Counter"] = (int) o + 1;
            }

            var sb = new StringBuilder();

            for (var i = 0; i < 1000; i++)
            {
                sb.Append(random.Next());
            }

            Session["TonOfStuffForProfiling"] = sb.ToString();

            return View();
        }
    }
}