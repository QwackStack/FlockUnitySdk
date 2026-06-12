using Flock.Analytics;

namespace Flock
{
    /// <summary>How the player authenticated (see <see cref="FlockEvents.OnAuthenticated"/>).</summary>
    public enum FlockAuthMethod
    {
        Email,
        Device,
        Google,
        Apple,
        Steam,
        /// <summary>Restored from the token store at startup.</summary>
        SessionRestore
    }

    /// <summary>Payload of <see cref="FlockEvents.OnAuthenticated"/>.</summary>
    public sealed class FlockAuthInfo
    {
        /// <summary>Player id from the access-token claims.</summary>
        public string PlayerId { get; }

        public FlockAuthMethod Method { get; }

        public FlockAuthInfo(string playerId, FlockAuthMethod method)
        {
            PlayerId = playerId;
            Method = method;
        }
    }

    /// <summary>Why a session ended (see <see cref="FlockEvents.OnSessionEnded"/>).</summary>
    public enum FlockSessionEndReason
    {
        /// <summary>The player logged out or auth tokens were cleared.</summary>
        Logout,
        /// <summary>Backgrounded past the session timeout.</summary>
        Timeout,
        /// <summary>The application quit.</summary>
        Quit,
        /// <summary>A new session replaced this one.</summary>
        Restarted,
        /// <summary>Ended explicitly via the analytics provider.</summary>
        Manual
    }

    /// <summary>Payload of <see cref="FlockEvents.OnSessionEnded"/>.</summary>
    public sealed class FlockSessionEndedArgs
    {
        /// <summary>Final session metrics (duration, screens, pauses, FPS).</summary>
        public FlockSessionSnapshot Snapshot { get; }

        public FlockSessionEndReason Reason { get; }

        public FlockSessionEndedArgs(FlockSessionSnapshot snapshot, FlockSessionEndReason reason)
        {
            Snapshot = snapshot;
            Reason = reason;
        }
    }
}
