using System;

namespace Flock.Config
{
    public class FlockConfig
    {
        public string ApiUrl { get; set; }
        public string GameId { get; set; }
        public string Environment { get; set; }
        public bool EnableDebugLogs { get; set; }
        public TimeSpan Timeout { get; set; }

        public FlockConfig(string apiUrl, string gameId, string environment = "production")
        {
            ApiUrl = apiUrl;
            GameId = gameId;
            Environment = environment;
            EnableDebugLogs = false;
            Timeout = TimeSpan.FromSeconds(30);
        }

        public FlockConfig WithDebugLogs(bool enable = true)
        {
            EnableDebugLogs = enable;
            return this;
        }

        public FlockConfig WithTimeout(TimeSpan timeout)
        {
            Timeout = timeout;
            return this;
        }
    }
} 