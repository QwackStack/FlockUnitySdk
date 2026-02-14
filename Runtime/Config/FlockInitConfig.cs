using System.Collections.Generic;
using Flock.Http;

namespace Flock.Config
{
    //is this even required??
    public enum FlockEnvironment
    {
        Production,
        Preprod,
        Development
    }

    public class FlockInitConfig
    {
        public string ApiUrl { get; set; }
        public string ApiKey { get; set; }
        public string GameId { get; set; }
        public string GameVersionId { get; set; }
        public FlockEnvironment Environment { get; set; }
        public bool EnableDebugLogs { get; set; }
        public RetryPolicy RetryPolicy { get; set; }

        public FlockInitConfig(
            string apiUrl,
            string apiKey,
            string gameId,
            string gameVersionId,
            FlockEnvironment environment = FlockEnvironment.Production,
            bool enableDebugLogs = false,
            RetryPolicy retryPolicy = null)
        {
            ApiUrl = apiUrl;
            ApiKey = apiKey;
            GameId = gameId;
            GameVersionId = gameVersionId;
            Environment = environment;
            EnableDebugLogs = enableDebugLogs;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
        }

        public Dictionary<string, string> GetBaseHeaders()
        {
            return new Dictionary<string, string>
            {
                { "X-Flock-API-Key", ApiKey },
                { "X-Game-Version-ID", GameVersionId }
            };
        }

        public Dictionary<string, string> GetAuthenticatedHeaders(string accessToken)
        {
            var headers = GetBaseHeaders();
            if (!string.IsNullOrEmpty(accessToken))
            {
                headers["Authorization"] = $"Bearer{accessToken}";
            }

            return headers;
        }
    }
}