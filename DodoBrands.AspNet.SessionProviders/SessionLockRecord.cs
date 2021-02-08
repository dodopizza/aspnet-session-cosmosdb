using System;
using Newtonsoft.Json;

namespace DodoBrands.CosmosDbSessionProvider.Cosmos
{
    public sealed class SessionLockRecord
    {
        [JsonProperty(PropertyName = "id")] public string SessionId { get; set; }

        [JsonProperty(PropertyName = "ttl")] public int TtlSeconds { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, PropertyName = "_etag")]
        public string ETag { get; set; }

        [JsonProperty(PropertyName = "CreatedDate")]
        public DateTime CreatedDate { get; set; }
    }
}