using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>The one owner of the player's party: at most one held per sign-in, changed only by the newest reading; runs on the main thread.</summary>
    internal sealed class FlockParties
    {
        internal const string RefreshCallName = "Party refresh";
        // A read overtaken this many times in a row means the party is changing faster than it can be read.
        private const int MostReadsWhileThePartyChanges = 5;

        private readonly FlockClient _client;
        private readonly FlockPartyRequests _requests;
        private readonly FlockRepeatingCalls _repeatingCalls;
        private readonly TimeSpan _refreshInterval;
        private FlockParty _held;
        private FlockRepeatingCall _refresh;
        private int _readsSent;
        // Reads numbered at or below this change nothing when they land.
        private int _readsSettledThrough;
        // A leave or disband of the held party on its way: a reading that finds the player out is its doing, not a removal.
        private FlockPartyEndReason _endingByThisGame;
        // Flock shut down or was reset: an answer still on its way holds nothing.
        private bool _stopped;

        internal FlockParties(FlockClient client, FlockRepeatingCalls repeatingCalls, TimeSpan refreshInterval)
        {
            _client = client;
            _requests = new FlockPartyRequests(client);
            _repeatingCalls = repeatingCalls;
            _refreshInterval = refreshInterval;
        }

        internal int CurrentSignInNumber => _client.SignInNumber;
        internal FlockParty Held => _held;
        internal TimeSpan RefreshInterval => _refreshInterval;

        internal async Task<FlockParty> CreateAsync(int? maxSize, IReadOnlyDictionary<string, object> settings, CancellationToken cancellationToken)
        {
            int signInNumber = RequireSignedIn(out string playerId);
            PartyRecord created = await _requests.CreateAsync(signInNumber, maxSize, settings, cancellationToken);
            RequireStillCurrent(signInNumber);
            // A new party is newer than every reading on its way; its only member is the player who made it, its leader.
            _readsSettledThrough = _readsSent;
            return Hold(signInNumber, playerId, created, new[] { created.LeaderPlayerId });
        }

        // The join answer carries no members, so the party is read before it is handed over.
        internal async Task<FlockParty> JoinAsync(string inviteCode, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(inviteCode, "Invite Code");
            int signInNumber = RequireSignedIn(out string playerId);
            PartyRecord joined = await _requests.JoinAsync(signInNumber, inviteCode, cancellationToken);
            _readsSettledThrough = _readsSent;
            PartyRead read = await ReadUntilNewestAsync(signInNumber, joined.Id, cancellationToken);
            RequireStillCurrent(signInNumber);
            return Apply(read, playerId);
        }

        /// <summary>The player's party, read now, or null when the player is in none.</summary>
        internal async Task<FlockParty> GetMineAsync(CancellationToken cancellationToken)
        {
            int signInNumber = RequireSignedIn(out string playerId);
            PartyRead read = await ReadUntilNewestAsync(signInNumber, null, cancellationToken);
            RequireStillCurrent(signInNumber);
            return Apply(read, playerId);
        }

        internal async Task LeaveAsync(FlockParty party, CancellationToken cancellationToken)
        {
            RequireRunning(party);
            _endingByThisGame = FlockPartyEndReason.Left;
            try
            {
                await _requests.LeaveAsync(party.SignInNumber, party.Id, cancellationToken);
            }
            catch (FlockException alreadyOut) when (alreadyOut.ErrorCode == FlockErrorCode.PartyNotAMember || alreadyOut.ErrorCode == FlockErrorCode.PartyNotFound)
            {
                // Out of the party already, which is what leaving asks for; a retried leave whose first answer was lost lands here too.
            }
            finally
            {
                _endingByThisGame = FlockPartyEndReason.None;
            }
            _readsSettledThrough = _readsSent;
            End(party, FlockPartyEndReason.Left);
        }

        internal async Task KickAsync(FlockParty party, string playerId, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(playerId, "Player ID");
            await ChangeAsync(party, () => _requests.KickAsync(party.SignInNumber, party.Id, playerId, cancellationToken), cancellationToken);
        }

        internal async Task MakeLeaderAsync(FlockParty party, string playerId, CancellationToken cancellationToken)
        {
            _requests.RequireGiven(playerId, "Player ID");
            await ChangeAsync(party, () => _requests.TransferAsync(party.SignInNumber, party.Id, playerId, cancellationToken), cancellationToken);
        }

        internal async Task UpdateAsync(FlockParty party, int? maxSize, IReadOnlyDictionary<string, object> settings, CancellationToken cancellationToken)
        {
            if (maxSize == null && settings == null)
                throw new FlockValidationException("Nothing to update: give the party a size, settings or both");
            await ChangeAsync(party, () => _requests.UpdateAsync(party.SignInNumber, party.Id, maxSize, settings, cancellationToken), cancellationToken);
        }

        internal async Task DisbandAsync(FlockParty party, CancellationToken cancellationToken)
        {
            RequireRunning(party);
            _endingByThisGame = FlockPartyEndReason.Disbanded;
            try
            {
                await SendChangeAsync(party, () => _requests.DisbandAsync(party.SignInNumber, party.Id, cancellationToken));
            }
            finally
            {
                _endingByThisGame = FlockPartyEndReason.None;
            }
            End(party, FlockPartyEndReason.Disbanded);
        }

        /// <summary>Ends the party of a sign-in that has ended; run once a frame.</summary>
        internal void EndIfSignInEnded()
        {
            if (_held != null && _held.SignInNumber != CurrentSignInNumber)
                End(_held, FlockPartyEndReason.SignedOut);
        }

        /// <summary>Flock is shutting down: the party ends without a word to the game, which hears Flock's own shutdown.</summary>
        internal void StopForShutdown()
        {
            _stopped = true;
            if (_held != null)
                End(_held, FlockPartyEndReason.SignedOut, raise: false);
            StopRefreshing();
        }

        // A change keeps the party: once the server has it, the party is read again, so its events come from the same place
        // whether this game or another player changed it.
        private async Task ChangeAsync(FlockParty party, Func<Task> send, CancellationToken cancellationToken)
        {
            RequireRunning(party);
            await SendChangeAsync(party, send);
            try
            {
                Apply(await ReadNumberedAsync(party.SignInNumber, null, cancellationToken), null);
            }
            catch (OperationCanceledException)
            {
                // The change went through; the next refresh brings the party.
            }
            catch (FlockException failure)
            {
                _client.Logger.LogWarning($"The party changed, but reading it afterwards failed: {failure.Message}. The next refresh brings it.");
            }
        }

        // A party the server no longer has for this player is over here too, whichever call found out.
        private async Task SendChangeAsync(FlockParty party, Func<Task> send)
        {
            try
            {
                await send();
            }
            catch (FlockException gone) when (gone.ErrorCode == FlockErrorCode.PartyNotFound)
            {
                End(party, FlockPartyEndReason.Removed);
                throw;
            }
            _readsSettledThrough = _readsSent;
        }

        // A reading taken over by a newer one, or by a change finishing, is read again so the caller gets the server's latest.
        private async Task<PartyRead> ReadUntilNewestAsync(int signInNumber, string partyId, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                PartyRead read = await ReadNumberedAsync(signInNumber, partyId, cancellationToken);
                if (read.Number > _readsSettledThrough)
                    return read;
                if (attempt == MostReadsWhileThePartyChanges)
                    throw new FlockException("The party kept changing while it was being read. Try again.");
            }
        }

        // Numbered as it is sent: the number, not the order answers arrive in, says which reading is newer.
        private async Task<PartyRead> ReadNumberedAsync(int signInNumber, string partyId, CancellationToken cancellationToken)
        {
            int number = ++_readsSent;
            PartyDetailRecord party = partyId == null
                ? await _requests.ReadMineAsync(signInNumber, cancellationToken)
                : await _requests.ReadAsync(signInNumber, partyId, cancellationToken);
            return new PartyRead(number, signInNumber, party);
        }

        /// <summary>The one place a reading changes the party; holds the party when a call asked for it (<paramref name="playerId"/> given), else returns the held one or null.</summary>
        private FlockParty Apply(PartyRead read, string playerId)
        {
            // A party from a sign-in that ended is over first; that sign-in's late readings are older than any newer one.
            EndIfSignInEnded();
            if (read.Number <= _readsSettledThrough)
                return _held;
            _readsSettledThrough = read.Number;

            PartyDetailRecord party = read.Party;
            if (_held != null && party != null && _held.Id == party.Id)
            {
                _held.Update(party, MemberIds(party));
                if (playerId != null)
                    KeepRefreshing();
                return _held;
            }
            // In no party now, or in another one than the one held.
            if (_held != null)
                End(_held, _endingByThisGame != FlockPartyEndReason.None ? _endingByThisGame : FlockPartyEndReason.Removed);
            if (party == null || playerId == null)
                return null;
            return Hold(read.SignInNumber, playerId, party, MemberIds(party));
        }

        private FlockParty Hold(int signInNumber, string playerId, PartyRecord party, IReadOnlyList<string> memberIds)
        {
            EndIfSignInEnded();
            if (_held != null && _held.Id == party.Id)
            {
                _held.Update(party, memberIds);
            }
            else
            {
                if (_held != null)
                    End(_held, FlockPartyEndReason.Removed);
                _held = new FlockParty(this, signInNumber, playerId, party, memberIds);
            }
            KeepRefreshing();
            return _held;
        }

        // Settles everything before raising Ended, so a handler that calls back in finds the party over.
        private void End(FlockParty party, FlockPartyEndReason reason, bool raise = true)
        {
            if (party.EndReasonSet != FlockPartyEndReason.None)
                return;
            if (_held == party)
            {
                _held = null;
                StopRefreshing();
            }
            party.End(reason, raise);
        }

        // Also restarts a refresh that a failure stopped, whenever the game asks for its party again.
        private void KeepRefreshing()
        {
            if (_refreshInterval <= TimeSpan.Zero || _held == null)
                return;
            if (_refresh != null && _refresh.IsRunning)
                return;
            int signInNumber = _held.SignInNumber;
            _refresh = _repeatingCalls.Start(
                RefreshCallName,
                _refreshInterval,
                cancellationToken => ReadNumberedAsync(signInNumber, null, cancellationToken),
                read => Apply(read, null),
                failure => _client.Logger.LogWarning($"The party is no longer refreshed: {failure.Message}. GetMyPartyAsync starts it again."));
        }

        private void StopRefreshing()
        {
            _refresh?.Stop();
            _refresh = null;
        }

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
                throw new OperationCanceledException("The player signed out or changed, or Flock shut down, while the party was on its way");
        }

        private static void RequireRunning(FlockParty party)
        {
            if (party.HasEnded)
                throw new FlockValidationException($"This party has ended ({party.EndReason}). Create or join another, or get the player's current one with GetMyPartyAsync.");
        }

        private static IReadOnlyList<string> MemberIds(PartyDetailRecord party)
        {
            List<string> ids = new List<string>(party.Members?.Count ?? 0);
            if (party.Members != null)
            {
                foreach (PartyMemberRecord member in party.Members)
                    ids.Add(member.PlayerId);
            }
            return ids;
        }

        /// <summary>One reading of the player's party, numbered when it was sent; a null party means the player is in none.</summary>
        internal sealed class PartyRead
        {
            internal PartyRead(int number, int signInNumber, PartyDetailRecord party)
            {
                Number = number;
                SignInNumber = signInNumber;
                Party = party;
            }

            internal int Number { get; }
            internal int SignInNumber { get; }
            internal PartyDetailRecord Party { get; }
        }
    }
}
