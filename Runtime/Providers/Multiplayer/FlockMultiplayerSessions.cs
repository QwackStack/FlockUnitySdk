using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>The one owner of the player's session: at most one held per sign-in, changed only by the newest answer; runs on the main thread.</summary>
    internal sealed class FlockMultiplayerSessions
    {
        // A read overtaken this many times in a row means the session is changing faster than it can be read.
        private const int MostReadsWhileTheSessionChanges = 5;

        private readonly FlockClient _client;
        private readonly FlockMultiplayerSessionRequests _requests;
        private FlockMultiplayerSession _held;
        private int _readsSent;
        // Reads numbered at or below this change nothing when they land.
        private int _readsSettledThrough;
        // A leave of the held session on its way: an answer that finds the player out is its doing.
        private bool _leavingByThisGame;
        // Flock shut down or was reset: an answer still on its way holds nothing.
        private bool _stopped;

        internal FlockMultiplayerSessions(FlockClient client)
        {
            _client = client;
            _requests = new FlockMultiplayerSessionRequests(client);
        }

        internal int CurrentSignInNumber => _client.SignInNumber;
        internal FlockMultiplayerSession Held => _held;

        internal async Task<FlockMultiplayerSession> HostAsync(int? maxPlayers, IReadOnlyDictionary<string, object> data, CancellationToken cancellationToken)
        {
            int signInNumber = RequireSignedIn(out string playerId);
            SessionRecord hosted = await _requests.HostAsync(signInNumber, maxPlayers, data, cancellationToken);
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
            for (int attempt = 1; ; attempt++)
            {
                int number = ++_readsSent;
                SessionRecord session = await _requests.ReadAsync(signInNumber, sessionId, cancellationToken);
                RequireStillCurrent(signInNumber);
                if (number > _readsSettledThrough)
                {
                    _readsSettledThrough = number;
                    return Take(session, signInNumber, playerId);
                }
                if (attempt == MostReadsWhileTheSessionChanges)
                    throw new FlockException("The session kept changing while it was being read. Try again.");
            }
        }

        internal async Task LeaveAsync(FlockMultiplayerSession session, CancellationToken cancellationToken)
        {
            RequireRunning(session);
            _leavingByThisGame = true;
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
                _leavingByThisGame = false;
            }
            End(session, FlockMultiplayerSessionEndReason.Left);
        }

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
            Task leaving = _requests.LeaveAsync(session.SignInNumber, session.Id, CancellationToken.None);
            // Observed on the thread that finishes it: a web player has no thread pool to run it on.
            leaving.ContinueWith(sent => { _ = sent.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>Flock is shutting down: the session ends without a word to the game, which hears Flock's own shutdown.</summary>
        internal void StopForShutdown()
        {
            _stopped = true;
            if (_held != null)
                End(_held, FlockMultiplayerSessionEndReason.SignedOut, raise: false);
        }

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
            End(session, FlockMultiplayerSessionEndReason.Dropped);
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
                    held.Update(session);
                }
                else
                {
                    held.TakeFinalReading(session);
                    End(held, _leavingByThisGame ? FlockMultiplayerSessionEndReason.Left : ending);
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
                _held = null;
            session.End(reason, raise);
        }

        private static bool IsOutOfTheSession(FlockException failure)
            => failure.ErrorCode == FlockErrorCode.MultiplayerSessionNotFound || failure.ErrorCode == FlockErrorCode.MultiplayerNotAParticipant;

        private int RequireSignedIn(out string playerId)
        {
            if (!_client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in");
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
                throw new FlockValidationException($"This session has ended ({session.EndReason}). Host or join another.");
        }
    }
}
