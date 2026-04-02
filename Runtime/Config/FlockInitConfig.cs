using System.Collections.Generic;
using Flock.Analytics;
using Flock.Http;

namespace Flock.Config
{
    public class FlockInitConfig
    {
        public string ApiUrl { get; set; }
        public string ApiKey { get; set; }
        public string GameId { get; set; }
        public string GameVersionId { get; set; }
        public bool EnableDebugLogs { get; set; }
        public RetryPolicy RetryPolicy { get; set; }
        public FlockAnalyticsConfig Analytics { get; set; }
        private Dictionary<string, string> _headers;
        public FlockInitConfig(
            string apiUrl,
            string apiKey,
            string gameId,
            string gameVersionId,
            bool enableDebugLogs = false,
            FlockAnalyticsConfig analytics = null,
            RetryPolicy retryPolicy = null)
        {
            ApiUrl = apiUrl;
            ApiKey = apiKey;
            GameId = gameId;
            GameVersionId = gameVersionId;
            EnableDebugLogs = enableDebugLogs;
            Analytics = analytics;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
        }

        public Dictionary<string, string> GetBaseHeaders()
        {
            _headers ??= new Dictionary<string, string>();
            _headers.TryAdd("X-Flock-API-Key", ApiKey);
            _headers.TryAdd("X-Game-Version-ID", GameVersionId);
            return _headers;
        }
    }
}
