using System;
using System.Collections.Generic;
using Flock.Analytics;
using Flock.Auth;
using Flock.Exceptions;
using Flock.Http;

namespace Flock.Config
{
    public class FlockInitConfig
    {
        public string ApiUrl { get; set; }
        public string GameId { get; set; }
        public string GameVersion { get; set; }
        public string GameVersionId { get; internal set; }
        public bool EnableDebugLogs { get; set; }
        /// <summary>
        /// When true, asset downloads are cached on disk keyed by asset ID and the
        /// asset's <c>UpdatedAt</c>. Later downloads of the same asset version
        /// are served from disk without hitting S3.
        /// </summary>
        public bool EnableAssetCache { get; set; } = true;

        /// <summary>
        /// Absolute path for the asset cache directory. When null/empty, defaults to
        /// <c>Application.persistentDataPath/flock_assets/</c>.
        /// </summary>
        public string AssetCacheDirectory { get; set; }

        /// <summary>
        /// Maximum size of the on-disk asset cache in megabytes. When the cache exceeds
        /// this size, least-recently-used entries are evicted until it fits. Default
        /// <c>100</c> MB; set to <c>0</c> for unlimited.
        /// </summary>
        public int AssetCacheMaxSizeMB { get; set; } = 100;

        /// <summary>
        /// When true, read-API responses are snapshotted to disk and served as a fallback
        /// when the network is unavailable. Disable on WebGL — persistentDataPath there
        /// does not support synchronous writes.
        /// </summary>
        public bool EnableOfflineCache { get; set; } = true;

        /// <summary>
        /// Absolute path for snapshot storage. When null/empty, defaults to
        /// <c>Application.persistentDataPath/Flock/snapshots/</c>.
        /// </summary>
        public string OfflineCacheDirectory { get; set; }

        public RetryPolicy RetryPolicy { get; set; }
        /// <summary>
        /// Flock analytics Settings
        /// </summary>
        public FlockAnalyticsConfig AnalyticsConfig { get; set; }

        /// <summary>
        /// Persistence layer for auth tokens between app launches. Defaults to the
        /// platform-appropriate <see cref="ITokenStore"/> selected by
        /// <see cref="TokenStoreFactory.Create"/>. After init, call
        /// <c>FlockClient.Instance.Authentication.TryRestoreSessionAsync()</c>
        /// to resume a stored session.
        /// </summary>
        public ITokenStore TokenStore { get;}

        private readonly string _apiKey;

        /// <summary>
        /// Flock Initialization Config
        /// <param name="apiUrl"> Flock endpoint.</param>
        /// <param name="apiKey"> Flock game secret key.</param>
        /// <param name="gameId"> Flock game ID</param>
        /// <param name="gameVersion"> Flock version name. The matching version ID is resolved from the backend during <see cref="FlockClient.CreateAsync"/>.</param>
        /// <param name="enableDebugLogs"> enable debug logs , follows passed logger</param>
        /// <param name="analyticsConfig"> Flock Analytics settings</param>
        /// <param name="retryPolicy"> Flock Requests settings</param>
        /// </summary>
        public FlockInitConfig(
            string apiUrl,
            string apiKey,
            string gameId,
            string gameVersion,
            bool enableDebugLogs = false,
            FlockAnalyticsConfig analyticsConfig = null,
            RetryPolicy retryPolicy = null)
        {
            ApiUrl = apiUrl;
            _apiKey = apiKey;
            GameId = gameId;
            GameVersion = gameVersion;
            EnableDebugLogs = enableDebugLogs;
            AnalyticsConfig = analyticsConfig;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
            TokenStore = TokenStoreFactory.Create();
        }

        public Dictionary<string, string> GetBaseHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                { "X-Flock-API-Key", _apiKey }
            };
            headers["X-Game-Version-ID"] = GameVersionId;
            return headers;
        }

        internal Dictionary<string, string> GetBootstrapHeaders()
        {
            return new Dictionary<string, string>
            {
                { "X-Flock-API-Key", _apiKey }
            };
        }
        
    }
}
