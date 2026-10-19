using System;
using Flock.Exceptions;

namespace Flock.Providers
{
    /// <summary>Why Flock's relay could not be used (FlockNetcodeStartResult.RelayFailureReason). An open set: compare with these and keep a default.</summary>
    public static class FlockRelayFailure
    {
        /// <summary>A web player cannot send UDP.</summary>
        public const string NotOnThisPlatform = "relay_not_on_this_platform";
        /// <summary>Flock listed no relay: switched off for this game in the dashboard, or none set up.</summary>
        public const string NotOffered = "relay_not_offered";
        /// <summary>The host takes no players through the relay (its FlockNetcodeOptions.Relay is Never, or its relay could not be opened).</summary>
        public const string NotOfferedByTheHost = "relay_not_offered_by_host";
        /// <summary>Flock does not list, for this player, the relay server the host uses.</summary>
        public const string ServerNotListed = "relay_server_not_listed";
        /// <summary>The studio's relay is paused until its bill is paid.</summary>
        public const string Paused = "relay_paused";
        /// <summary>The player asked Flock for relay logins more than 12 times in a minute, or its seat drew the most logins one seat may (10).</summary>
        public const string TooManyLogins = "relay_too_many_logins";
        /// <summary>Flock says the player has no seat in the session.</summary>
        public const string NotInSession = "relay_not_in_session";
        /// <summary>Flock did not answer the request for relay logins.</summary>
        public const string FlockUnreachable = "flock_unreachable";
        /// <summary>Flock refused the request for relay logins for another reason.</summary>
        public const string LoginsRefused = "relay_logins_refused";
        /// <summary>The relay server's name could not be looked up, or the server did not answer.</summary>
        public const string Unreachable = "relay_unreachable";
        /// <summary>The relay refused the login Flock minted.</summary>
        public const string WrongLogin = "relay_wrong_login";
        /// <summary>The relay has no room for another address.</summary>
        public const string Full = "relay_full";
        /// <summary>The relay refused to let packets in from the host's relay address, or from a player's.</summary>
        public const string OpenRefused = "relay_open_refused";
        /// <summary>The relay refused a request for another reason.</summary>
        public const string Refused = "relay_refused";
        /// <summary>The relay let the address go (a renewal was refused, or none was answered before it lapsed), or it has answered nothing for 45 s: this device's packets no longer reach it.</summary>
        public const string Lost = "relay_lost";
        /// <summary>The relay connection was closed by its owner (the session's end) before the call.</summary>
        public const string Closed = "relay_closed";

        /// <summary>How to fix a relay failure of this reason: the one place each reason's fix is written.</summary>
        internal static string FixFor(string reason)
        {
            switch (reason)
            {
                case NotOnThisPlatform: return "Use a connection of the game's own from a web player, published with PublishConnectionAsync.";
                case NotOffered: return "Turn the relay on for the game in the Flock dashboard, or connect players directly (FlockNetcodeOptions.Relay Never).";
                case NotOfferedByTheHost: return "The host takes no players through the relay: have it start the netcode with FlockNetcodeOptions.Relay WhenNeeded or Always, or connect directly.";
                case ServerNotListed: return "The host and this player were given different relay servers; have the host start the netcode again so both use one Flock lists for each.";
                case Paused: return "Settle the studio's bill in the Flock dashboard; meanwhile players connect directly (FlockNetcodeOptions.Relay Never or WhenNeeded).";
                case TooManyLogins: return "Start the netcode once per session rather than again and again. A seat that drew the most logins it may (10) gets no more; another session gives a seat of its own.";
                case NotInSession: return "Join the session again (JoinSessionAsync) before starting the netcode.";
                case FlockUnreachable: return "Check the device is online, then start the netcode again.";
                case LoginsRefused: return "Fix what the reason above names, then start the netcode again.";
                case Unreachable: return "Check the device is online and that a firewall lets the game send UDP to the relay; otherwise players connect directly.";
                case WrongLogin: return "Start the netcode again so Flock gives fresh relay logins.";
                case Full: return "The relay has no room for another address now; try again shortly, or connect players directly.";
                case OpenRefused: return "That address cannot come in through this relay: the player connects directly, or joins the session again.";
                case Lost: return "Start the netcode again to reopen the relay; the session raises RelayLost when this happens.";
                case Closed: return "The session ended and closed its relay: host or join another session.";
                case Refused: return "The relay's own code is in the reason above. Start the netcode again so Flock gives fresh relay logins; if the relay keeps refusing, connect players directly (FlockNetcodeOptions.Relay Never).";
                default: return "Start the netcode again; if the relay keeps refusing, connect players directly.";
            }
        }
    }

    /// <summary>The relay cannot be used, for <see cref="Reason"/>, and how to fix it is the fix for that reason.</summary>
    internal sealed class FlockRelayException : FlockException
    {
        internal FlockRelayException(string reason, string message, Exception innerException = null) : base(message, innerException)
        {
            Reason = reason;
            Operation = "Open the relay";
            Hint = FlockRelayFailure.FixFor(reason);
        }

        /// <summary>One of <see cref="FlockRelayFailure"/>'s reasons.</summary>
        internal string Reason { get; }
    }
}
