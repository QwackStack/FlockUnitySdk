using System;
using Newtonsoft.Json;

namespace Flock.Analytics
{
    // Tombstone persisted while a session runs; a survivor at next launch means dirty exit.
    internal class FlockTerminationMarker
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; }

        [JsonProperty("last_state")]
        public string LastState { get; set; }

        [JsonProperty("last_alive_utc")]
        public DateTime LastAliveUtc { get; set; }

        [JsonProperty("exception_count")]
        public int ExceptionCount { get; set; }
    }
}
