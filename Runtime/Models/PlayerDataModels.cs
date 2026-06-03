using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class PlayerTemplateSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("game_version_id")]
        public string GameVersionId { get; set; }

        [JsonProperty("schema")]
        public List<TypedSchema> Schema { get; set; }

        [JsonProperty("data")]
        public List<DataField> Data { get; set; }

        [JsonProperty("tag")]
        public string Tag { get; set; }
    }

    public class PlayerData
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("player_template_id")]
        public string PlayerTemplateId { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("data")]
        public List<DataField> Data { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
