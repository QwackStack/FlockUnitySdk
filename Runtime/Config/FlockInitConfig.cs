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
        private string _apiUrl;
        private FlockAnalyticsConfig _analyticsConfig = new FlockAnalyticsConfig();

        /// <summary>Flock endpoint, without the version segment. Surrounding spaces and trailing slashes are removed, so a URL pasted as "https://host/ " reaches the same routes as in the editor's tools.</summary>
        public string ApiUrl { get => _apiUrl; set => _apiUrl = TrimApiUrl(value); }
        public string GameId { get; set; }
        public string GameVersion { get; set; }
        /// <summary>The ID of <see cref="GameVersion"/>. FlockConfig bakes it at edit time; a config built by hand sets it, since runtime init never contacts the server to resolve it.</summary>
        public string GameVersionId { get; set; }
        public bool EnableDebugLogs { get; set; }
        /// <summary>
        /// When true, asset downloads are cached on disk keyed by asset ID and the
        /// asset's <c>UpdatedAt</c>. Later downloads of the same asset version
        /// are served from disk without hitting S3.
        /// </summary>
        public bool EnableAssetCache { get; set; } = true;

        /// <summary>
        /// Absolute path for the asset cache directory. When null/empty, defaults to
        /// <c>Application.persistentDataPath/Flock/flock_assets/</c>.
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

        /// <summary>How often the player's party is read again while the game holds it (default 10 s); each read is one API call. Zero or less turns it off, and the party then changes only when the game asks for it again.</summary>
        public TimeSpan PartyRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Flock analytics settings. Never null: null sets the defaults.</summary>
        public FlockAnalyticsConfig AnalyticsConfig { get => _analyticsConfig; set => _analyticsConfig = value ?? new FlockAnalyticsConfig(); }

        /// <summary>
        /// Persistence layer for auth tokens between app launches. Defaults to the
        /// platform-appropriate <see cref="ITokenStore"/>, keeping this game's sign-in apart from every other game's (the game is
        /// the API URL and the API key). Set it before <c>FlockClient.Create</c> to keep tokens
        /// your own way; null keeps nothing between launches. After init, call
        /// <c>FlockClient.Instance.Authentication.TryRestoreSessionAsync()</c>
        /// to resume a stored session.
        /// </summary>
        public ITokenStore TokenStore
        {
            get
            {
                if (_tokenStoreChosen)
                    return _tokenStore;
                // Made for the game the config names when the store is needed, so an API URL set after the constructor keeps the
                // sign-in with the game the analytics are kept for.
                string gameKey = GameKey;
                if (_gameTokenStore == null || _gameTokenStoreKey != gameKey)
                {
                    _gameTokenStore = TokenStoreFactory.CreateForGame(gameKey);
                    _gameTokenStoreKey = gameKey;
                }
                return _gameTokenStore;
            }
            set
            {
                _tokenStore = value;
                _tokenStoreChosen = true;
            }
        }

        private ITokenStore _tokenStore;
        private bool _tokenStoreChosen;
        private ITokenStore _gameTokenStore;
        private string _gameTokenStoreKey;

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
            // Trimmed by the property: a leading space breaks WebGL's absolute-URL check and silently makes calls relative.
            ApiUrl = apiUrl;
            _apiKey = apiKey;
            GameId = gameId;
            GameVersion = gameVersion;
            EnableDebugLogs = enableDebugLogs;
            AnalyticsConfig = analyticsConfig;
            RetryPolicy = retryPolicy ?? new RetryPolicy();
        }

        /// <summary>This game as the server knows it (the backend and the API key): the name its saved sign-in and analytics files are kept under.</summary>
        internal string GameKey => FlockGameKey.Of(ApiUrl, _apiKey);

        /// <summary>The API URL as every request is built from it: surrounding spaces and trailing slashes removed.</summary>
        internal static string TrimApiUrl(string apiUrl) => apiUrl?.Trim().TrimEnd('/').TrimEnd();

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
