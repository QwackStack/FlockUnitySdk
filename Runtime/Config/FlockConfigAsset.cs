using Flock.Analytics;
using Flock.Http;
using UnityEngine;
using UnityEngine.Serialization;

namespace Flock.Config
{
    [CreateAssetMenu(fileName = "FlockConfig", menuName = "Flock/SDK Configuration", order = 1)]
    public class FlockConfigAsset : ScriptableObject
    {
        [Header("Required Settings")]
        [Tooltip("API endpoint URL")]
        public string apiUrl = "https://api-flock.qwacks.com";

        [Tooltip("Your Flock API Key")]
        public string apiKey;

        [Tooltip("Your Game ID")]
        public string gameId;

        [Tooltip("Your Game Version name. The matching version ID is fetched from the backend on SDK init.")]
        [FormerlySerializedAs("gameVersionId")]
        public string gameVersion;

        [Header("Codegen")]
        [Tooltip("Project-relative folder where 'Flock > Sync Schemas' writes generated .cs files (and 'Clean Generated' wipes). Must start with 'Assets/'. Created automatically if missing. Treat this folder as Flock-owned — files in it will be deleted on regen/clean.")]
        public string generatedCodePath = "Assets/Flock/Generated";

        [Header("Optional Settings")]
        [Tooltip("Enable detailed debug logging")]
        public bool enableDebugLogs = false;

        [Header("Analytics")]
        [Tooltip("Enable analytics tracking")]
        public bool analyticsEnabled = true;

        [Tooltip("Automatically start a session on SDK init")]
        public bool analyticsAutoStartSession = true;

        [Tooltip("Automatically end session on app quit")]
        public bool analyticsAutoEndOnQuit = true;

        [Tooltip("Background duration (seconds) before a new session starts")]
        public float analyticsSessionTimeout = 30f;

        [Tooltip("Heartbeat interval in seconds (0 to disable)")]
        public float analyticsHeartbeatInterval = 60f;

        [Tooltip("Sessions shorter than this are marked as bounces")]
        public float analyticsBounceThreshold = 10f;

        [Tooltip("Persist session to disk for crash recovery")]
        public bool analyticsPersistSession = true;

        [Tooltip("Track FPS metrics")]
        public bool analyticsTrackFps = true;

        [Tooltip("FPS sample interval in seconds")]
        public float analyticsFpsSampleInterval = 1f;

        [Header("Analytics — Caching")]
        [Tooltip("Cache failed analytics events (including log_event) on disk and retry on the next session.")]
        public bool analyticsCacheFailedEvents = true;

        [Tooltip("Maximum number of failed events kept on disk. Oldest entries are dropped when the cap is hit.")]
        public int analyticsMaxCachedEvents = 1000;

        [Tooltip("How many cached events are flushed per batch when retrying.")]
        public int analyticsCacheFlushBatchSize = 50;

        [Tooltip("Interval (seconds) for the periodic event-buffer flush. The buffer is also drained on session pause, session end, and online-event triggers. Set to 0 to disable interval-based flushing.")]
        public float analyticsEventBufferFlushInterval = 10f;

        [Header("Asset Cache")]
        [Tooltip("Cache asset downloads on disk, keyed by asset ID + UpdatedAt. Disable on WebGL — persistentDataPath there does not support synchronous writes.")]
        public bool enableAssetCache = true;

        [Tooltip("Absolute path for the asset cache. Leave empty to default to Application.persistentDataPath/flock_assets.")]
        public string assetCacheDirectory = "";

        [Tooltip("Maximum size of the on-disk asset cache, in MB. 0 means unlimited; LRU eviction otherwise.")]
        public int assetCacheMaxSizeMB = 100;

        [Header("HTTP Retry Policy")]
        [Tooltip("How many times to retry after the initial attempt fails. 0 disables retries. Delay between retries is exponential backoff with ±25% jitter — change the defaults via code (new RetryPolicy { ... }) if you need to tune them.")]
        public int retryMaxRetries = 3;

        [Tooltip("Adds ±25% randomness to each retry delay to avoid thundering-herd reconnects after a server outage. Leave on unless you have a specific reason.")]
        public bool retryUseJitter = true;

        public string ApiKey
        {
            get => apiKey;
            set => apiKey = value;
        }

        public FlockInitConfig ToInitConfig()
        {
            FlockAnalyticsConfig analyticsConfig = new FlockAnalyticsConfig
            {
                Enabled = analyticsEnabled,
                AutoStartSession = analyticsAutoStartSession,
                AutoEndSessionOnQuit = analyticsAutoEndOnQuit,
                SessionTimeoutSeconds = analyticsSessionTimeout,
                HeartbeatIntervalSeconds = analyticsHeartbeatInterval,
                BounceThresholdSeconds = analyticsBounceThreshold,
                PersistSessionOnDisk = analyticsPersistSession,
                TrackFps = analyticsTrackFps,
                FpsSampleIntervalSeconds = analyticsFpsSampleInterval,
                CacheFailedEvents = analyticsCacheFailedEvents,
                MaxCachedEvents = analyticsMaxCachedEvents,
                CacheFlushBatchSize = analyticsCacheFlushBatchSize,
                EventBufferFlushIntervalSeconds = analyticsEventBufferFlushInterval,
            };

            RetryPolicy retryPolicy = new RetryPolicy
            {
                MaxRetries = retryMaxRetries,
                UseJitter = retryUseJitter,
            };

            return new FlockInitConfig(apiUrl, apiKey, gameId, gameVersion, enableDebugLogs,
                analyticsConfig: analyticsConfig,
                retryPolicy: retryPolicy)
            {
                EnableAssetCache = enableAssetCache,
                AssetCacheDirectory = assetCacheDirectory,
                AssetCacheMaxSizeMB = assetCacheMaxSizeMB,
            };
        }

#if UNITY_EDITOR && !FLOCK_NO_SCHEMA
        [System.NonSerialized] private string _lastSeenGameVersion;
        [System.NonSerialized] private bool _versionTracked;

        private void OnValidate()
        {
            if (!_versionTracked)
            {
                _lastSeenGameVersion = gameVersion;
                _versionTracked = true;
                return;
            }

            if (!string.Equals(_lastSeenGameVersion, gameVersion, System.StringComparison.Ordinal))
            {
                Debug.LogWarning(
                    $"[Flock Config] gameVersion changed ('{_lastSeenGameVersion}' → '{gameVersion}'). " +
                    "Run 'Flock > Sync Schemas' to regenerate.");
                _lastSeenGameVersion = gameVersion;
            }
        }
#endif

        public bool IsValid(out string errorMessage)
        {
            if (string.IsNullOrEmpty(apiUrl))
            {
                errorMessage = "API URL is required";
                return false;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                errorMessage = "API Key is required";
                return false;
            }

            if (string.IsNullOrEmpty(gameId))
            {
                errorMessage = "Game ID is required";
                return false;
            }

            if (string.IsNullOrEmpty(gameVersion))
            {
                errorMessage = "Game Version is required";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
    }
}
