using System;
using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>Why the relay cannot be used, one reason each, so the netcode that uses it can say what went wrong.</summary>
    internal static class FlockRelayFailure
    {
        /// <summary>A web player cannot send UDP.</summary>
        internal const string NotOnThisPlatform = "relay_not_on_this_platform";
        /// <summary>Flock listed no relay: switched off for this game in the dashboard, or none set up.</summary>
        internal const string NotOffered = "relay_not_offered";
        /// <summary>The studio's relay is paused until its bill is paid.</summary>
        internal const string Paused = "relay_paused";
        /// <summary>The player asked Flock for relay logins more than 12 times in a minute.</summary>
        internal const string TooManyLogins = "relay_too_many_logins";
        /// <summary>Flock says the player has no seat in the session.</summary>
        internal const string NotInSession = "relay_not_in_session";
        /// <summary>Flock did not answer the request for relay logins.</summary>
        internal const string FlockUnreachable = "flock_unreachable";
        /// <summary>Flock refused the request for relay logins for another reason.</summary>
        internal const string LoginsRefused = "relay_logins_refused";
        /// <summary>The relay server's name could not be looked up, or the server did not answer.</summary>
        internal const string Unreachable = "relay_unreachable";
        /// <summary>The relay refused the login Flock minted.</summary>
        internal const string WrongLogin = "relay_wrong_login";
        /// <summary>The relay has no room for another address.</summary>
        internal const string Full = "relay_full";
        /// <summary>The relay refused to let packets in from the host's relay address.</summary>
        internal const string OpenRefused = "relay_open_refused";
        /// <summary>The relay refused a request for another reason.</summary>
        internal const string Refused = "relay_refused";
        /// <summary>The relay let the address go: a renewal was refused, or none was answered before it lapsed.</summary>
        internal const string Lost = "relay_lost";
        /// <summary>The relay connection was closed by its owner (the session's end) before the call.</summary>
        internal const string Closed = "relay_closed";
    }

    /// <summary>The relay cannot be used, for <see cref="Reason"/>.</summary>
    internal sealed class FlockRelayException : FlockException
    {
        internal FlockRelayException(string reason, string message, Exception innerException = null) : base(message, innerException)
        {
            Reason = reason;
            Operation = "Open the relay";
        }

        /// <summary>One of <see cref="FlockRelayFailure"/>'s reasons.</summary>
        internal string Reason { get; }
    }
}
