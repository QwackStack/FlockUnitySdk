using System;
using System.Collections.Generic;
using Flock.Models;

namespace Flock.Analytics
{
    /// <summary>The session_end event the SDK records when a session ends: why it ended, how long it ran, screens seen, bounce.</summary>
    internal static class FlockSessionEndEvent
    {
        internal const string EventName = "session_end";
        internal const string Category = "session";

        internal const string Quit = "quit";
        internal const string Timeout = "timeout";
        internal const string SignOut = "sign_out";
        internal const string PlayerChanged = "player_changed";
        internal const string Restarted = "restarted";
        internal const string EndedByTheGame = "ended_by_the_game";
        /// <summary>The launch ended without ending its session (a crash, or the system closing the app); found at the next launch.</summary>
        internal const string UnexpectedExit = "unexpected_exit";

        internal static string ReasonName(FlockSessionEndReason reason)
        {
            switch (reason)
            {
                case FlockSessionEndReason.Logout: return SignOut;
                case FlockSessionEndReason.Timeout: return Timeout;
                case FlockSessionEndReason.Quit: return Quit;
                case FlockSessionEndReason.Restarted: return Restarted;
                case FlockSessionEndReason.Manual: return EndedByTheGame;
                default: return reason.ToString().ToLowerInvariant();
            }
        }

        /// <summary>The server keeps a session's length in whole seconds; rounded, so 0.9 s is 1, not 0.</summary>
        internal static int WholeSeconds(float seconds) => (int)Math.Round(Math.Max(0f, seconds), MidpointRounding.AwayFromZero);

        internal static AnalyticsEventRequest Build(FlockSessionSnapshot snapshot, string reason, string playerId)
        {
            return new AnalyticsEventRequest
            {
                PlayerId = playerId,
                EventName = EventName,
                EventCategory = Category,
                // Null while the session has not reached the server; the server keeps the event without one.
                SessionId = snapshot.ServerSessionId,
                Timestamp = (snapshot.EndTimeUtc ?? DateTime.UtcNow).ToString("o"),
                Properties = Properties(snapshot, reason)
            };
        }

        private static Dictionary<string, object> Properties(FlockSessionSnapshot snapshot, string reason)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "reason", reason },
                { "duration_seconds", WholeSeconds(snapshot.DurationSeconds) },
                { "screens_viewed", snapshot.ScreensViewed },
                { "is_bounce", snapshot.IsBounce },
                { "session_number", snapshot.SessionNumber },
                { "is_first_session", snapshot.IsFirstSession },
                { "pause_count", snapshot.PauseCount }
            };
            // Only when frames were sampled: Track FPS off, or a session too short for a sample, has no frame rate.
            if (snapshot.AverageFps > 0f)
            {
                properties["average_fps"] = Math.Round(snapshot.AverageFps, 1);
                properties["min_fps"] = Math.Round(snapshot.MinFps, 1);
            }
            return properties;
        }
    }
}
