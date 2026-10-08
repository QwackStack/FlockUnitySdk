using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>The one owner of the player's search for a match: at most one per sign-in, its ticket checked until it leaves the queue; runs on the main thread.</summary>
    internal sealed class FlockMatchmaking
    {
        internal const string CheckCallName = "Matchmaking check";
        // The server asks for a check every 2 to 5 s; the repeating calls move each wait a quarter either way.
        internal static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(3);
        private const string PartyChangedReason = "party_changed";

        private readonly FlockClient _client;
        private readonly FlockMatchmakingRequests _requests;
        private readonly FlockRepeatingCalls _repeatingCalls;
        private readonly FlockMultiplayerSessions _sessions;
        private readonly Func<string, CancellationToken, Task<string>> _findQueueId;
        private TimeSpan _checkInterval = DefaultCheckInterval;
        private Search _search;
        // A search's first request is on its way, so a second one is refused until it lands.
        private bool _starting;
        private int _readsSent;
        // Readings numbered at or below this change nothing when they land.
        private int _readsAppliedThrough;
        // The party whose searches are watched, its latest ticket seen, and whether that ticket needs nothing more: a party's
        // search is reported once, and one from before the party was first read here never is.
        private string _partyWatched;
        private int _partyWatchedSignInNumber;
        private string _partyTicketSeen;
        private bool _partyTicketSettled;
        // Flock shut down or was reset: an answer still on its way starts nothing.
        private bool _stopped;

        internal FlockMatchmaking(FlockClient client, FlockRepeatingCalls repeatingCalls, FlockMultiplayerSessions sessions, Func<string, CancellationToken, Task<string>> findQueueId)
        {
            _client = client;
            _requests = new FlockMatchmakingRequests(client);
            _repeatingCalls = repeatingCalls;
            _sessions = sessions;
            _findQueueId = findQueueId;
        }

        internal int CurrentSignInNumber => _client.SignInNumber;

        // A game always checks at the default; tests shorten it.
        internal void SetCheckIntervalForTesting(TimeSpan interval) => _checkInterval = interval;

        /// <summary>True while a search of this party is known here and has not ended.</summary>
        internal bool IsSearching(FlockParty party) => _search != null && _search.Party == party && _search.TicketId != null && !_search.Ending;

        internal async Task<FlockMatchmakingResult> FindAloneAsync(string queueName, FlockMatchmakingOptions options, CancellationToken cancellationToken)
        {
            int signInNumber = RequireSignedIn();
            RequireNoSearch();
            Search search = await StartAsync(signInNumber, _client.CurrentPlayerId, null, queueName, options, cancellationToken);
            return await WaitAsync(search, cancellationToken);
        }

        // The leader starts the party's search; a member's game waits for the leader's. A search of the party already running is waited for.
        internal async Task<FlockMatchmakingResult> FindForPartyAsync(FlockParty party, string queueName, FlockMatchmakingOptions options, CancellationToken cancellationToken)
        {
            if (party.HasEnded)
                throw new FlockValidationException($"This party has ended ({party.EndReason}). Create or join another, or get the player's current one with GetMyPartyAsync.");
            int signInNumber = RequireSignedIn();
            if (_search != null && _search.Party == party)
                return await WaitAsync(_search, cancellationToken);
            RequireNoSearch();
            Search search = party.IsLeader
                ? await StartAsync(signInNumber, _client.CurrentPlayerId, party, queueName, options, cancellationToken)
                : WaitForLeader(signInNumber, party);
            return await WaitAsync(search, cancellationToken);
        }

        /// <summary>The player's own ticket, read with each party refresh so a member's game learns of the leader's search; null when it could not be read this time.</summary>
        internal async Task<TicketReading> ReadForPartyAsync(int signInNumber, CancellationToken cancellationToken)
        {
            try
            {
                return await ReadNumberedAsync(signInNumber, null, cancellationToken);
            }
            catch (FlockException failure)
            {
                _client.Logger.LogDebug($"The party refresh could not read the player's search this time: {failure.Message}");
                return null;
            }
        }

        /// <summary>Takes the ticket read with a party refresh, after the party's own reading.</summary>
        internal void TakePartyReading(FlockParty party, TicketReading reading) => TakeCurrent(party, reading);

        /// <summary>Marks where the party's searches stand the moment the game holds the party, so a search made after it is never taken for an old one.</summary>
        internal async Task StartWatchingAsync(FlockParty party, CancellationToken cancellationToken)
        {
            if (party == null || party.HasEnded || (_partyWatched == party.Id && _partyWatchedSignInNumber == party.SignInNumber))
                return;
            TicketReading reading;
            try
            {
                reading = await ReadForPartyAsync(party.SignInNumber, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The game gave up or the sign-in ended: the party is the player's already, and its next refresh marks the start.
                return;
            }
            if (_partyWatched == party.Id && _partyWatchedSignInNumber == party.SignInNumber)
            {
                TakeCurrent(party, reading);
                return;
            }
            // Only marks the start: a search already running is followed by the next reading, once the game holds the party to listen.
            if (!_stopped && !party.HasEnded && TakeIfNewest(reading))
                BeginWatch(party, reading.Ticket);
        }

        /// <summary>Ends the search of a sign-in that has ended, as cancelled; run once a frame. Nothing more is sent for it.</summary>
        internal void EndIfSignInEnded()
        {
            Search search = _search;
            if (search == null || search.SignInNumber == CurrentSignInNumber)
                return;
            _search = null;
            StopChecking(search);
            search.Finished.TrySetCanceled();
        }

        /// <summary>The game is quitting: the search is cancelled without waiting, so it seats nobody who has gone.</summary>
        internal void CancelForQuit()
        {
            Search search = _search;
            if (search == null)
                return;
            _search = null;
            StopChecking(search);
            if (search.TicketId != null && !search.Ending)
                FlockMultiplayerSessions.LetRun(_requests.CancelAsync(search.SignInNumber, search.TicketId, CancellationToken.None));
        }

        /// <summary>Flock is shutting down: the search ends without a word to the game, which hears Flock's own shutdown.</summary>
        internal void StopForShutdown()
        {
            _stopped = true;
            Search search = _search;
            _search = null;
            if (search == null)
                return;
            StopChecking(search);
            search.Finished.TrySetCanceled();
        }

        // Makes the ticket and starts checking it; a caller that gives up while the ticket is on its way leaves nothing behind.
        private async Task<Search> StartAsync(int signInNumber, string playerId, FlockParty party, string queueName, FlockMatchmakingOptions options, CancellationToken cancellationToken)
        {
            _starting = true;
            try
            {
                string queueId = await _findQueueId(queueName, cancellationToken);
                RequireStillCurrent(signInNumber);
                Task<TicketRecord> making = MakeTicketAsync(signInNumber, playerId, party?.Id, queueId, options?.Attributes, cancellationToken);
                if (!await FinishedBeforeGivenUpAsync(making, cancellationToken))
                {
                    FlockMultiplayerSessions.LetRun(CancelOnceMadeAsync(signInNumber, making));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                TicketRecord ticket = await making;
                RequireStillCurrent(signInNumber);
                return Begin(signInNumber, party, ticket);
            }
            finally
            {
                _starting = false;
            }
        }

        // The player's own earlier search (an earlier launch's, or this party's when its leader searches again) is cancelled and
        // this one made once more; a party's search is never cancelled for a search alone, since one member's cancel stops it.
        private async Task<TicketRecord> MakeTicketAsync(int signInNumber, string playerId, string partyId, string queueId, IReadOnlyDictionary<string, object> attributes, CancellationToken givenUp)
        {
            try
            {
                return OwnTicket(await _requests.CreateAsync(signInNumber, queueId, partyId, attributes, CancellationToken.None), playerId);
            }
            catch (FlockException queued) when (queued.ErrorCode == FlockErrorCode.MatchmakingAlreadyQueued)
            {
                // A search the game gave up never replaces one: the search queued may be the game's next.
                givenUp.ThrowIfCancellationRequested();
                TicketRecord earlier = await ReadCurrentOrNoneAsync(signInNumber, CancellationToken.None);
                bool ours = earlier != null && earlier.Status == TicketRecord.Queued && (earlier.PartyId == null || earlier.PartyId == partyId);
                if (!ours)
                    throw;
                givenUp.ThrowIfCancellationRequested();
                await CancelOrLeaveAsync(signInNumber, earlier.Id);
                _client.Logger.LogDebug("The player's search left from before was cancelled, and this one takes its place.");
                return OwnTicket(await _requests.CreateAsync(signInNumber, queueId, partyId, attributes, CancellationToken.None), playerId);
            }
        }

        // A party's call answers every member's ticket; the player's own is the one to check.
        private static TicketRecord OwnTicket(List<TicketRecord> tickets, string playerId)
        {
            TicketRecord own = tickets.Find(ticket => string.Equals(ticket.PlayerId, playerId, StringComparison.Ordinal));
            if (own == null)
                throw new FlockNetworkException("Invalid response from server");
            return own;
        }

        private async Task CancelOnceMadeAsync(int signInNumber, Task<TicketRecord> making)
        {
            TicketRecord ticket = await making;
            await CancelOrLeaveAsync(signInNumber, ticket.Id);
        }

        // Cancels a search; one matched meanwhile gives up the seat the match gave, so a search the game gave up leaves nothing behind.
        private async Task CancelOrLeaveAsync(int signInNumber, string ticketId)
        {
            TicketRecord ended;
            try
            {
                ended = await _requests.CancelAsync(signInNumber, ticketId, CancellationToken.None);
            }
            catch (FlockException notCancelable) when (notCancelable.ErrorCode == FlockErrorCode.MatchmakingTicketNotCancelable)
            {
                ended = await _requests.ReadAsync(signInNumber, ticketId, CancellationToken.None);
            }
            string sessionId = ended.Status == TicketRecord.Matched ? ended.Match?.ConnectionInfo?.SessionId : null;
            if (string.IsNullOrEmpty(sessionId))
                return;
            FlockMultiplayerSession session = await _sessions.GetAsync(sessionId, CancellationToken.None);
            if (!session.HasEnded)
                await session.LeaveAsync(CancellationToken.None);
        }

        private Search Begin(int signInNumber, FlockParty party, TicketRecord ticket)
        {
            // A ticket just made is newer than every reading sent before it.
            _readsAppliedThrough = _readsSent;
            Search search = new Search(signInNumber, party) { TicketId = ticket.Id };
            _search = search;
            Check(search);
            if (party != null)
            {
                _partyWatched = party.Id;
                _partyWatchedSignInNumber = signInNumber;
                _partyTicketSeen = ticket.Id;
                _partyTicketSettled = false;
                if (!party.HasEnded)
                    party.RaiseSearchStarted();
            }
            return search;
        }

        // A member's game reads the player's own ticket until the party's search shows up.
        private Search WaitForLeader(int signInNumber, FlockParty party)
        {
            Search search = new Search(signInNumber, party);
            _search = search;
            Check(search);
            return search;
        }

        // A caller that gives up cancels the search on the server, then hears of it as a cancellation.
        private async Task<FlockMatchmakingResult> WaitAsync(Search search, CancellationToken cancellationToken)
        {
            if (search.HasCaller)
                throw new FlockValidationException("A call already waits for this search. Wait for its result, or for the party's SearchEnded.");
            search.HasCaller = true;
            try
            {
                if (!await FinishedBeforeGivenUpAsync(search.Finished.Task, cancellationToken))
                {
                    await CancelSearchAsync(search);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return await search.Finished.Task;
            }
            finally
            {
                search.HasCaller = false;
            }
        }

        // The game gave up: the search is cancelled on the server, and a match that landed meanwhile gives its seat up.
        private async Task CancelSearchAsync(Search search)
        {
            if (search.Ending)
            {
                FlockMatchmakingResult ended = await ResultOrNoneAsync(search.Finished.Task);
                if (ended?.Session != null && !ended.Session.HasEnded)
                    await LeaveQuietlyAsync(ended.Session);
                return;
            }
            search.CancelledByThisGame = true;
            StopChecking(search);
            if (_search == search)
                _search = null;
            if (search.TicketId != null)
            {
                try
                {
                    await CancelOrLeaveAsync(search.SignInNumber, search.TicketId);
                }
                catch (OperationCanceledException)
                {
                    // The sign-in ended: nothing more is sent as it.
                }
                catch (FlockException failure)
                {
                    _client.Logger.LogWarning($"The search could not be cancelled on the server: {failure.Message}. It ends on its own within 5 minutes.");
                }
                if (search.Party != null)
                {
                    SettlePartyTicket(search);
                    if (!search.Party.HasEnded)
                        search.Party.RaiseSearchEnded(new FlockMatchmakingResult(FlockMatchmakingOutcome.Cancelled, null, null));
                }
            }
            search.Finished.TrySetCanceled();
        }

        private void Check(Search search)
        {
            search.Check = _repeatingCalls.Start(
                CheckCallName,
                _checkInterval,
                cancellationToken => ReadNumberedAsync(search.SignInNumber, search.TicketId, cancellationToken),
                reading => TakeCheck(search, reading),
                failure => CheckStopped(search, failure));
        }

        private static void StopChecking(Search search)
        {
            search.Check?.Stop();
            search.Check = null;
        }

        // A check the server refused for good ends the search with that failure; the party's next reading follows its search again.
        private void CheckStopped(Search search, Exception failure)
        {
            if (_search != search)
                return;
            _search = null;
            if (!search.HasCaller)
                _client.Logger.LogWarning($"The party's search is no longer followed: {failure.Message}. The party's next refresh, or its FindMatchAsync, follows it again.");
            search.Finished.TrySetException(failure);
            // Nobody may wait for it, so its failure is read here rather than reported as unobserved.
            _ = search.Finished.Task.Exception;
        }

        // Numbered as it is sent: the number, not the order answers arrive in, says which reading is newer.
        private async Task<TicketReading> ReadNumberedAsync(int signInNumber, string ticketId, CancellationToken cancellationToken)
        {
            int number = ++_readsSent;
            TicketRecord ticket = ticketId != null
                ? await _requests.ReadAsync(signInNumber, ticketId, cancellationToken)
                : await ReadCurrentOrNoneAsync(signInNumber, cancellationToken);
            return new TicketReading(number, ticket);
        }

        // A player who never searched has no ticket, which is an answer here, not a failure.
        private async Task<TicketRecord> ReadCurrentOrNoneAsync(int signInNumber, CancellationToken cancellationToken)
        {
            try
            {
                return await _requests.ReadCurrentAsync(signInNumber, cancellationToken);
            }
            catch (FlockException none) when (none.ErrorCode == FlockErrorCode.MatchmakingTicketNotFound)
            {
                return null;
            }
        }

        // Takes a reading newer than every one taken so far, and says whether it did.
        private bool TakeIfNewest(TicketReading reading)
        {
            if (reading == null || reading.Number <= _readsAppliedThrough)
                return false;
            _readsAppliedThrough = reading.Number;
            return true;
        }

        private void TakeCheck(Search search, TicketReading reading)
        {
            if (search.TicketId == null)
            {
                TakeCurrent(search.Party, reading);
                return;
            }
            if (TakeIfNewest(reading))
                TakeTicket(search, reading.Ticket);
        }

        // The one place a search's ticket moves it on: a ticket that left the queue ends it.
        private void TakeTicket(Search search, TicketRecord ticket)
        {
            if (_search != search || search.Ending || search.CancelledByThisGame || ticket == null || ticket.Id != search.TicketId)
                return;
            if (ticket.Status == TicketRecord.Queued)
                return;
            FlockMultiplayerSessions.LetRun(FinishAsync(search, ticket));
        }

        // Marks where the party's searches stand; true when one is already running, which is left unseen so it is followed and told started.
        private bool BeginWatch(FlockParty party, TicketRecord ticket)
        {
            _partyWatched = party.Id;
            _partyWatchedSignInNumber = party.SignInNumber;
            bool running = ticket != null && ticket.Status == TicketRecord.Queued && ticket.PartyId == party.Id;
            _partyTicketSeen = running ? null : ticket?.Id;
            _partyTicketSettled = !running;
            return running;
        }

        // The player's own ticket read without its id, by a party refresh or a member's game waiting for the leader.
        private void TakeCurrent(FlockParty party, TicketReading reading)
        {
            if (_stopped || party == null || party.HasEnded || !TakeIfNewest(reading))
                return;
            TicketRecord ticket = reading.Ticket;
            // The party's first reading here only marks where its searches stood; a search already running is followed.
            if ((_partyWatched != party.Id || _partyWatchedSignInNumber != party.SignInNumber) && !BeginWatch(party, ticket))
                return;
            if (ticket == null || ticket.PartyId != party.Id)
                return;
            if (_search != null && _search.TicketId == ticket.Id)
            {
                TakeTicket(_search, ticket);
                return;
            }
            if (ticket.Id == _partyTicketSeen && _partyTicketSettled)
                return;
            // This game's own search on its way is begun by its own answer, not followed from a reading.
            if (_starting)
                return;
            // A search alone runs here: the server queues a player once, so the party's is not this player's yet.
            if (_search != null && (_search.Party != party || _search.TicketId != null))
                return;

            bool isNew = ticket.Id != _partyTicketSeen;
            _partyTicketSeen = ticket.Id;
            _partyTicketSettled = false;
            Search search = _search ?? new Search(party.SignInNumber, party);
            _search = search;
            search.TicketId = ticket.Id;
            if (isNew)
                party.RaiseSearchStarted();
            if (_search != search)
                return;
            if (ticket.Status != TicketRecord.Queued)
                TakeTicket(search, ticket);
            else if (search.Check == null)
                Check(search);
        }

        // A ticket that left the queue: a match hands over the session it seats the player in, read before anyone hears of it.
        private async Task FinishAsync(Search search, TicketRecord ticket)
        {
            search.Ending = true;
            StopChecking(search);
            FlockMatchmakingResult result = ticket.Status == TicketRecord.Matched
                ? new FlockMatchmakingResult(FlockMatchmakingOutcome.Matched, await MatchedSessionOrNoneAsync(ticket), ticket.Match?.PlayerIds)
                : new FlockMatchmakingResult(OutcomeOf(ticket), null, null);
            if (_search != search)
                return;
            _search = null;
            if (search.Party != null)
                SettlePartyTicket(search);
            // The party is told first: a game's await can resume inside the result, and a search it starts there is told after this one's end.
            if (search.Party != null && !search.Party.HasEnded)
                search.Party.RaiseSearchEnded(result);
            search.Finished.TrySetResult(result);
        }

        private async Task<FlockMultiplayerSession> MatchedSessionOrNoneAsync(TicketRecord ticket)
        {
            string sessionId = ticket.Match?.ConnectionInfo?.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                _client.Logger.LogWarning("The match made no session: the game's multiplayer settings in the Flock dashboard create none on a match. The result names the matched players.");
                return null;
            }
            try
            {
                return await _sessions.GetAsync(sessionId, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // The sign-in ended while the session was on its way: the search ends as cancelled on the next frame.
                return null;
            }
            catch (FlockException failure)
            {
                _client.Logger.LogWarning($"The match's session could not be read: {failure.Message}. GetMySessionAsync finds it.");
                return null;
            }
        }

        private void SettlePartyTicket(Search search)
        {
            if (search.Party.Id == _partyWatched && search.SignInNumber == _partyWatchedSignInNumber && search.TicketId == _partyTicketSeen)
                _partyTicketSettled = true;
        }

        private static string OutcomeOf(TicketRecord ticket)
        {
            if (ticket.Status == TicketRecord.Expired)
                return FlockMatchmakingOutcome.Expired;
            if (ticket.Status == TicketRecord.Cancelled)
                return ticket.CancelReason == PartyChangedReason ? FlockMatchmakingOutcome.PartyChanged : FlockMatchmakingOutcome.Cancelled;
            // A status the server added: the search ended as the server names it.
            return ticket.Status;
        }

        // No RunContinuationsAsynchronously: Task.WhenAny over such a source never finished in a WebGL player (measured).
        private static async Task<bool> FinishedBeforeGivenUpAsync(Task work, CancellationToken cancellationToken)
        {
            if (work.IsCompleted || !cancellationToken.CanBeCanceled)
                return true;
            TaskCompletionSource<bool> givenUp = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => givenUp.TrySetResult(true)))
                await Task.WhenAny(work, givenUp.Task);
            return work.IsCompleted;
        }

        private static async Task<FlockMatchmakingResult> ResultOrNoneAsync(Task<FlockMatchmakingResult> finished)
        {
            try
            {
                return await finished;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task LeaveQuietlyAsync(FlockMultiplayerSession session)
        {
            try
            {
                await session.LeaveAsync(CancellationToken.None);
            }
            catch (Exception failure) when (failure is FlockException || failure is OperationCanceledException)
            {
                _client.Logger.LogWarning($"The session matched while the search was being cancelled could not be left: {failure.Message}. The server lets the seat go when it stops hearing from it.");
            }
        }

        private int RequireSignedIn()
        {
            if (!_client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in");
            return CurrentSignInNumber;
        }

        private void RequireNoSearch()
        {
            if (_starting || _search != null)
                throw new FlockValidationException("A search for a match is already running here: this player's own, or the party's that this game follows. Wait for its result (the party's through its FindMatchAsync or SearchEnded), or cancel it through the token given to FindMatchAsync.");
        }

        // The sign-in a call was asked under ended, or Flock shut down, while its answer was on its way.
        private void RequireStillCurrent(int signInNumber)
        {
            if (_stopped || signInNumber != CurrentSignInNumber)
                throw new OperationCanceledException("The player signed out or changed, or Flock shut down, while the search was starting");
        }

        /// <summary>One reading of the player's ticket, numbered when it was sent; a null ticket means the player never searched.</summary>
        internal sealed class TicketReading
        {
            internal TicketReading(int number, TicketRecord ticket)
            {
                Number = number;
                Ticket = ticket;
            }

            internal int Number { get; }
            internal TicketRecord Ticket { get; }
        }

        private sealed class Search
        {
            internal Search(int signInNumber, FlockParty party)
            {
                SignInNumber = signInNumber;
                Party = party;
            }

            internal int SignInNumber { get; }
            // The party whose search this is, or null for a search alone.
            internal FlockParty Party { get; }
            // Null while a member's game waits for the leader to start.
            internal string TicketId;
            internal FlockRepeatingCall Check;
            internal readonly TaskCompletionSource<FlockMatchmakingResult> Finished = new TaskCompletionSource<FlockMatchmakingResult>();
            internal bool HasCaller;
            // The ticket left the queue and the result is being worked out; no other reading counts.
            internal bool Ending;
            // The game gave up on it, and its cancel settles the search.
            internal bool CancelledByThisGame;
        }
    }
}
