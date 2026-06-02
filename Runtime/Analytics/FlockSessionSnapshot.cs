using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Analytics
{
    public class FlockSessionSnapshot
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("server_session_id")]
        public string ServerSessionId { get; set; }

        [JsonProperty("session_number")]
        public int SessionNumber { get; set; }

        [JsonProperty("start_time_utc")]
        public DateTime StartTimeUtc { get; set; }

        [JsonProperty("end_time_utc")]
        public DateTime? EndTimeUtc { get; set; }

        [JsonProperty("last_heartbeat_utc")]
        public DateTime? LastHeartbeatUtc { get; set; }

        [JsonProperty("duration_seconds")]
        public float DurationSeconds { get; set; }

        [JsonProperty("total_pause_duration_seconds")]
        public float TotalPauseDurationSeconds { get; set; }

        [JsonProperty("pause_count")]
        public int PauseCount { get; set; }

        [JsonProperty("screens_viewed")]
        public int ScreensViewed { get; set; }

        [JsonProperty("screen_names")]
        public List<string> ScreenNames { get; set; }

        [JsonProperty("average_fps")]
        public float AverageFps { get; set; }

        [JsonProperty("min_fps")]
        public float MinFps { get; set; }

        [JsonProperty("max_fps")]
        public float MaxFps { get; set; }

        [JsonProperty("device_info")]
        public FlockDeviceInfo DeviceInfo { get; set; }

        [JsonProperty("is_active")]
        public bool IsActive { get; set; }

        [JsonProperty("is_bounce")]
        public bool IsBounce { get; set; }

        [JsonProperty("is_first_session")]
        public bool IsFirstSession { get; set; }
    }
}
