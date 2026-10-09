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

        [JsonProperty("heartbeat_interval_seconds")]
        public int? HeartbeatIntervalSeconds { get; set; }
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

        // Sent only as false: the server keeps a session to its host's game version unless told otherwise.
        [JsonProperty("same_version_only", NullValueHandling = NullValueHandling.Ignore)]
        public bool? SameVersionOnly { get; set; }
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

    /// <summary>The servers a player may connect through: relay entries with credentials minted for them, and STUN entries with none.</summary>
    internal sealed class RelayCredentialsRecord
    {
        [JsonProperty("ttl")]
        public int Ttl { get; set; }

        [JsonProperty("ice_servers")]
        public List<IceServerRecord> IceServers { get; set; }

        [JsonProperty("relay_paused")]
        public bool RelayPaused { get; set; }
    }

    internal sealed class IceServerRecord
    {
        [JsonProperty("urls")]
        public List<string> Urls { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("credential")]
        public string Credential { get; set; }
    }

    // Asked with no session named, so a direct connection is not counted as relay use of the player's seat.
    internal sealed class RelayCredentialsBody
    {
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

    /// <summary>A player's matchmaking ticket, as the ticket routes answer it; the reads also carry the match once there is one.</summary>
    internal sealed class TicketRecord
    {
        internal const string Queued = "queued";
        internal const string Matched = "matched";
        internal const string Cancelled = "cancelled";
        internal const string Expired = "expired";

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("queue_id")]
        public string QueueId { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("party_id")]
        public string PartyId { get; set; }

        [JsonProperty("cancel_reason")]
        public string CancelReason { get; set; }

        [JsonProperty("match")]
        public MatchRecord Match { get; set; }
    }

    internal sealed class MatchRecord
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("player_ids")]
        public List<string> PlayerIds { get; set; }

        [JsonProperty("connection_info")]
        public MatchConnectionRecord ConnectionInfo { get; set; }
    }

    /// <summary>Where a match's players meet: the session the match made, unless the game's settings make none.</summary>
    internal sealed class MatchConnectionRecord
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }
    }

    internal sealed class CreateTicketBody
    {
        [JsonProperty("queue_id")]
        public string QueueId { get; set; }

        [JsonProperty("attributes", NullValueHandling = NullValueHandling.Ignore)]
        public IReadOnlyDictionary<string, object> Attributes { get; set; }

        [JsonProperty("party_id", NullValueHandling = NullValueHandling.Ignore)]
        public string PartyId { get; set; }
    }
}
