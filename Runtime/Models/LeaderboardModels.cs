using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    /// <summary>Which window of a board to read — a window *key*, not the board's window type. `never`/`weekly`/`seasonal` describe how a board buckets and are never sent here.</summary>
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
