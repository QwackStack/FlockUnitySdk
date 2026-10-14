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

        /// <summary>The leaderboards that turned away the score this write fed them. Only UpdatePlayerDataAsync and UpdatePlayerDataFieldAsync report them: empty after a read, AddGameFundsAsync or UnlockAchievementAsync means "not reported here", not "no board refused". The write itself succeeded either way, and the cached row never keeps them.</summary>
        [JsonProperty("leaderboard_refusals", NullValueHandling = NullValueHandling.Ignore)]
        public List<LeaderboardRefusal> LeaderboardRefusals { get; set; } = new List<LeaderboardRefusal>();

        // A copy for the cache: a refusal describes the one write it came back with, never the row a later read finds.
        internal PlayerData WithoutLeaderboardRefusals()
        {
            PlayerData copy = (PlayerData)MemberwiseClone();
            copy.LeaderboardRefusals = new List<LeaderboardRefusal>();
            return copy;
        }
    }

    /// <summary>Why a leaderboard turned a score away. Strings rather than an enum: the server can add a reason, and an unknown one must not break reading the write's answer.</summary>
    public static class LeaderboardRefusalReasons
    {
        /// <summary>The value is outside what the board accepts; trying again changes nothing.</summary>
        public const string ScoreBounds = "score_bounds";
        /// <summary>The player's recent gains fill the board's allowance; it clears as they age out, unless this jump is larger than all of it.</summary>
        public const string GainLimit = "gain_limit";
    }

    /// <summary>A leaderboard that turned away the score a player-data write fed it. The write itself succeeded; the board did not take the new value.</summary>
    public class LeaderboardRefusal
    {
        [JsonProperty("leaderboard_id")]
        public string LeaderboardId { get; set; }

        /// <summary>The board's name, which is how the leaderboard calls address it.</summary>
        [JsonProperty("leaderboard_name")]
        public string LeaderboardName { get; set; }

        /// <summary>One of <see cref="LeaderboardRefusalReasons"/>, as the server spelled it.</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        /// <summary>Whether trying again can ever work.</summary>
        [JsonProperty("retryable")]
        public bool Retryable { get; set; }

        /// <summary>Seconds until it can, when Retryable and the server said how long; null otherwise.</summary>
        [JsonProperty("retry_after_seconds")]
        public int? RetryAfterSeconds { get; set; }
    }
}
