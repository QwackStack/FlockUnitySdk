using Flock.Analytics;
using Flock.Exceptions;

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
        Facebook,
        Discord,
        /// <summary>Restored from the token store at startup.</summary>
        SessionRestore
    }

    /// <summary>A credential kind that can be linked to a player. Mirrors the backend LoginType set; Unknown = a provider this SDK version predates.</summary>
    public enum FlockCredentialProvider
    {
        Unknown = 0,
        DeviceId,
        Email,
        Google,
        Apple,
        Facebook,
        Steam,
        Discord
    }

    /// <summary>Wire mapping for <see cref="FlockCredentialProvider"/> — the backend spells these as LoginType strings.</summary>
    public static class FlockCredentialProviders
    {
        /// <summary>Wire string for a provider. Throws on Unknown, which is never a valid request value.</summary>
        public static string ToWire(FlockCredentialProvider provider)
        {
            switch (provider)
            {
                case FlockCredentialProvider.DeviceId: return "device_id";
                case FlockCredentialProvider.Email: return "email";
                case FlockCredentialProvider.Google: return "google";
                case FlockCredentialProvider.Apple: return "apple";
                case FlockCredentialProvider.Facebook: return "facebook";
                case FlockCredentialProvider.Steam: return "steam";
                case FlockCredentialProvider.Discord: return "discord";
                default: throw new FlockValidationException($"'{provider}' is not a credential provider the SDK can send.");
            }
        }

        /// <summary>Parses a wire string; returns Unknown for null/empty or anything this SDK version doesn't know.</summary>
        public static FlockCredentialProvider Parse(string wire)
        {
            if (string.IsNullOrEmpty(wire))
                return FlockCredentialProvider.Unknown;

            switch (wire.ToLowerInvariant())
            {
                case "device_id": return FlockCredentialProvider.DeviceId;
                case "email": return FlockCredentialProvider.Email;
                case "google": return FlockCredentialProvider.Google;
                case "apple": return FlockCredentialProvider.Apple;
                case "facebook": return FlockCredentialProvider.Facebook;
                case "steam": return FlockCredentialProvider.Steam;
                case "discord": return FlockCredentialProvider.Discord;
                default: return FlockCredentialProvider.Unknown;
            }
        }
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
