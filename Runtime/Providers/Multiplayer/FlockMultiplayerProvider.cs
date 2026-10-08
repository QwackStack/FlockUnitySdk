using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>The entry point for multiplayer; make its calls from the main thread.</summary>
    public class FlockMultiplayerProvider : FlockProviderBase
    {
        private readonly FlockRepeatingCalls _repeatingCalls;
        private readonly FlockMatchmakingQueueNames _queueNames;
        private readonly FlockParties _parties;
        private readonly FlockMultiplayerSessions _sessions;
        private readonly FlockMatchmaking _matchmaking;
        private FlockBehaviour _frames;

        public FlockMultiplayerProvider(FlockClient client) : base(client)
        {
            _repeatingCalls = new FlockRepeatingCalls(client);
            _queueNames = new FlockMatchmakingQueueNames(ReadQueuesAsync, client.Logger);
            _sessions = new FlockMultiplayerSessions(client, _repeatingCalls);
            _matchmaking = new FlockMatchmaking(client, _repeatingCalls, _sessions, FindQueueIdAsync);
            _parties = new FlockParties(client, _repeatingCalls, _matchmaking, client.InitConfig.PartyRefreshInterval);
        }

        internal FlockRepeatingCalls RepeatingCalls => _repeatingCalls;
        internal FlockParties Parties => _parties;
        internal FlockMultiplayerSessions Sessions => _sessions;
        internal FlockMatchmaking Matchmaking => _matchmaking;

        /// <summary>Makes a party with the signed-in player as its leader and only member. Leave <paramref name="maxSize"/> null for the server's default (4); a player can be in one party at a time.</summary>
        /// <param name="maxSize">From 2 to 64 players.</param>
        /// <param name="settings">The game's own data on the party, such as a mode; the players in it read it.</param>
        public Task<FlockParty> CreatePartyAsync(int? maxSize = null, IReadOnlyDictionary<string, object> settings = null, CancellationToken cancellationToken = default)
            => _parties.CreateAsync(maxSize, settings, cancellationToken);

        /// <summary>Joins the party with this invite code; letter case does not matter. Joining the party the player is already in returns it.</summary>
        public Task<FlockParty> JoinPartyAsync(string inviteCode, CancellationToken cancellationToken = default)
            => _parties.JoinAsync(inviteCode, cancellationToken);

        /// <summary>The signed-in player's party, read now, or null when the player is in none. A party lasts until the player leaves it, across launches, so call this after signing in. Returns the same object each time while it lasts.</summary>
        public Task<FlockParty> GetMyPartyAsync(CancellationToken cancellationToken = default)
            => _parties.GetMineAsync(cancellationToken);

        /// <summary>Hosts a session: the signed-in player is its host and only player, and others join with its join code. Leave <paramref name="maxPlayers"/> null for the game's default (8). A player holds one seat at a time, so a session held until now ends.</summary>
        /// <param name="maxPlayers">From 2 to 16 players.</param>
        /// <param name="data">The game's own data on the session, such as a map; set once, read by every player.</param>
        /// <param name="sameVersionOnly">True (the default) refuses players whose game sends another Game Version with <c>MultiplayerVersionMismatch</c>; false lets any build join.</param>
        public Task<FlockMultiplayerSession> HostSessionAsync(int? maxPlayers = null, IReadOnlyDictionary<string, object> data = null, bool sameVersionOnly = true, CancellationToken cancellationToken = default)
            => _sessions.HostAsync(maxPlayers, data, sameVersionOnly, cancellationToken);

        /// <summary>The session the player is seated in, found without its id (after a restart, or a session a party leader made), or null when seated nowhere. Returns the same object while the session lasts.</summary>
        public Task<FlockMultiplayerSession> GetMySessionAsync(CancellationToken cancellationToken = default)
            => _sessions.GetMineAsync(cancellationToken);

        /// <summary>Takes a seat in the session with this join code; letter case does not matter. Joining the session the player is seated in returns it; a session held until now ends. A session kept to its host's Game Version refuses another build with <c>MultiplayerVersionMismatch</c>.</summary>
        public Task<FlockMultiplayerSession> JoinSessionAsync(string joinCode, CancellationToken cancellationToken = default)
            => _sessions.JoinAsync(joinCode, cancellationToken);

        /// <summary>A session the player holds or once held a seat in, read now. One the player is no longer seated in comes back ended: with the session's own reason once the session is over (a host who left last reads <c>host_left</c>), otherwise <c>left</c> or <c>dropped</c>. Any other is refused with <c>MultiplayerSessionNotFound</c>.</summary>
        public Task<FlockMultiplayerSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => _sessions.GetAsync(sessionId, cancellationToken);

        /// <summary>Searches for a match alone in the queue named exactly <paramref name="queueName"/> (letter case included), and hands back the session the match seats the player in, held like any other. A search the server ends without a match (nobody found within 5 minutes, or another game of this player stopping it) comes back as its outcome, not an error. Cancelling the token cancels the search on the server; a match that landed meanwhile gives its seat up. A search of the player's own left from an earlier launch is cancelled first. To search as a party, use <see cref="FlockParty.FindMatchAsync"/>.</summary>
        public Task<FlockMatchmakingResult> FindMatchAsync(string queueName, FlockMatchmakingOptions options = null, CancellationToken cancellationToken = default)
            => _matchmaking.FindAloneAsync(queueName, options, cancellationToken);

        /// <summary>The id of the matchmaking queue named exactly <paramref name="queueName"/>, found without starting a search.</summary>
        internal Task<string> FindQueueIdAsync(string queueName, CancellationToken cancellationToken)
        {
            RequireNotEmpty(queueName, "Queue Name");
            return _queueNames.FindIdAsync(queueName, cancellationToken);
        }

        // Repeating calls run on the game's frames, so a web player needs no timers. Called once from FlockClient init.
        internal void StartFrameUpdates()
        {
            if (_frames != null || !Application.isPlaying)
                return;
            FlockBehaviour behaviour = FlockBehaviour.Instance;
            if (behaviour == null)
                return;

            _frames = behaviour;
            _frames.OnTick += HandleFrame;
            _frames.OnQuit += HandleQuit;
        }

        /// <summary>Stops every repeating call and lets go of the game's frames; runs when Flock shuts down.</summary>
        internal void StopForShutdown()
        {
            if (_frames != null)
            {
                _frames.OnTick -= HandleFrame;
                _frames.OnQuit -= HandleQuit;
                _frames = null;
            }
            _parties.StopForShutdown();
            _matchmaking.StopForShutdown();
            _sessions.StopForShutdown();
            _repeatingCalls.StopAll();
        }

        private void HandleFrame()
        {
            _parties.EndIfSignInEnded();
            _matchmaking.EndIfSignInEnded();
            _sessions.EndIfSignInEnded();
            _repeatingCalls.SendThoseDue();
        }

        private void HandleQuit()
        {
            _repeatingCalls.StopAll();
            _matchmaking.CancelForQuit();
            _sessions.LeaveForQuit();
        }

        private Task<List<MatchmakingQueue>> ReadQueuesAsync(CancellationToken cancellationToken)
        {
            return ExecuteAsync(async () =>
            {
                GenericResponse<List<MatchmakingQueue>> response = await FlockHttpClient.GetAsync<GenericResponse<List<MatchmakingQueue>>>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.MatchmakingQueues}?limit={FlockMatchmakingQueueNames.MostQueuesListed}",
                    Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, "Read matchmaking queues", cancellationToken);
        }
    }
}
