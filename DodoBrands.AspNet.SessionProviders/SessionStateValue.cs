using System;
using System.Web;
using System.Web.SessionState;
using Newtonsoft.Json;

namespace DodoBrands.AspNet.SessionProviders
{
    public sealed class SessionStateRecord
    {
        [JsonProperty(PropertyName = "id")] public string SessionId { get; set; }

        [JsonProperty(PropertyName = "ttl")] public int TtlSeconds { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, PropertyName = "_etag")]
        public string ETag { get; }

        [JsonProperty(PropertyName = "compressed")]
        public bool Compressed { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, PropertyName = "Payload")]
        public byte[] Payload { get; set; }

        [JsonProperty(PropertyName = "CreatedDate")]
        public DateTime CreatedDate { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, PropertyName = "IsNew")]
        public string IsNew { get; set; }
    }

    public sealed class SessionStateValue
    {
        public int Timeout { get; }

        public SessionStateItemCollection SessionItems { get; }

        public HttpStaticObjectsCollection StaticObjects { get; }

        public SessionStateValue(
            SessionStateItemCollection sessionItems,
            HttpStaticObjectsCollection staticObjects,
            int timeout)
        {
            SessionItems = sessionItems;
            StaticObjects = staticObjects;
            Timeout = timeout;
        }
    }
}