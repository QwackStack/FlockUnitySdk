using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Providers
{
    /// <summary>Why a session stopped being the player's. An open set: the server can add reasons, so compare with these and keep a default.</summary>
    public static class FlockMultiplayerSessionEndReason
    {
        /// <summary>The host ended it.</summary>
        public const string EndedByHost = "ended_by_host";
        /// <summary>The host left and nobody was left to take over.</summary>
        public const string HostLeft = "host_left";
        /// <summary>It reached the game's longest session age.</summary>
        public const string Expired = "expired";
        /// <summary>Every player left.</summary>
        public const string Empty = "empty";
        /// <summary>The player left it, with <see cref="FlockMultiplayerSession.LeaveAsync"/> or from another device.</summary>
        public const string Left = "left";
        /// <summary>The server stopped hearing from the player and gave the seat up.</summary>
        public const string Dropped = "dropped";
        /// <summary>The player hosted or joined another session, which gives this seat up.</summary>
        public const string MovedToAnotherSession = "moved_to_another_session";
        /// <summary>The sign-in the session belonged to ended: a sign-out, another player signing in, or Flock shutting down.</summary>
        public const string SignedOut = "signed_out";
    }

    /// <summary>One player seated in a session.</summary>
    public sealed class FlockMultiplayerSessionPlayer
    {
        internal FlockMultiplayerSessionPlayer(string playerId)
        {
            PlayerId = playerId;
        }

        public string PlayerId { get; }
    }

    /// <summary>How to reach the session's host, as the host published it: a mode and whatever values that mode needs.</summary>
    public sealed class FlockMultiplayerSessionConnection
    {
        internal FlockMultiplayerSessionConnection(string mode, IReadOnlyDictionary<string, object> values)
        {
            Mode = mode;
            Values = values;
        }

        /// <summary>What kind of connection this is, such as "direct", or one the game names itself.</summary>
        public string Mode { get; }

        /// <summary>Everything the host published, the mode included.</summary>
        public IReadOnlyDictionary<string, object> Values { get; }

        /// <summary>Reads one value as <typeparamref name="T"/>. False when the key is absent or the value cannot become a T.</summary>
        public bool TryGetValue<T>(string key, out T value)
        {
            value = default;
            if (string.IsNullOrEmpty(key) || !Values.TryGetValue(key, out object raw))
                return false;
            return FlockJsonValues.TryConvert(raw, out value);
        }
    }

    /// <summary>A short-lived proof that the player holds a seat, to show the host when connecting.</summary>
    public sealed class FlockMultiplayerSessionJoinToken
    {
        internal FlockMultiplayerSessionJoinToken(string token, int expiresInSeconds)
        {
            Token = token;
            ExpiresInSeconds = expiresInSeconds;
        }

        public string Token { get; }

        /// <summary>How long the token is good for from when it was given (60 s).</summary>
        public int ExpiresInSeconds { get; }
    }

    /// <summary>A multiplayer session the player holds a seat in, kept seated by heartbeats while it lasts and updated from their answers and every call's; use it from the main thread.</summary>
    public sealed class FlockMultiplayerSession
    {
        // A third of the shorter of the server's default timeouts (60 s host, 90 s seat), for an answer that names no interval.
        private static readonly TimeSpan HeartbeatWhenNoneIsGiven = TimeSpan.FromSeconds(20);

        private readonly FlockMultiplayerSessions _owner;
        private readonly string _playerId;
        // Set once, by the owner, when the session ends; a sign-in that ended reads as signed_out before then.
        private string _endReason;

        internal FlockMultiplayerSession(FlockMultiplayerSessions owner, int signInNumber, string playerId, SessionRecord session)
        {
            _owner = owner;
            SignInNumber = signInNumber;
            _playerId = playerId;
            Id = session.Id;
            CopyFrom(session);
        }

        public string Id { get; }

        /// <summary>The code other players join with, through <see cref="FlockMultiplayerProvider.JoinSessionAsync"/>. Letter case does not matter.</summary>
        public string JoinCode { get; private set; }

        public string HostPlayerId { get; private set; }

        /// <summary>True when the signed-in player hosts: only the host can publish the connection, end the session, hand hosting over or verify join tokens.</summary>
        public bool IsHost => string.Equals(HostPlayerId, _playerId, StringComparison.Ordinal);

        /// <summary>The players seated now, longest seated first: when the host leaves, the first of the others hosts. A new list on every change.</summary>
        public IReadOnlyList<FlockMultiplayerSessionPlayer> Players { get; private set; }

        public int MaxPlayers { get; private set; }

        /// <summary>The game's own data on the session, set when it was hosted. Read one value with <see cref="TryGetData{T}"/>.</summary>
        public IReadOnlyDictionary<string, object> Data { get; private set; }

        /// <summary>How to reach the host, or null until the host publishes it. When hosting moves it is cleared until the new host publishes; <see cref="ConnectionChanged"/> is raised both times.</summary>
        public FlockMultiplayerSessionConnection Connection { get; private set; }

        /// <summary>Why the session ended (a <see cref="FlockMultiplayerSessionEndReason"/> value, or a reason the server added), or null while it lasts.</summary>
        public string EndReason => _endReason ?? (SignInNumber != _owner.CurrentSignInNumber ? FlockMultiplayerSessionEndReason.SignedOut : null);

        /// <summary>True once the session is no longer the player's here; its calls are then refused.</summary>
        public bool HasEnded => EndReason != null;

        /// <summary>The seated players changed.</summary>
        public event Action PlayersChanged;

        /// <summary>Another player hosts the session now.</summary>
        public event Action HostChanged;

        /// <summary>The host published how to reach it or published again, or hosting moved and the address was cleared.</summary>
        public event Action ConnectionChanged;

        /// <summary>The session stopped being the player's, raised once with the reason. Not raised when Flock shuts down or the game quits.</summary>
        public event Action<string> Ended;

        internal int SignInNumber { get; }
        internal string PlayerId => _playerId;
        internal string EndReasonSet => _endReason;
        internal int HostEpoch { get; private set; }
        internal int ConnectionEpoch { get; private set; }
        // How often to heartbeat, as the server says for the game's timeouts.
        internal TimeSpan HeartbeatInterval { get; private set; }

        /// <summary>Reads one <see cref="Data"/> value as <typeparamref name="T"/>. False when the key is absent or the value cannot become a T.</summary>
        public bool TryGetData<T>(string key, out T value)
        {
            value = default;
            if (string.IsNullOrEmpty(key) || !Data.TryGetValue(key, out object raw))
                return false;
            return FlockJsonValues.TryConvert(raw, out value);
        }

        /// <summary>Gives up the player's seat. If the server says the player is already out, that counts as leaving. When the host leaves, the longest seated of the others hosts.</summary>
        public Task LeaveAsync(CancellationToken cancellationToken = default) => _owner.LeaveAsync(this, cancellationToken);

        /// <summary>Ends the session for every player. Host only.</summary>
        public Task EndAsync(CancellationToken cancellationToken = default) => _owner.EndAsync(this, cancellationToken);

        /// <summary>Hands hosting to another seated player, who then publishes their own connection. Host only.</summary>
        public Task MakeHostAsync(string playerId, CancellationToken cancellationToken = default) => _owner.MakeHostAsync(this, playerId, cancellationToken);

        /// <summary>Says how to reach the host: a mode (32 characters at most) and the values it needs; under 4 KB in all. Replaces what was published before. Host only.</summary>
        public Task PublishConnectionAsync(string mode, IReadOnlyDictionary<string, object> values = null, CancellationToken cancellationToken = default)
            => _owner.PublishConnectionAsync(this, mode, values, cancellationToken);

        /// <summary>A token proving the player holds a seat, to send the host when connecting; good for 60 s.</summary>
        public Task<FlockMultiplayerSessionJoinToken> RequestJoinTokenAsync(CancellationToken cancellationToken = default) => _owner.RequestJoinTokenAsync(this, cancellationToken);

        /// <summary>Checks a token a connecting player sent and returns that player's id; refused with <c>MultiplayerInvalidJoinToken</c> when it is not good for this session. Host only.</summary>
        public Task<string> VerifyJoinTokenAsync(string token, CancellationToken cancellationToken = default) => _owner.VerifyJoinTokenAsync(this, token, cancellationToken);

        // Takes a newer reading of the session, then says what changed; events are raised once everything is in place.
        internal void Update(SessionRecord session)
        {
            IReadOnlyList<string> seated = SeatedPlayers(session);
            bool playersChanged = !SamePlayers(seated);
            bool hostChanged = session.HostEpoch != HostEpoch || !string.Equals(session.HostPlayerId, HostPlayerId, StringComparison.Ordinal);
            bool connectionEpochMoved = session.ConnectionEpoch != ConnectionEpoch;
            bool hadConnection = Connection != null;
            CopyFrom(session);
            // A publish moves the epoch; hosting moving on clears the address without moving it.
            bool connectionChanged = connectionEpochMoved || hadConnection != (Connection != null);

            if (playersChanged)
                FlockEvents.InvokeEach(PlayersChanged, $"{nameof(FlockMultiplayerSession)}.{nameof(PlayersChanged)}");
            if (hostChanged)
                FlockEvents.InvokeEach(HostChanged, $"{nameof(FlockMultiplayerSession)}.{nameof(HostChanged)}");
            if (connectionChanged)
                FlockEvents.InvokeEach(ConnectionChanged, $"{nameof(FlockMultiplayerSession)}.{nameof(ConnectionChanged)}");
        }

        // The last reading of a session that has ended for the player: kept for what it says, without raising changes.
        internal void TakeFinalReading(SessionRecord session) => CopyFrom(session);

        internal void End(string reason, bool raise)
        {
            _endReason = reason;
            if (raise)
                FlockEvents.InvokeEach(Ended, reason, $"{nameof(FlockMultiplayerSession)}.{nameof(Ended)}");
        }

        internal static IReadOnlyList<string> SeatedPlayers(SessionRecord session)
        {
            List<string> seated = new List<string>();
            if (session.Participants != null)
            {
                foreach (SessionParticipantRecord participant in session.Participants)
                {
                    if (participant.Status == SessionWireValues.SeatJoined)
                        seated.Add(participant.PlayerId);
                }
            }
            return seated;
        }

        private void CopyFrom(SessionRecord session)
        {
            JoinCode = session.JoinCode;
            HostPlayerId = session.HostPlayerId;
            HostEpoch = session.HostEpoch;
            MaxPlayers = session.MaxPlayers;
            Data = ReadOnlyCopy(session.Data);

            List<FlockMultiplayerSessionPlayer> players = new List<FlockMultiplayerSessionPlayer>();
            foreach (string playerId in SeatedPlayers(session))
                players.Add(new FlockMultiplayerSessionPlayer(playerId));
            Players = players.AsReadOnly();

            ConnectionEpoch = session.ConnectionEpoch;
            HeartbeatInterval = session.HeartbeatIntervalSeconds > 0 ? TimeSpan.FromSeconds(session.HeartbeatIntervalSeconds.Value) : HeartbeatWhenNoneIsGiven;
            // The server blanks the address for a player without a seat, and sends none before the host first publishes.
            Connection = session.ConnectionInfo != null && session.ConnectionInfo.TryGetValue("mode", out object mode)
                ? new FlockMultiplayerSessionConnection(mode as string, ReadOnlyCopy(session.ConnectionInfo))
                : null;
        }

        private bool SamePlayers(IReadOnlyList<string> seated)
        {
            if (Players.Count != seated.Count)
                return false;
            for (int i = 0; i < seated.Count; i++)
            {
                if (!string.Equals(Players[i].PlayerId, seated[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static IReadOnlyDictionary<string, object> ReadOnlyCopy(Dictionary<string, object> source)
        {
            return new ReadOnlyDictionary<string, object>(source == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(source));
        }
    }

    /// <summary>The server's words for a seat and for a session that is over.</summary>
    internal static class SessionWireValues
    {
        internal const string SeatJoined = "joined";
        internal const string SeatDropped = "dropped";
        internal const string SessionEnded = "ended";
    }
}
