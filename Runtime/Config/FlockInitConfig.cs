using System.Collections.Generic;
using System.Text;
using Flock.Http;

namespace Flock.Config
{
    public class FlockInitConfig
    {
        public string ApiUrl { get; set; }
        public string ApiKey { get; set; }
        //for now only leaderboard uses
        public string GameId { get; set; }
        public string GameVersionId { get; set; }
        public bool EnableDebugLogs { get; set; }
        public RetryPolicy RetryPolicy { get; set; }

        public FlockInitConfig(
            string apiUrl,
            string apiKey,
            string gameId,
            string gameVersionId,
            bool enableDebugLogs = false,
            RetryPolicy retryPolicy = null)
        {
            ApiUrl = apiUrl;
            ApiKey = apiKey;
            GameId = gameId;
            GameVersionId = gameVersionId;
            EnableDebugLogs = enableDebugLogs;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
        }

        public Dictionary<string, string> GetBaseHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                { "X-Flock-API-Key", ApiKey }
            };

            if (!string.IsNullOrEmpty(GameVersionId))
                headers["X-Game-Version-ID"] = GameVersionId;

            return headers;
        }

        public Dictionary<string, string> GetAuthenticatedHeaders(string accessToken)
        {
            var headers = GetBaseHeaders();
            if (!string.IsNullOrEmpty(accessToken))
                headers["Authorization"] = new StringBuilder().Append("Bearer ").Append(accessToken).ToString();

            return headers;
        }
    }
}
