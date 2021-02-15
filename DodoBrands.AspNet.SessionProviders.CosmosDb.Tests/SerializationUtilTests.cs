using System;
using System.Web.SessionState;
using NUnit.Framework;

namespace DodoBrands.AspNet.SessionProviders.CosmosDb
{
    public class SessionSerializationUtilTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SerializeDeserializeSessionValue(bool compressed)
        {
            var expected = $"expected_{Guid.NewGuid():N}";
            var sessionStateItemCollection = new SessionStateItemCollection
            {
                ["actual"] = expected
            };

            var serialized = new SessionStateValue(
                    sessionStateItemCollection,
                    null,
                    1)
                .Write(compressed);

            var deserialized = serialized.ReadSessionState(compressed);

            var actual = deserialized.SessionItems["actual"];

            Assert.AreEqual(expected, actual);
        }
    }
}