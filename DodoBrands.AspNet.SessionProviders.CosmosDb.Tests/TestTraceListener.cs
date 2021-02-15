using System.Diagnostics;
using NUnit.Framework;

namespace DodoBrands.AspNet.SessionProviders.CosmosDb
{
    public class TestTraceListener : TraceListener
    {
        public override void Write(string message)
        {
            TestContext.Write(message);
        }

        public override void WriteLine(string message)
        {
            TestContext.WriteLine(message);
        }
    }
}