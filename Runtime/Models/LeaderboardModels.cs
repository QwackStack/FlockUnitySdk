using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Flock.Models
{
    /// <summary>What a board's scores measure. Duration scores are in seconds — the name says so because the wire value doesn't.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum FlockLeaderboardValueType
    {
        [EnumMember(Value = "integer")]
        Integer,

        [EnumMember(Value = "float")]
        Float,

        [EnumMember(Value = "duration")]
        DurationSeconds
    }

    /// <summary>Which end of the scale wins — high score or best time.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum FlockLeaderboardDirection
    {
        [EnumMember(Value = "higher")]
        Higher,

        [EnumMember(Value = "lower")]
        Lower
    }

    /// <summary>How repeated writes to the source field fold into one score.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum FlockLeaderboardAggregation
    {
        [EnumMember(Value = "best")]
        Best,

        [EnumMember(Value = "latest")]
        Latest,

        [EnumMember(Value = "sum")]
        Sum
    }

    /// <summary>How a board buckets over time. Board config — not the window key you read with (<see cref="FlockLeaderboardWindow"/>).</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum FlockLeaderboardWindowType
    {
        [EnumMember(Value = "never")]
        Never,

        [EnumMember(Value = "weekly")]
        Weekly,

        [EnumMember(Value = "seasonal")]
        Seasonal
    }

    /// <summary>Whether the board ranks everyone together or per country.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum FlockLeaderboardScope
    {
        [EnumMember(Value = "global")]
        Global,

        [EnumMember(Value = "country")]
        Country
    }

    /// <summary>A board's public configuration, looked up by name. The player-data field it projects over is deliberately not exposed.</summary>
    public class Leaderboard
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("value_type")]
        public FlockLeaderboardValueType ValueType { get; set; }

        [JsonProperty("direction")]
        public FlockLeaderboardDirection Direction { get; set; }

        [JsonProperty("aggregation")]
        public FlockLeaderboardAggregation Aggregation { get; set; }

        [JsonProperty("window_type")]
        public FlockLeaderboardWindowType WindowType { get; set; }

        [JsonProperty("scope")]
        public FlockLeaderboardScope Scope { get; set; }

        /// <summary>True when a bigger number ranks better — sort and label from this instead of re-reading <see cref="Direction"/> everywhere.</summary>
        [JsonIgnore]
        public bool IsHigherBetter => Direction == FlockLeaderboardDirection.Higher;

        /// <summary>Formats a score the way this board measures. An unranked player's null score formats as empty.</summary>
        public string FormatScore(double? score)
        {
            if (!score.HasValue || double.IsNaN(score.Value) || double.IsInfinity(score.Value))
                return string.Empty;

            switch (ValueType)
            {
                case FlockLeaderboardValueType.DurationSeconds:
                    return FormatDuration(score.Value);
                case FlockLeaderboardValueType.Float:
                    return score.Value.ToString("0.##", CultureInfo.InvariantCulture);
                default:
                    return score.Value.ToString("0", CultureInfo.InvariantCulture);
            }
        }

        // Seconds in, clock out: h:mm:ss.fff once past an hour, m:ss.fff below it.
        private static string FormatDuration(double seconds)
        {
            bool negative = seconds < 0d;
            TimeSpan span = TimeSpan.FromSeconds(Math.Abs(seconds));
            string text = span.TotalHours >= 1d
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}"
                : $"{span.Minutes}:{span.Seconds:00}.{span.Milliseconds:000}";
            return negative ? "-" + text : text;
        }
    }

    /// <summary>Which window of a board to read — a window *key*, not the board's <see cref="FlockLeaderboardWindowType"/>. `never`/`weekly`/`seasonal` describe how a board buckets and are never sent here.</summary>
    /// <remarks>Default (<see cref="Current"/>) sends no parameter, which the API reads as the live window: all-time, the current week, or the current season depending on the board.</remarks>
    public readonly struct FlockLeaderboardWindow
    {
        private readonly string _wire;

        private FlockLeaderboardWindow(string wire)
        {
            _wire = wire;
        }

        /// <summary>The board's live window — all-time, current week, or current season. Sends nothing.</summary>
        public static FlockLeaderboardWindow Current => new FlockLeaderboardWindow(null);

        /// <summary>One finished season on a seasonal board; builds the `season:{id}` key the API expects.</summary>
        public static FlockLeaderboardWindow Season(string seasonId) => new FlockLeaderboardWindow("season:" + seasonId);

        /// <summary>A raw period key, e.g. "2026-W31" on a weekly board. Sent verbatim.</summary>
        public static FlockLeaderboardWindow Period(string periodKey) => new FlockLeaderboardWindow(periodKey);

        public string ToWireValue() => _wire;
    }

    /// <summary>One row of a leaderboard's standings.</summary>
    public class StandingEntry
    {
        [JsonProperty("rank")]
        public int Rank { get; set; }

        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("player_name")]
        public string PlayerName { get; set; }

        [JsonProperty("score")]
        public double Score { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("achieved_at")]
        public string AchievedAt { get; set; }
    }

    /// <summary>A slice of a leaderboard for one window — <see cref="Total"/> is the full entry count, not the size of <see cref="Items"/>.</summary>
    public class Standings
    {
        [JsonProperty("window")]
        public string Window { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("items")]
        public List<StandingEntry> Items { get; set; }
    }

    /// <summary>The signed-in player's placement — <see cref="Rank"/> and <see cref="Score"/> are null when they have no entry on the board yet.</summary>
    public class PlayerRank
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("window")]
        public string Window { get; set; }

        [JsonProperty("rank")]
        public int? Rank { get; set; }

        [JsonProperty("score")]
        public double? Score { get; set; }
    }
}
