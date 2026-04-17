using System.Collections.Generic;
using Flock.Analytics;
using Flock.Http;

namespace Flock.Config
{
    public class FlockInitConfig
    {
        public string ApiUrl { get; set; }
        public string GameId { get; set; }
        public string GameVersionId { get; set; }
        public bool EnableDebugLogs { get; set; }
        public RetryPolicy RetryPolicy { get; set; }
        /// <summary>
        /// Flock analytics Settings
        /// </summary>
        public FlockAnalyticsConfig AnalyticsConfig { get; set; }
        private readonly string _apiKey;

        private Dictionary<string, string> _headers;
        /// <summary>
        /// Flock Initialization Config
        /// <param name="apiUrl"> Flock endpoint.</param>
        /// <param name="apiKey"> Flock game secret key.</param>
        /// <param name="gameId"> Flock game ID</param>
        /// <param name="gameVersionId"> Flock version ID</param>
        /// <param name="enableDebugLogs"> enable debug logs , follows passed logger</param>
        /// <param name="analyticsConfig"> Flock Analytics settings</param>
        /// <param name="retryPolicy"> Flock Requests settings</param>
        /// </summary>
        public FlockInitConfig(
            string apiUrl,
            string apiKey,
            string gameId,
            string gameVersionId,
            bool enableDebugLogs = false,
            FlockAnalyticsConfig analyticsConfig = null,
            RetryPolicy retryPolicy = null)
        {
            ApiUrl = apiUrl;
            _apiKey = apiKey;
            GameId = gameId;
            GameVersionId = gameVersionId;
            EnableDebugLogs = enableDebugLogs;
            AnalyticsConfig = analyticsConfig;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
        }

        public Dictionary<string, string> GetBaseHeaders()
        {
            _headers ??= new Dictionary<string, string>();
            _headers.TryAdd("X-Flock-API-Key", _apiKey);
            _headers.TryAdd("X-Game-Version-ID", GameVersionId);
            return _headers;
        }
    }
}
