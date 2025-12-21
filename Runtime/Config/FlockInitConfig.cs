using Flock.Http;

namespace Flock.Config
{
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
        public FlockEnvironment Environment { get; set; }
        public bool EnableDebugLogs { get; set; }
        public RetryPolicy RetryPolicy { get; set; }

        public FlockInitConfig(string apiUrl, string apiKey, FlockEnvironment environment = FlockEnvironment.Production, bool enableDebugLogs = false, RetryPolicy retryPolicy = null)
        {
            ApiUrl = apiUrl;
            ApiKey = apiKey;
            Environment = environment;
            EnableDebugLogs = enableDebugLogs;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
        }
    }
}