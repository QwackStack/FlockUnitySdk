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

    /// <summary>The player a kick or a change of leader or host names.</summary>
    internal sealed class PlayerIdBody
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }
    }

    /// <summary>A multiplayer session and every player who held a seat in it, in seniority order, as every session route answers it.</summary>
    internal sealed class SessionRecord
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("ended_reason")]
        public string EndedReason { get; set; }

        [JsonProperty("host_player_id")]
        public string HostPlayerId { get; set; }

        [JsonProperty("host_epoch")]
        public int HostEpoch { get; set; }

        [JsonProperty("join_code")]
        public string JoinCode { get; set; }

        [JsonProperty("max_players")]
        public int MaxPlayers { get; set; }

        [JsonProperty("connection_info")]
        public Dictionary<string, object> ConnectionInfo { get; set; }

        [JsonProperty("connection_epoch")]
        public int ConnectionEpoch { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("participants")]
        public List<SessionParticipantRecord> Participants { get; set; }
    }

    internal sealed class SessionParticipantRecord
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }

    internal sealed class HostSessionBody
    {
        [JsonProperty("max_players", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxPlayers { get; set; }

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyDictionary<string, object> Data { get; set; }
    }

    internal sealed class JoinSessionBody
    {
        [JsonProperty("join_code")]
        public string JoinCode { get; set; }
    }

    internal sealed class PublishConnectionBody
    {
        [JsonProperty("connection_info")]
        public Dictionary<string, object> ConnectionInfo { get; set; }
    }

    internal sealed class JoinTokenRecord
    {
        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }
    }

    internal sealed class JoinTokenBody
    {
        [JsonProperty("token")]
        public string Token { get; set; }
    }

    internal sealed class VerifiedJoinTokenRecord
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }
    }
}
