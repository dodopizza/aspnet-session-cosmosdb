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
        public async Task<ActionResult> Index()
        {
            var random = _random.Value;
            //await Task.Delay(random.Next(500, 1500));

            var o = Session["Counter"];
            if (o == null)
            {
                Session["Counter"] = 1;
            }
            else
            {
                Session["Counter"] = (int) o + 1;
            }

            Session["HeavyPayload"] = GeneratePayload(random, 20_000);

            return View();
        }

        private readonly ThreadLocal<Random> _random = new ThreadLocal<Random>(() => new Random());

        private static string GeneratePayload(Random random, int size)
        {
            var sb = new StringBuilder();
            while (sb.Length < size)
            {
                sb.Append((char) ('0' + random.Next(10)));
            }

            return sb.ToString();
        }
    }
}