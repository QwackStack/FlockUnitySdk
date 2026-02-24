using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    public class AchievementPlatform
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("external_id")]
        public string ExternalId { get; set; }
    }

    public class Achievement
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("platforms")]
        public List<AchievementPlatform> Platforms { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }
}
