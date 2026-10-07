using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Providers
{
    /// <summary>One row of the game's matchmaking queue list, as much of it as the SDK reads.</summary>
    internal sealed class MatchmakingQueue
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>A party as every party route answers it; only the two reads add the members.</summary>
    internal class PartyRecord
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("leader_player_id")]
        public string LeaderPlayerId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("invite_code")]
        public string InviteCode { get; set; }

        [JsonProperty("max_size")]
        public int MaxSize { get; set; }

        [JsonProperty("settings")]
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>A party and its members in the order they joined, which is also the order leadership passes in.</summary>
    internal sealed class PartyDetailRecord : PartyRecord
    {
        [JsonProperty("members")]
        public List<PartyMemberRecord> Members { get; set; }
    }

    internal sealed class PartyMemberRecord
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }
    }

    /// <summary>A party's size and settings as a create or an update sends them; what is not given is left out.</summary>
    internal sealed class PartySizeAndSettingsBody
    {
        [JsonProperty("max_size", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxSize { get; set; }

        [JsonProperty("settings", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyDictionary<string, object> Settings { get; set; }
    }

    internal sealed class JoinPartyBody
    {
        [JsonProperty("invite_code")]
        public string InviteCode { get; set; }
    }

    /// <summary>The player a kick or a leadership change names.</summary>
    internal sealed class PartyPlayerBody
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }
    }
}
