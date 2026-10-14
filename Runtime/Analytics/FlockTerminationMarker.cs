using System;
using Newtonsoft.Json;

namespace Flock.Analytics
{
    // Tombstone persisted while the launch runs; a survivor at next launch means dirty exit.
    internal class FlockTerminationMarker
    {
        // Null while no session runs: before sign-in, or after one ended.
        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("last_state")]
        public string LastState { get; set; }

        [JsonProperty("last_alive_utc")]
        public DateTime LastAliveUtc { get; set; }

        [JsonProperty("exception_count")]
        public int ExceptionCount { get; set; }

        // The server's id for the session, once it registered; null before.
        [JsonProperty("server_session_id")]
        public string ServerSessionId { get; set; }

        // The launch's own versions, so the next launch reports the ones that ended, not its own.
        [JsonProperty("app_version")]
        public string AppVersion { get; set; }

        [JsonProperty("sdk_version")]
        public string SdkVersion { get; set; }
    }
}
