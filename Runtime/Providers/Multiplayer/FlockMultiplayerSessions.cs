using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>The one owner of the player's session: at most one held per sign-in, changed only by the newest answer; runs on the main thread.</summary>
    internal sealed class FlockMultiplayerSessions
    {
        internal const string HeartbeatCallName = "Session heartbeat";
        internal const string ConnectionWaitCallName = "Session connection wait";
        internal const string RelayedPlayersReadCallName = "Session read for relayed players";
        // While a game waits for the host's address, the session is read this often; heartbeats alone come every 20 s.
        internal static readonly TimeSpan DefaultConnectionWaitInterval = TimeSpan.FromSeconds(2);
        // While a host takes players through its relay, the session is read this often, so a joining player's relay address is let in within seconds.
        internal static readonly TimeSpan DefaultRelayedPlayersReadInterval = TimeSpan.FromSeconds(2);
        // A read overtaken this many times in a row means the session is changing faster than it can be read.
        private const int MostReadsWhileTheSessionChanges = 5;

        private readonly FlockClient _client;
        private readonly FlockMultiplayerSessionRequests _requests;
        private readonly FlockRepeatingCalls _repeatingCalls;
        private readonly FlockDirectAddresses _directAddresses;
        // Each call waiting for the host's address, woken when one is published, the session ends, or Flock shuts down.
        private readonly List<TaskCompletionSource<bool>> _connectionWaiters = new List<TaskCompletionSource<bool>>();
        private TimeSpan _connectionWaitInterval = DefaultConnectionWaitInterval;
        private FlockMultiplayerSession _held;
        private FlockRepeatingCall _heartbeat;
        private FlockRepeatingCall _connectionWait;
        private TimeSpan _relayedPlayersReadInterval = DefaultRelayedPlayersReadInterval;
        private FlockRepeatingCall _relayedPlayersRead;
        // The relay the read for relayed players runs for.
        private FlockRelayConnection _relayedPlayersReadRelay;
        private int _readsSent;
        // Reads numbered at or below this change nothing when they land.
        private int _readsSettledThrough;
        // The session a leave is on its way for: a reading that finds the player out of that session is the leave's doing.
        private string _leavingSessionId;
        // Flock shut down or was reset: an answer still on its way holds nothing.
        private bool _stopped;

        internal FlockMultiplayerSessions(FlockClient client, FlockRepeatingCalls repeatingCalls)
        {
            _client = client;
            _requests = new FlockMultiplayerSessionRequests(client);
            _repeatingCalls = repeatingCalls;
            _directAddresses = new FlockDirectAddresses(client, _requests);
        }

        internal int CurrentSignInNumber => _client.SignInNumber;
        internal FlockMultiplayerSession Held => _held;

        /// <summary>From the game's frames: tells the held session's game once when its relay stopped by itself.</summary>
        internal void TellIfTheRelayWasLost() => _held?.TellIfTheRelayWasLost();
        internal FlockDirectAddresses DirectAddresses => _directAddresses;

        /// <summary>How the relay's steps are timed; a test shortens them.</summary>
        internal FlockRelayConnection.Timing RelayTiming { get; } = new FlockRelayConnection.Timing();

        internal void SetConnectionWaitIntervalForTesting(TimeSpan interval) => _connectionWaitInterval = interval;

        internal void SetRelayedPlayersReadIntervalForTesting(TimeSpan interval) => _relayedPlayersReadInterval = interval;

        internal bool ReadsForRelayedPlayersForTesting => _relayedPlayersRead != null && _relayedPlayersRead.IsRunning;

        /// <summary>The interval the running heartbeat uses, or null when none runs.</summary>
        internal TimeSpan? HeartbeatIntervalForTesting => _heartbeat != null && _heartbeat.IsRunning ? _heartbeat.Interval : (TimeSpan?)null;

        internal async Task<FlockMultiplayerSession> HostAsync(int? maxPlayers, IReadOnlyDictionary<string, object> data, bool sameVersionOnly, CancellationToken cancellationToken)
        {
            int signInNumber = RequireSignedIn(out string playerId);
            SessionRecord hosted = await _requests.HostAsync(signInNumber, maxPlayers, data, sameVersionOnly, cancellationToken);
            RequireStillCurrent(signInNumber);
            return TakeAnswerOfChange(hosted, signInNumber, playerId);
        }

        internal async Task<FlockMultiplayerSession> JoinAsync(string joinCode, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(joinCode, "Join Code");
            int signInNumber = RequireSignedIn(out string playerId);
            SessionRecord joined = await _requests.JoinAsync(signInNumber, joinCode, cancellationToken);
            RequireStillCurrent(signInNumber);
            return TakeAnswerOfChange(joined, signInNumber, playerId);
        }

        /// <summary>A session the player holds or once held a seat in, read now; one the player is no longer seated in comes back ended.</summary>
        internal async Task<FlockMultiplayerSession> GetAsync(string sessionId, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(sessionId, "Session ID");
            int signInNumber = RequireSignedIn(out string playerId);
            return await ReadUntilNewestAsync(signInNumber, playerId, sessionId, () => _requests.ReadAsync(signInNumber, sessionId, cancellationToken), cancellationToken);
        }

        /// <summary>The session the player is seated in now, found without its id, or null when seated nowhere.</summary>
        internal async Task<FlockMultiplayerSession> GetMineAsync(CancellationToken cancellationToken)
        {
            int signInNumber = RequireSignedIn(out string playerId);
            return await ReadUntilNewestAsync(signInNumber, playerId, null, () => ReadMineOrNoneAsync(signInNumber, cancellationToken), cancellationToken);
        }

        internal async Task LeaveAsync(FlockMultiplayerSession session, CancellationToken cancellationToken)
        {
            RequireRunning(session);
            _leavingSessionId = session.Id;
            try
            {
                SessionRecord left = await _requests.LeaveAsync(session.SignInNumber, session.Id, cancellationToken);
                TakeAnswerOfChange(left, session.SignInNumber, null);
            }
            catch (FlockException alreadyOut) when (IsOutOfTheSession(alreadyOut))
            {
                // Out of the session already, which is what leaving asks for; a retried leave whose first answer was lost lands here too.
            }
            finally
            {
                ForgetLeaveOf(session.Id);
            }
            End(session, FlockMultiplayerSessionEndReason.Left);
        }

        // Only a session's own leave answer forgets that leave, so an earlier leave answered late leaves a newer one in place.
        private void ForgetLeaveOf(string sessionId)
        {
            if (_leavingSessionId == sessionId)
                _leavingSessionId = null;
        }

        // Why a session the player is found out of ended: this game's own leave of that session, else what was found.
        private string EndingOf(FlockMultiplayerSession session, string found)
            => session.Id == _leavingSessionId ? FlockMultiplayerSessionEndReason.Left : found;

        internal Task EndAsync(FlockMultiplayerSession session, CancellationToken cancellationToken)
            => ChangeAsync(session, () => _requests.EndAsync(session.SignInNumber, session.Id, cancellationToken), cancellationToken);

        internal async Task MakeHostAsync(FlockMultiplayerSession session, string playerId, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(playerId, "Player ID");
            await ChangeAsync(session, () => _requests.MakeHostAsync(session.SignInNumber, session.Id, playerId, cancellationToken), cancellationToken);
        }

        internal async Task PublishConnectionAsync(FlockMultiplayerSession session, string mode, IReadOnlyDictionary<string, object> values, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(mode, "Mode");
            Dictionary<string, object> connection = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
            connection["mode"] = mode;
            await ChangeAsync(session, () => _requests.PublishConnectionAsync(session.SignInNumber, session.Id, connection, cancellationToken), cancellationToken);
        }

        // The backend checks nothing of a direct descriptor, so what it holds is checked here: a port, and an address players can use.
        internal async Task PublishDirectConnectionAsync(FlockMultiplayerSession session, int port, CancellationToken cancellationToken)
        {
            if (port < 1 || port > 65535)
                throw new FlockValidationException($"The port must be from 1 to 65535, not {port}") { Hint = "Give the port the game's netcode listens on." };
            RequireRunning(session);
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                throw new FlockValidationException("A web player cannot be reached directly")
                {
                    Hint = "Host from a desktop or phone build, or publish a connection of the game's own with PublishConnectionAsync."
                };
            FlockDirectAddresses.DeviceAddresses found = await _directAddresses.FindAsync(session.SignInNumber, cancellationToken);
            if (found.Lan == null && found.Public == null)
                throw new FlockNetworkException("This device has no network address to publish") { Hint = "Connect the device to a network, then publish the connection again." };
            Dictionary<string, object> values = new Dictionary<string, object>();
            AddDirectAddresses(values, found, port);
            await PublishConnectionAsync(session, FlockMultiplayerConnectionMode.Direct, values, cancellationToken);
        }

        // The host's address on the relay, and with withDirectAddresses its own addresses too, so players on its network skip the relay.
        internal async Task PublishRelayConnectionAsync(FlockMultiplayerSession session, FlockRelayConnection relay, int port, bool withDirectAddresses, CancellationToken cancellationToken)
        {
            RequireRunning(session);
            Dictionary<string, object> values = new Dictionary<string, object>
            {
                [RelayConnectionValues.RelayAddress] = relay.Address.ToString(),
                [RelayConnectionValues.RelayServer] = relay.Server,
            };
            if (withDirectAddresses)
                AddDirectAddresses(values, await _directAddresses.FindAsync(session.SignInNumber, cancellationToken), port);
            await PublishConnectionAsync(session, FlockMultiplayerConnectionMode.Relay, values, cancellationToken);
        }

        // A player's address on the relay, published for its own seat so the host's relay lets it in: a relay may give each address an
        // IP of its own (Cloudflare's does, measured), and lets packets in only from IPs it was opened to. Skipped when the last reading holds it.
        internal async Task PublishSeatRelayAddressAsync(FlockMultiplayerSession session, FlockRelayConnection relay, CancellationToken cancellationToken)
        {
            RequireRunning(session);
            if (session.SeatRelayAddresses.TryGetValue(session.PlayerId, out FlockRelayPeer published) && published.Equals(relay.Address))
                return;
            Dictionary<string, object> values = new Dictionary<string, object>
            {
                ["mode"] = FlockMultiplayerConnectionMode.Relay,
                [RelayConnectionValues.RelayAddress] = relay.Address.ToString(),
            };
            await ChangeAsync(session, () => _requests.PublishSeatConnectionAsync(session.SignInNumber, session.Id, values, cancellationToken), cancellationToken);
        }

        // A host's relay lets in every seated player's relay address its seat names, at each reading; the connection skips an IP already open or asked.
        private void OpenTheRelayToSeatedPlayers(FlockMultiplayerSession session)
        {
            FlockRelayConnection relay = session.Relay;
            if (session.HasEnded || !session.IsHost || relay == null || !relay.IsOpen)
                return;
            foreach (KeyValuePair<string, FlockRelayPeer> seat in session.SeatRelayAddresses)
            {
                if (!string.Equals(seat.Key, session.PlayerId, StringComparison.Ordinal) && !relay.IsOpenTo(seat.Value) && !relay.RefusedToOpenTo(seat.Value))
                    LetRun(OpenToSeatedPlayerAsync(relay, seat.Value));
            }
        }

        // A refusal is told once: an IP the relay refused is skipped at later readings. A relay that did not answer is asked again at the next one.
        private async Task OpenToSeatedPlayerAsync(FlockRelayConnection relay, FlockRelayPeer player)
        {
            try
            {
                await relay.OpenToAsync(player);
            }
            catch (FlockRelayException refused) when (refused.Reason == FlockRelayFailure.OpenRefused)
            {
                _client.Logger.LogWarning($"A player's relay address cannot be let in, so that player cannot join through Flock's relay: {refused.Message}");
            }
        }

        /// <summary>While the held session's host takes players through its relay (a host bridge hears what arrives), the session is read every 2 s, so a joining player's relay address is let in within seconds; heartbeats alone come every 20 s. Run once a frame.</summary>
        internal void KeepReadingForRelayedPlayers()
        {
            FlockMultiplayerSession held = _held;
            FlockRelayConnection relay = held?.Relay;
            bool wanted = held != null && !_stopped && held.IsHost && relay != null && relay.IsOpen && relay.HandsArrivalsOver && !relay.ReadForPlayersRefused;
            if (!wanted || relay != _relayedPlayersReadRelay)
                StopReadingForRelayedPlayers();
            if (!wanted || (_relayedPlayersRead != null && _relayedPlayersRead.IsRunning))
                return;
            string sessionId = held.Id;
            int signInNumber = held.SignInNumber;
            _relayedPlayersReadRelay = relay;
            _relayedPlayersRead = _repeatingCalls.Start(
                RelayedPlayersReadCallName,
                _relayedPlayersReadInterval,
                cancellationToken => ReadNumberedAsync(signInNumber, sessionId, cancellationToken),
                TakeReading,
                failure => RelayedPlayersReadStopped(sessionId, relay, failure));
        }

        private void StopReadingForRelayedPlayers()
        {
            _relayedPlayersRead?.Stop();
            _relayedPlayersRead = null;
            _relayedPlayersReadRelay = null;
        }

        // Not started again for this relay whatever the refusal, or the frames would send it again until the reason landed. A seat the
        // server no longer has ends the session; any other refusal leaves it to the heartbeats' answers.
        private void RelayedPlayersReadStopped(string sessionId, FlockRelayConnection relay, Exception failure)
        {
            relay.ReadForPlayersRefused = true;
            FlockMultiplayerSession held = _held;
            if (held == null || held.Id != sessionId)
                return;
            if (failure is FlockException coded && IsOutOfTheSession(coded))
            {
                LetRun(LearnWhyItEndedAsync(held, CancellationToken.None));
                return;
            }
            _client.Logger.LogWarning($"The session is no longer read every 2 s for players joining through Flock's relay: {failure.Message}. The heartbeats still bring their relay addresses, about every 20 s.");
        }

        // An unknown address is left out; with neither known, nothing is added.
        private static void AddDirectAddresses(Dictionary<string, object> values, FlockDirectAddresses.DeviceAddresses found, int port)
        {
            if (found.Lan == null && found.Public == null)
                return;
            values[DirectConnectionValues.Address] = found.Public ?? found.Lan;
            values[DirectConnectionValues.Port] = port;
            if (found.Lan != null)
                values[DirectConnectionValues.LanAddress] = found.Lan;
            if (found.Public != null)
                values[DirectConnectionValues.PublicAddress] = found.Public;
        }

        // Woken by the host publishing, the session ending, or Flock shutting down; the session is read every 2 s meanwhile.
        internal async Task<FlockMultiplayerSessionConnection> WaitForConnectionAsync(FlockMultiplayerSession session, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
                throw new FlockValidationException("WaitForConnectionAsync needs a time to wait that is longer than zero") { Hint = "Pass how long to wait for the host's connection, such as 30 seconds." };
            RequireRunning(session);
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Connection != null)
                return session.Connection;

            TaskCompletionSource<bool> woken = new TaskCompletionSource<bool>();
            Action published = () =>
            {
                if (session.Connection != null)
                    woken.TrySetResult(true);
            };
            Action<string> ended = reason => woken.TrySetResult(true);
            session.ConnectionChanged += published;
            session.Ended += ended;
            _connectionWaiters.Add(woken);
            KeepReadingForConnection();
            CancellationTokenSource stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Task timeUp = FlockWaiting.DelayAsync(timeout, stopWaiting.Token);
                await Task.WhenAny(woken.Task, timeUp);
            }
            finally
            {
                stopWaiting.Cancel();
                stopWaiting.Dispose();
                session.ConnectionChanged -= published;
                session.Ended -= ended;
                _connectionWaiters.Remove(woken);
                if (_connectionWaiters.Count == 0)
                    StopReadingForConnection();
            }
            if (woken.Task.IsCanceled)
                throw new OperationCanceledException("Flock shut down while waiting for the host's connection");
            cancellationToken.ThrowIfCancellationRequested();
            return session.HasEnded ? null : session.Connection;
        }

        internal async Task<FlockMultiplayerSessionJoinToken> RequestJoinTokenAsync(FlockMultiplayerSession session, CancellationToken cancellationToken)
        {
            RequireRunning(session);
            JoinTokenRecord token = await AskAsync(session, () => _requests.RequestJoinTokenAsync(session.SignInNumber, session.Id, cancellationToken), cancellationToken);
            return new FlockMultiplayerSessionJoinToken(token.Token, token.ExpiresIn);
        }

        internal async Task<string> VerifyJoinTokenAsync(FlockMultiplayerSession session, string token, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(token, "Join Token");
            RequireRunning(session);
            VerifiedJoinTokenRecord verified = await AskAsync(session, () => _requests.VerifyJoinTokenAsync(session.SignInNumber, session.Id, token, cancellationToken), cancellationToken);
            return verified.PlayerId;
        }

        /// <summary>The player's address on Flock's relay for this session, opened once and shared by every caller until it fails or the session ends, which closes it; on <paramref name="server"/> ("name:port") when one is named, else on the first relay Flock lists that answers. A caller's token only stops its own wait. Fails with a <see cref="FlockRelayException"/> naming why.</summary>
        internal async Task<FlockRelayConnection> OpenRelayAsync(FlockMultiplayerSession session, string server, CancellationToken cancellationToken)
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                throw new FlockRelayException(FlockRelayFailure.NotOnThisPlatform, "A web player cannot send UDP, so it cannot use Flock's relay");
            RequireRunning(session);
            Task<FlockRelayConnection> opening = session.RelayOpening;
            if (opening != null && !HasFailed(opening) && !IsOn(session, opening, server))
            {
                // Open, or opening, on another server: it makes way once its opening has ended.
                if (!await FlockWaiting.FinishedBeforeGivenUpAsync(opening, cancellationToken))
                    throw new OperationCanceledException(cancellationToken);
                RequireRunning(session);
                if (session.RelayOpening == opening && !HasFailed(opening) && !IsOn(session, opening, server))
                {
                    session.Relay?.Close();
                    session.Relay = null;
                    session.RelayOpening = null;
                }
                opening = session.RelayOpening;
            }
            if (opening == null || HasFailed(opening))
            {
                session.Relay?.Close();
                session.Relay = null;
                opening = OpenRelayForAsync(session, server);
                LetRun(opening);
                session.RelayOpening = opening;
                session.RelayServerAsked = server;
            }
            if (!await FlockWaiting.FinishedBeforeGivenUpAsync(opening, cancellationToken))
                throw new OperationCanceledException(cancellationToken);
            return await opening;
        }

        // An opening that failed, or one whose relay has since stopped, opens again on the next call.
        private static bool HasFailed(Task<FlockRelayConnection> opening)
            => opening.IsFaulted || opening.IsCanceled || (opening.Status == TaskStatus.RanToCompletion && opening.Result.HasStopped);

        // Whether an opening serves a caller who needs the relay on server; any server does for one who named none.
        private static bool IsOn(FlockMultiplayerSession session, Task<FlockRelayConnection> opening, string server)
            => server == null || string.Equals(session.RelayServerAsked, server, StringComparison.OrdinalIgnoreCase)
               || (opening.Status == TaskStatus.RanToCompletion && string.Equals(opening.Result.Server, server, StringComparison.OrdinalIgnoreCase));

        // Logins named with the session, then each relay server Flock lists in turn, or the one named; the session's end stops it at any step.
        private async Task<FlockRelayConnection> OpenRelayForAsync(FlockMultiplayerSession session, string server)
        {
            CancellationToken stop = session.RelayStopToken;
            RelayCredentialsRecord answer;
            try
            {
                answer = await _requests.RelayLoginsAsync(session.SignInNumber, session.Id, stop);
            }
            catch (FlockException failure)
            {
                throw RelayLoginsRefused(failure);
            }
            RequireStillCurrent(session.SignInNumber);
            stop.ThrowIfCancellationRequested();
            if (answer.RelayPaused)
                throw new FlockRelayException(FlockRelayFailure.Paused, "Flock paused this studio's relay until its bill is paid");
            List<FlockRelayLogin> logins = FlockRelayLogin.InAnswer(answer);
            if (logins.Count == 0)
                throw new FlockRelayException(FlockRelayFailure.NotOffered, "Flock listed no relay for this game: it is switched off for the game in the Flock dashboard, or no relay is set up");
            if (server != null)
            {
                // Players reach the host only from the relay server it is on, since only that server's addresses share the IP it opened to.
                logins = logins.FindAll(login => string.Equals(login.ToString(), server, StringComparison.OrdinalIgnoreCase));
                if (logins.Count == 0)
                    throw new FlockRelayException(FlockRelayFailure.ServerNotListed, $"Flock does not list the relay server the host uses ({server}) for this player");
            }
            // Looked up while the relay opens: a direct link learns its own address from them, since a relay server may answer few of a device's ports (Cloudflare's, measured).
            Task<List<IPEndPoint>> stunServers = FlockDirectAddresses.LookUpStunServersAsync(answer, RelayTiming.NameLookUpWait, RelayTiming.NameLookUpForTesting, stop);
            LetRun(stunServers);
            FlockRelayException first = null;
            foreach (FlockRelayLogin login in logins)
            {
                try
                {
                    FlockRelayConnection connection = await FlockRelayConnection.OpenAsync(login, RelayTiming, _client.Logger, stop);
                    session.Relay = connection;
                    connection.StunServers = await stunServers;
                    return connection;
                }
                catch (FlockRelayException failure)
                {
                    // The first server Flock lists is its main one, so its reason is the one told when none opens.
                    first = first ?? failure;
                }
            }
            throw first;
        }

        private static FlockRelayException RelayLoginsRefused(FlockException failure)
        {
            if (failure.ErrorCode == FlockErrorCode.MultiplayerMintRateLimited)
                return new FlockRelayException(FlockRelayFailure.TooManyLogins, "Flock refused relay logins: more than 12 were asked for this player in a minute.", failure);
            if (failure.ErrorCode == FlockErrorCode.MultiplayerSeatMintLimit)
                return new FlockRelayException(FlockRelayFailure.TooManyLogins, "Flock refused relay logins: this player's seat in the session has drawn the most one seat may (10).", failure);
            if (IsOutOfTheSession(failure))
                return new FlockRelayException(FlockRelayFailure.NotInSession, "Flock refused relay logins: the player has no seat in this session.", failure);
            if (!failure.StatusCode.HasValue)
                return new FlockRelayException(FlockRelayFailure.FlockUnreachable, $"Flock did not answer the request for relay logins: {failure.Message}", failure);
            return new FlockRelayException(FlockRelayFailure.LoginsRefused, $"Flock refused relay logins: {failure.Message}", failure);
        }

        /// <summary>Ends the session of a sign-in that has ended; run once a frame.</summary>
        internal void EndIfSignInEnded()
        {
            if (_held != null && _held.SignInNumber != CurrentSignInNumber)
                End(_held, FlockMultiplayerSessionEndReason.SignedOut);
        }

        /// <summary>The game is quitting: gives the seat up without waiting, so the others see it at once rather than when the server stops hearing from it.</summary>
        internal void LeaveForQuit()
        {
            FlockMultiplayerSession session = _held;
            if (session == null || session.HasEnded)
                return;
            End(session, FlockMultiplayerSessionEndReason.Left, raise: false);
            LetRun(_requests.LeaveAsync(session.SignInNumber, session.Id, CancellationToken.None));
        }

        /// <summary>Flock is shutting down: the session ends without a word to the game, which hears Flock's own shutdown.</summary>
        internal void StopForShutdown()
        {
            _stopped = true;
            if (_held != null)
                End(_held, FlockMultiplayerSessionEndReason.SignedOut, raise: false);
            // A call waiting for the host's address ends as cancelled, as every other call does at shutdown.
            foreach (TaskCompletionSource<bool> waiting in _connectionWaiters.ToArray())
                waiting.TrySetCanceled();
        }

        // A read overtaken by a newer answer is sent again, so the caller gets the server's latest; sessionId is null for the player's current one.
        private async Task<FlockMultiplayerSession> ReadUntilNewestAsync(int signInNumber, string playerId, string sessionId, Func<Task<SessionRecord>> read, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                int number = ++_readsSent;
                SessionRecord session = await read();
                RequireStillCurrent(signInNumber);
                // Reads are numbered for the held session; one of another session the player is out of cannot change what is held.
                if (sessionId != null && session != null && (_held == null || _held.Id != session.Id) && EndingIn(session, playerId) != null)
                    return Take(session, signInNumber, playerId);
                if (number > _readsSettledThrough)
                {
                    _readsSettledThrough = number;
                    if (session != null)
                        return Take(session, signInNumber, playerId);
                    // Seated nowhere: a session held until now is over, and a reading of it says why.
                    EndIfSignInEnded();
                    if (_held != null)
                        await LearnWhyItEndedAsync(_held, cancellationToken);
                    return null;
                }
                if (attempt == MostReadsWhileTheSessionChanges)
                    throw new FlockException("The session kept changing while it was being read") { Hint = "Read it again in a moment: it changes while players join and leave and while hosting moves." };
            }
        }

        // Seated nowhere is an answer here, not a failure.
        private async Task<SessionRecord> ReadMineOrNoneAsync(int signInNumber, CancellationToken cancellationToken)
        {
            try
            {
                return await _requests.ReadCurrentAsync(signInNumber, cancellationToken);
            }
            catch (FlockException none) when (none.ErrorCode == FlockErrorCode.MultiplayerSessionNotFound)
            {
                return null;
            }
        }

        // The held session is kept seated by heartbeats at the interval its answers give; a new interval or a call a failure stopped
        // starts them again. Ending a session stops them, so a newly held one always starts afresh.
        private void KeepHeartbeating()
        {
            FlockMultiplayerSession held = _held;
            if (held == null || _stopped)
                return;
            if (_heartbeat != null && _heartbeat.IsRunning && _heartbeat.Interval == held.HeartbeatInterval)
                return;
            StopHeartbeating();
            string sessionId = held.Id;
            int signInNumber = held.SignInNumber;
            _heartbeat = _repeatingCalls.Start(
                HeartbeatCallName,
                held.HeartbeatInterval,
                cancellationToken => HeartbeatNumberedAsync(signInNumber, sessionId, cancellationToken),
                TakeReading,
                failure => HeartbeatStopped(sessionId, failure));
        }

        private void StopHeartbeating()
        {
            _heartbeat?.Stop();
            _heartbeat = null;
        }

        // Read while any call waits for the held session's address; its answers are readings like a heartbeat's.
        private void KeepReadingForConnection()
        {
            FlockMultiplayerSession held = _held;
            if (held == null || _stopped || _connectionWaiters.Count == 0)
                return;
            if (_connectionWait != null && _connectionWait.IsRunning)
                return;
            string sessionId = held.Id;
            int signInNumber = held.SignInNumber;
            _connectionWait = _repeatingCalls.Start(
                ConnectionWaitCallName,
                _connectionWaitInterval,
                cancellationToken => ReadNumberedAsync(signInNumber, sessionId, cancellationToken),
                TakeReading,
                failure => ConnectionWaitStopped(sessionId, failure));
        }

        private void StopReadingForConnection()
        {
            _connectionWait?.Stop();
            _connectionWait = null;
        }

        // A seat the server no longer has ends the session; any other refusal leaves the wait to the heartbeats' answers.
        private void ConnectionWaitStopped(string sessionId, Exception failure)
        {
            FlockMultiplayerSession held = _held;
            if (held == null || held.Id != sessionId)
                return;
            if (failure is FlockException coded && IsOutOfTheSession(coded))
            {
                LetRun(LearnWhyItEndedAsync(held, CancellationToken.None));
                return;
            }
            _client.Logger.LogWarning($"The session is no longer read while waiting for the host's address: {failure.Message}. The heartbeats still bring it, about every 20 s.");
        }

        // A heartbeat's answer is a reading like any other: numbered when sent, so an older one changes nothing.
        private async Task<SessionReading> HeartbeatNumberedAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            int number = ++_readsSent;
            SessionRecord session = await _requests.HeartbeatAsync(signInNumber, sessionId, cancellationToken);
            return new SessionReading(number, signInNumber, session);
        }

        private async Task<SessionReading> ReadNumberedAsync(int signInNumber, string sessionId, CancellationToken cancellationToken)
        {
            int number = ++_readsSent;
            SessionRecord session = await _requests.ReadAsync(signInNumber, sessionId, cancellationToken);
            return new SessionReading(number, signInNumber, session);
        }

        private void TakeReading(SessionReading reading)
        {
            if (reading.Number <= _readsSettledThrough)
                return;
            _readsSettledThrough = reading.Number;
            Take(reading.Session, reading.SignInNumber, null);
        }

        // A seat the server no longer has ends the session, read once to learn why; any other refusal leaves the seat to time out.
        private void HeartbeatStopped(string sessionId, Exception failure)
        {
            FlockMultiplayerSession held = _held;
            if (held == null || held.Id != sessionId)
                return;
            if (failure is FlockException coded && IsOutOfTheSession(coded))
            {
                LetRun(LearnWhyItEndedAsync(held, CancellationToken.None));
                return;
            }
            _client.Logger.LogWarning($"The session is no longer kept seated: {failure.Message}. The server gives the seat up when it stops hearing from it; GetMySessionAsync or GetSessionAsync starts the heartbeats again.");
        }

        // Lets work no caller waits for run to its end, reading its failure on the thread that finishes it (a web player has no
        // thread pool), so no failure is reported as unobserved. Matchmaking uses it too.
        internal static void LetRun(Task work)
            => work.ContinueWith(done => { _ = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        // A change keeps the session, or ends it (end, a host leaving alone): its answer is the session as it now is.
        private async Task ChangeAsync(FlockMultiplayerSession session, Func<Task<SessionRecord>> send, CancellationToken cancellationToken)
        {
            RequireRunning(session);
            SessionRecord changed = await AskAsync(session, send, cancellationToken);
            TakeAnswerOfChange(changed, session.SignInNumber, null);
        }

        // A session the server no longer has the player in is over here too, whichever call found out; it is read once to learn why.
        private async Task<T> AskAsync<T>(FlockMultiplayerSession session, Func<Task<T>> send, CancellationToken cancellationToken)
        {
            try
            {
                return await send();
            }
            catch (FlockException gone) when (IsOutOfTheSession(gone))
            {
                await LearnWhyItEndedAsync(session, cancellationToken);
                throw;
            }
        }

        // The reading says why; one already overtaken by a newer answer leaves it to that answer.
        private async Task LearnWhyItEndedAsync(FlockMultiplayerSession session, CancellationToken cancellationToken)
        {
            try
            {
                int number = ++_readsSent;
                SessionRecord last = await _requests.ReadAsync(session.SignInNumber, session.Id, cancellationToken);
                if (number > _readsSettledThrough)
                {
                    _readsSettledThrough = number;
                    Take(last, session.SignInNumber, null);
                }
                return;
            }
            catch (FlockException)
            {
                // Not even readable any more: the seat was given up without a word.
            }
            catch (OperationCanceledException)
            {
                // The caller gave up; the session is over all the same.
            }
            End(session, EndingOf(session, FlockMultiplayerSessionEndReason.Dropped));
        }

        // The answer to a change is the newest reading: reads this sign-in sent before the change finished change nothing.
        private FlockMultiplayerSession TakeAnswerOfChange(SessionRecord session, int signInNumber, string playerId)
        {
            if (signInNumber == CurrentSignInNumber)
                _readsSettledThrough = _readsSent;
            return Take(session, signInNumber, playerId);
        }

        /// <summary>The one place a reading changes the session; holds it when a call asked for it (<paramref name="playerId"/> given) and the player is seated.</summary>
        private FlockMultiplayerSession Take(SessionRecord session, int signInNumber, string playerId)
        {
            // A session from a sign-in that ended is over first; that sign-in's late readings are older than any newer one.
            EndIfSignInEnded();
            if (signInNumber != CurrentSignInNumber)
                return null;

            if (_held != null && _held.Id == session.Id)
            {
                FlockMultiplayerSession held = _held;
                // The server's epochs only grow, so a reading with a lower one was taken before the one held and changes nothing.
                if (session.HostEpoch < held.HostEpoch || session.ConnectionEpoch < held.ConnectionEpoch)
                    return held;
                string ending = EndingIn(session, held.PlayerId);
                if (ending == null)
                {
                    // The heartbeat and the relay follow the reading before anyone hears of it, so a handler finds the SDK settled too.
                    FlockMultiplayerSession.ReadingChanges changes = held.TakeNewerReading(session);
                    KeepHeartbeating();
                    OpenTheRelayToSeatedPlayers(held);
                    held.RaiseChanges(changes);
                }
                else
                {
                    held.TakeFinalReading(session);
                    End(held, EndingOf(held, ending));
                }
                return held;
            }
            if (playerId == null)
                return null;

            FlockMultiplayerSession taken = new FlockMultiplayerSession(this, signInNumber, playerId, session);
            string ended = EndingIn(session, playerId);
            if (ended != null)
            {
                // A session the player is not seated in any more: handed back as it ended, never held.
                taken.End(ended, raise: false);
                return taken;
            }
            // Hosting or joining gives up any other seat, so the session held until now is over at once.
            if (_held != null)
                End(_held, FlockMultiplayerSessionEndReason.MovedToAnotherSession);
            _held = taken;
            KeepHeartbeating();
            return taken;
        }

        // Why a reading says the session is no longer the player's, or null while they hold a seat in it. A session that is over
        // gives its own reason first: closing one marks every seat left at the same moment, so a seat cannot say who left first.
        private static string EndingIn(SessionRecord session, string playerId)
        {
            if (session.Status == SessionWireValues.SessionEnded)
                return string.IsNullOrEmpty(session.EndedReason) ? FlockMultiplayerSessionEndReason.Left : session.EndedReason;
            if (session.Participants != null)
            {
                foreach (SessionParticipantRecord participant in session.Participants)
                {
                    if (!string.Equals(participant.PlayerId, playerId, StringComparison.Ordinal))
                        continue;
                    if (participant.Status == SessionWireValues.SeatJoined)
                        return null;
                    return participant.Status == SessionWireValues.SeatDropped ? FlockMultiplayerSessionEndReason.Dropped : FlockMultiplayerSessionEndReason.Left;
                }
            }
            return FlockMultiplayerSessionEndReason.Left;
        }

        // Settles everything before raising Ended, so a handler that calls back in finds the session over.
        private void End(FlockMultiplayerSession session, string reason, bool raise = true)
        {
            if (session.EndReasonSet != null)
                return;
            // A session whose sign-in ended reads as signed out from then on, whatever answer lands after.
            if (session.SignInNumber != CurrentSignInNumber)
                reason = FlockMultiplayerSessionEndReason.SignedOut;
            if (_held == session)
            {
                _held = null;
                StopHeartbeating();
                StopReadingForRelayedPlayers();
            }
            // A call waiting for this session's address is woken by Ended and stops the reads itself, inline on the main thread.
            session.End(reason, raise);
        }

        private static bool IsOutOfTheSession(FlockException failure)
            => failure.ErrorCode == FlockErrorCode.MultiplayerSessionNotFound || failure.ErrorCode == FlockErrorCode.MultiplayerNotAParticipant;

        private int RequireSignedIn(out string playerId)
        {
            if (!_client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            playerId = _client.CurrentPlayerId;
            return CurrentSignInNumber;
        }

        // The sign-in a call was asked under ended, or Flock shut down, while its answer was on its way.
        private void RequireStillCurrent(int signInNumber)
        {
            if (_stopped || signInNumber != CurrentSignInNumber)
                throw new OperationCanceledException("The player signed out or changed, or Flock shut down, while the session was on its way");
        }

        private static void RequireRunning(FlockMultiplayerSession session)
        {
            if (session.HasEnded)
                throw new FlockValidationException($"This session has ended ({session.EndReason})") { Hint = FlockErrorHints.FindAnotherSession };
        }

        private sealed class SessionReading
        {
            internal SessionReading(int number, int signInNumber, SessionRecord session)
            {
                Number = number;
                SignInNumber = signInNumber;
                Session = session;
            }

            internal int Number { get; }
            internal int SignInNumber { get; }
            internal SessionRecord Session { get; }
        }
    }
}
