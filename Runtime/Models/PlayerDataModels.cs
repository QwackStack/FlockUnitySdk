using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
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
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    internal class PlayerDataRequest
    {
        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }
    }

    internal class UpdatePlayerDataRequest
    {
        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }
    }
}
