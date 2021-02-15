using System;
using Newtonsoft.Json;

namespace DodoBrands.AspNet.SessionProviders.CosmosDb
{
    internal class TryLockResponse
    {
        [JsonProperty("locked")] public bool Locked { get; set; }

        [JsonProperty("etag")] public string ETag { get; set; }

        [JsonProperty("createdDate")] public DateTime CreatedDate { get; set; }
    }
}