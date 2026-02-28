using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Models
{
    public class GameConfigSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("game_version_id")]
        public string GameVersionId { get; set; }

        [JsonProperty("schema")]
        public Dictionary<string, object> Schema { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("tag")]
        public string Tag { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        public T GetDataAs<T>()
        {
            if (Data == null) return default;
            return JObject.FromObject(Data).ToObject<T>();
        }
    }

    public class GamePatchSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("game_config_id")]
        public string GameConfigId { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        public T GetDataAs<T>()
        {
            if (Data == null) return default;
            return JObject.FromObject(Data).ToObject<T>();
        }
    }
}
