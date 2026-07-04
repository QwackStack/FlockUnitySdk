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

        /// <summary>Per-download timeout for asset (S3) downloads, default off (TimeSpan.Zero) so large assets aren't aborted mid-transfer. Mirrors Addressables' Timeout; separate from HttpTimeout, which only covers API/JSON calls.</summary>
        public TimeSpan AssetDownloadTimeout { get; set; } = TimeSpan.Zero;

        /// <summary>Retry attempts for a failed asset download, independent of the API RetryPolicy (mirrors Addressables' RetryCount). Default 3; set 0 to disable. Backoff/jitter come from RetryPolicy; only transient failures retry (permanent 4xx do not).</summary>
        public int AssetDownloadRetryCount { get; set; } = 3;

        /// <summary>
        /// Maximum number of asset downloads that run in parallel during batch calls.
        /// Default <c>4</c>; set to <c>0</c> or negative to use no limit (matches old behaviour).
        /// </summary>
        public int AssetMaxConcurrentDownloads { get; set; } = 4;

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

        /// <summary>Per-request timeout for SDK HTTP calls (default 30s; the client otherwise waits 100s). Asset downloads use UnityWebRequest and are unaffected.</summary>
        public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
        /// <param name="gameVersion"> Flock version name. The matching version ID is resolved at edit time (Flock > Settings) and baked into FlockConfig; runtime init uses it directly.</param>
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
            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "X-Flock-API-Key", _apiKey }
            };
            headers["X-Game-Version-ID"] = GameVersionId;
            return headers;
        }

    }
}
