using Newtonsoft.Json;

namespace Flock.Models
{
    public class PaginatedResponse<T>
    {
        [JsonProperty("items")]
        public T[] Items { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("page")]
        public int Page { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }
    }
}
