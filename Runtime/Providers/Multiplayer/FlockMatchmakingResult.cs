using System;
using System.Collections.Generic;

namespace Flock.Providers
{
    /// <summary>How a search for a match ended. The server can add others, so compare <see cref="FlockMatchmakingResult.Outcome"/> with these and treat any other as ended without a match.</summary>
    public static class FlockMatchmakingOutcome
    {
        /// <summary>Players were matched; <see cref="FlockMatchmakingResult.Session"/> is the session the match seats them in.</summary>
        public const string Matched = "matched";

        /// <summary>No match was found within the server's time for a search (5 minutes). Search again to keep looking.</summary>
        public const string Expired = "expired";

        /// <summary>A player joined or left the party while it searched, so the server stopped the search; the leader searches again.</summary>
        public const string PartyChanged = "party_changed";

        /// <summary>Stopped by another game: a party member's, or this player's on another device. A search this game cancels ends the call with a cancellation instead.</summary>
        public const string Cancelled = "cancelled";
    }

    /// <summary>What the game can choose about a search.</summary>
    public sealed class FlockMatchmakingOptions
    {
        /// <summary>The game's own data on the search, passed to the server as given.</summary>
        public IReadOnlyDictionary<string, object> Attributes { get; set; }
    }

    /// <summary>A search for a match that has ended: matched, or how it ended without one.</summary>
    public sealed class FlockMatchmakingResult
    {
        internal FlockMatchmakingResult(string outcome, FlockMultiplayerSession session, IReadOnlyList<string> playerIds)
        {
            Outcome = outcome;
            Session = session;
            PlayerIds = playerIds == null ? (IReadOnlyList<string>)Array.Empty<string>() : new List<string>(playerIds).AsReadOnly();
        }

        /// <summary>One of <see cref="FlockMatchmakingOutcome"/>.</summary>
        public string Outcome { get; }

        public bool IsMatched => Outcome == FlockMatchmakingOutcome.Matched;

        /// <summary>The session the match seats its players in, held like any other; null without a match, when the game's multiplayer settings make no session on a match, or when reading it failed (GetMySessionAsync finds it).</summary>
        public FlockMultiplayerSession Session { get; }

        /// <summary>The matched players' ids, the longest waiting first; empty without a match.</summary>
        public IReadOnlyList<string> PlayerIds { get; }
    }
}
