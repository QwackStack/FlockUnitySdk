using System.Collections.Generic;
using Flock.Interfaces;

namespace Flock
{
    /// <summary>Typed calls for the tracking plan's common gameplay events; each is a <see cref="IAnalyticProvider.TrackEvent"/> with the plan's name and properties.</summary>
    /// <remarks>Every argument is optional and one left unset is not sent. A named argument wins over the same key in <c>extraProperties</c>, which is never changed.</remarks>
    public static class FlockGameplayEvents
    {
        internal const string GameplayCategory = "gameplay";
        internal const string InterfaceCategory = "ui";

        internal const string LevelStart = "level_start";
        internal const string LevelComplete = "level_complete";
        internal const string LevelFail = "level_fail";
        internal const string CheckpointStart = "checkpoint_start";
        internal const string TutorialStep = "tutorial_step";
        internal const string SettingsChange = "settings_change";
        internal const string MatchStart = "match_start";
        internal const string MatchComplete = "match_complete";

        /// <summary>A player begins a level or chapter, after loading finishes (level_start).</summary>
        public static bool TrackLevelStart(this IAnalyticProvider analytics, int? level = null, string difficulty = null,
            Dictionary<string, object> extraProperties = null)
            => Track(analytics, LevelStart, GameplayCategory, extraProperties,
                ("level", level), ("difficulty", difficulty));

        /// <summary>A player finishes a level, before the results screen (level_complete).</summary>
        public static bool TrackLevelComplete(this IAnalyticProvider analytics, int? level = null, int? score = null,
            int? durationSeconds = null, int? stars = null, Dictionary<string, object> extraProperties = null)
            => Track(analytics, LevelComplete, GameplayCategory, extraProperties,
                ("level", level), ("score", score), ("duration_seconds", durationSeconds), ("stars", stars));

        /// <summary>A player loses or leaves a level unfinished: death, timeout or quitting (level_fail).</summary>
        public static bool TrackLevelFail(this IAnalyticProvider analytics, int? level = null, string reason = null,
            int? durationSeconds = null, Dictionary<string, object> extraProperties = null)
            => Track(analytics, LevelFail, GameplayCategory, extraProperties,
                ("level", level), ("reason", reason), ("duration_seconds", durationSeconds));

        /// <summary>A checkpoint or save point inside a level activates (checkpoint_start).</summary>
        public static bool TrackCheckpoint(this IAnalyticProvider analytics, int? level = null, string checkpoint = null,
            Dictionary<string, object> extraProperties = null)
            => Track(analytics, CheckpointStart, GameplayCategory, extraProperties,
                ("level", level), ("checkpoint", checkpoint));

        /// <summary>The player completes a tutorial step (tutorial_step, category ui).</summary>
        public static bool TrackTutorialStep(this IAnalyticProvider analytics, int? step = null, string stepName = null,
            Dictionary<string, object> extraProperties = null)
            => Track(analytics, TutorialStep, InterfaceCategory, extraProperties,
                ("step", step), ("step_name", stepName));

        /// <summary>The player saves a setting, not every slider tick (settings_change, category ui).</summary>
        public static bool TrackSettingsChange(this IAnalyticProvider analytics, string setting = null, string value = null,
            Dictionary<string, object> extraProperties = null)
            => Track(analytics, SettingsChange, InterfaceCategory, extraProperties,
                ("setting", setting), ("value", value));

        /// <summary>A match goes live, when players can act (match_start).</summary>
        public static bool TrackMatchStart(this IAnalyticProvider analytics, string mode = null, string map = null,
            int? teamSize = null, Dictionary<string, object> extraProperties = null)
            => Track(analytics, MatchStart, GameplayCategory, extraProperties,
                ("mode", mode), ("map", map), ("team_size", teamSize));

        /// <summary>A match ends with a final result, once per player (match_complete); result is one of <see cref="FlockMatchResult"/> or the game's own.</summary>
        public static bool TrackMatchComplete(this IAnalyticProvider analytics, string mode = null, string result = null,
            int? score = null, int? durationSeconds = null, int? kills = null, Dictionary<string, object> extraProperties = null)
            => Track(analytics, MatchComplete, GameplayCategory, extraProperties,
                ("mode", mode), ("result", result), ("score", score), ("duration_seconds", durationSeconds), ("kills", kills));

        private static bool Track(IAnalyticProvider analytics, string eventName, string category,
            Dictionary<string, object> extraProperties, params (string Key, object Value)[] named)
        {
            Dictionary<string, object> properties = extraProperties != null
                ? new Dictionary<string, object>(extraProperties)
                : new Dictionary<string, object>();
            foreach ((string key, object value) in named)
            {
                if (value != null)
                    properties[key] = value;
            }
            return analytics.TrackEvent(eventName, properties, category);
        }
    }

    /// <summary>The match results the tracking plan names; the server takes any string, so a game may send its own.</summary>
    public static class FlockMatchResult
    {
        public const string Win = "win";
        public const string Loss = "loss";
        public const string Draw = "draw";
    }
}
