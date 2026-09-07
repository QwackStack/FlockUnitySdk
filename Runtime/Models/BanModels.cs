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

        /// <summary>True when this is a real ban record rather than the "not banned" empty one.</summary>
        /// <remarks>
        /// <b>The only test.</b> <c>GetBanAsync</c> never returns null: a player with no ban is the ordinary
        /// state of almost everyone, so the server answers a 2xx with <c>result: null</c> and the SDK turns
        /// that into an empty record — not an error, and not a null every call site has to guard.
        /// </remarks>
        [JsonIgnore]
        public bool IsBanned => !string.IsNullOrEmpty(Id);

        /// <summary>True when this player is banned from <paramref name="feature"/> specifically.</summary>
        /// <remarks>
        /// A player can be banned from one feature and not others, so <see cref="IsBanned"/> alone is not
        /// enough to gate a particular action.
        /// </remarks>
        public bool IsBannedFrom(string feature)
        {
            return Data != null && Data.ContainsKey(feature);
        }
    }
}
