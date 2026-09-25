using System;
using Flock;

namespace Protokite.Playtest
{
    /// <summary>Whether the playtest can run, and if not, what stops it.</summary>
    public enum ProtokitePlaytestStatus
    {
        /// <summary>Playtesting is switched off, or the project has no playtest settings.</summary>
        TurnedOff,
        /// <summary>Playtesting is on but no Protokite API URL is set.</summary>
        ProtokiteApiUrlMissing,
        /// <summary>The Protokite API URL is not an http or https address.</summary>
        ProtokiteApiUrlUnusable,
        /// <summary>Everything is set, and the playtest is waiting for the Flock SDK to start.</summary>
        WaitingForFlock,
        /// <summary>The playtest can run.</summary>
        Ready
    }

    /// <summary>The playtest's entry point.</summary>
    public static class ProtokitePlaytest
    {
        /// <summary>
        /// Whether the playtest can run right now. Worked out on every read from the project's settings and whether the
        /// Flock SDK is running, so it follows Flock shutting down and starting again.
        /// </summary>
        public static ProtokitePlaytestStatus Status => StatusFor(ProtokitePlaytestSettings.Load(), FlockClient.IsInitialized);

        /// <summary>The status for these settings (null means the project has none) and whether Flock is running.</summary>
        internal static ProtokitePlaytestStatus StatusFor(ProtokitePlaytestSettings settings, bool flockIsRunning)
        {
            if (settings == null || !settings.PlaytestingEnabled)
                return ProtokitePlaytestStatus.TurnedOff;

            string url = settings.ProtokiteApiUrl?.Trim();
            if (string.IsNullOrEmpty(url))
                return ProtokitePlaytestStatus.ProtokiteApiUrlMissing;
            if (!IsUsableApiUrl(url))
                return ProtokitePlaytestStatus.ProtokiteApiUrlUnusable;

            return flockIsRunning ? ProtokitePlaytestStatus.Ready : ProtokitePlaytestStatus.WaitingForFlock;
        }

        // An absolute http or https address with a host; spaces around it are allowed, as the Flock SDK allows them in its own URL.
        private static bool IsUsableApiUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrEmpty(parsed.Host);
        }
    }
}
