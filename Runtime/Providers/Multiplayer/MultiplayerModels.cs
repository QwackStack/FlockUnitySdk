using Newtonsoft.Json;

namespace Flock.Providers
{
    /// <summary>One row of the game's matchmaking queue list, as much of it as the SDK reads.</summary>
    internal sealed class MatchmakingQueue
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
