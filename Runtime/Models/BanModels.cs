using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class FeatureBan
    {
        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("ban_duration")]
        public string BanDuration { get; set; }

        [JsonProperty("effective_datetime")]
        public string EffectiveDatetime { get; set; }
    }

    public class PlayerBan
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, FeatureBan> Data { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }
}
