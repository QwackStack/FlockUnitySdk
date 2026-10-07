using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Providers
{
    /// <summary>Why a party stopped being the player's.</summary>
    public enum FlockPartyEndReason
    {
        /// <summary>The party has not ended.</summary>
        None,
        /// <summary>The player left it with <see cref="FlockParty.LeaveAsync"/>.</summary>
        Left,
        /// <summary>The player disbanded it with <see cref="FlockParty.DisbandAsync"/>.</summary>
        Disbanded,
        /// <summary>The server no longer lists the player in it: removed by the leader, the party disbanded, or the player left it from another device.</summary>
        Removed,
        /// <summary>The sign-in the party belonged to ended: a sign-out, another player signing in, or Flock shutting down. The player is still in the party on the server.</summary>
        SignedOut,
    }

    /// <summary>One player in a party.</summary>
    public sealed class FlockPartyMember
    {
        internal FlockPartyMember(string playerId)
        {
            PlayerId = playerId;
        }

        public string PlayerId { get; }
    }

    /// <summary>The player's party, read again every Party Refresh Seconds (Flock > Settings; 0 turns that off), after each change this game makes, and by GetMyPartyAsync; use it from the main thread.</summary>
    public sealed class FlockParty
    {
        private readonly FlockParties _owner;
        private readonly string _playerId;
        // Set once, by the owner, when the party ends; a sign-in that ended reads as SignedOut before then.
        private FlockPartyEndReason _endReason;

        internal FlockParty(FlockParties owner, int signInNumber, string playerId, PartyRecord party, IReadOnlyList<string> memberIds)
        {
            _owner = owner;
            SignInNumber = signInNumber;
            _playerId = playerId;
            Id = party.Id;
            CopyFrom(party, memberIds);
        }

        public string Id { get; }

        /// <summary>The code other players join with, through <see cref="FlockMultiplayerProvider.JoinPartyAsync"/>. Letter case does not matter.</summary>
        public string InviteCode { get; private set; }

        public string LeaderPlayerId { get; private set; }

        /// <summary>True when the signed-in player leads the party: only the leader can remove players, hand over leadership, update or disband it.</summary>
        public bool IsLeader => string.Equals(LeaderPlayerId, _playerId, StringComparison.Ordinal);

        /// <summary>The players in the party, in the order they joined. When the leader leaves, the first of the others leads. A new list on every change.</summary>
        public IReadOnlyList<FlockPartyMember> Members { get; private set; }

        public int MaxSize { get; private set; }

        /// <summary>The game's own data on the party, as the leader last set it with <see cref="UpdateAsync"/>. Read one value with <see cref="TryGetSetting{T}"/>.</summary>
        public IReadOnlyDictionary<string, object> Settings { get; private set; }

        public FlockPartyEndReason EndReason => _endReason != FlockPartyEndReason.None
            ? _endReason
            : SignInNumber != _owner.CurrentSignInNumber ? FlockPartyEndReason.SignedOut : FlockPartyEndReason.None;

        /// <summary>True once the party is no longer the player's here; its calls are then refused and it is no longer refreshed.</summary>
        public bool HasEnded => EndReason != FlockPartyEndReason.None;

        /// <summary>The players in the party changed.</summary>
        public event Action MembersChanged;

        /// <summary>Another player leads the party now.</summary>
        public event Action LeaderChanged;

        /// <summary>The party stopped being the player's, raised once. Not raised when Flock shuts down.</summary>
        public event Action<FlockPartyEndReason> Ended;

        internal int SignInNumber { get; }
        internal FlockPartyEndReason EndReasonSet => _endReason;

        /// <summary>Reads one <see cref="Settings"/> value as <typeparamref name="T"/>. False when the key is absent or the value cannot become a T.</summary>
        public bool TryGetSetting<T>(string key, out T value)
        {
            value = default;
            if (string.IsNullOrEmpty(key) || !Settings.TryGetValue(key, out object raw))
                return false;
            return FlockJsonValues.TryConvert(raw, out value);
        }

        /// <summary>Leaves the party. If the server says the player is already out of it, that counts as leaving. When the leader leaves, the first of the others leads; the last one out disbands it.</summary>
        public Task LeaveAsync(CancellationToken cancellationToken = default) => _owner.LeaveAsync(this, cancellationToken);

        /// <summary>Removes another player from the party. Leader only; the leader cannot remove themselves.</summary>
        public Task KickAsync(string playerId, CancellationToken cancellationToken = default) => _owner.KickAsync(this, playerId, cancellationToken);

        /// <summary>Hands leadership to another player in the party. Leader only.</summary>
        public Task MakeLeaderAsync(string playerId, CancellationToken cancellationToken = default) => _owner.MakeLeaderAsync(this, playerId, cancellationToken);

        /// <summary>Changes the party's size, its settings or both; leave one null to keep it. New settings replace all of the old ones. Leader only.</summary>
        public Task UpdateAsync(int? maxSize = null, IReadOnlyDictionary<string, object> settings = null, CancellationToken cancellationToken = default)
            => _owner.UpdateAsync(this, maxSize, settings, cancellationToken);

        /// <summary>Disbands the party, so every player in it is free to make or join another. Leader only.</summary>
        public Task DisbandAsync(CancellationToken cancellationToken = default) => _owner.DisbandAsync(this, cancellationToken);

        // Takes a newer reading of the party, then says what changed; events are raised once everything is in place.
        internal void Update(PartyRecord party, IReadOnlyList<string> memberIds)
        {
            bool membersChanged = !SameMembers(memberIds);
            bool leaderChanged = !string.Equals(LeaderPlayerId, party.LeaderPlayerId, StringComparison.Ordinal);
            CopyFrom(party, memberIds);

            if (membersChanged)
                FlockEvents.InvokeEach(MembersChanged, $"{nameof(FlockParty)}.{nameof(MembersChanged)}");
            if (leaderChanged)
                FlockEvents.InvokeEach(LeaderChanged, $"{nameof(FlockParty)}.{nameof(LeaderChanged)}");
        }

        internal void End(FlockPartyEndReason reason, bool raise)
        {
            _endReason = reason;
            if (raise)
                FlockEvents.InvokeEach(Ended, reason, $"{nameof(FlockParty)}.{nameof(Ended)}");
        }

        private void CopyFrom(PartyRecord party, IReadOnlyList<string> memberIds)
        {
            InviteCode = party.InviteCode;
            LeaderPlayerId = party.LeaderPlayerId;
            MaxSize = party.MaxSize;
            Settings = new ReadOnlyDictionary<string, object>(party.Settings == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(party.Settings));

            List<FlockPartyMember> members = new List<FlockPartyMember>(memberIds.Count);
            foreach (string memberId in memberIds)
                members.Add(new FlockPartyMember(memberId));
            Members = members.AsReadOnly();
        }

        private bool SameMembers(IReadOnlyList<string> memberIds)
        {
            if (Members.Count != memberIds.Count)
                return false;
            for (int i = 0; i < memberIds.Count; i++)
            {
                if (!string.Equals(Members[i].PlayerId, memberIds[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
