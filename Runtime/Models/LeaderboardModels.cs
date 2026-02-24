using System;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class LeaderboardData
    {
        [JsonProperty("goal")]
        public string Goal { get; set; }

        [JsonProperty("source_key")]
        public string SourceKey { get; set; }
    }

    public class Leaderboard
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("data")]
        public LeaderboardData Data { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
