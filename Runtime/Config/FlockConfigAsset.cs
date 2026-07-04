using System;
using Flock.Analytics;
using Flock.Http;
using UnityEngine;

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
        public string gameVersion;

        [Header("Resolved (do not edit by hand)")]
        [Tooltip(
            "Game Version ID, resolved from the dashboard at edit time and baked here. " +
            "Runtime init uses this directly and never contacts the server. Resolved " +
            "automatically in Flock > Settings when you change your credentials or version.")]
        public string gameVersionId;

        [Header("Codegen")]
        [Tooltip("Project-relative folder where the Codegen tab's Sync Schemas writes generated .cs files (and Delete Generated Code wipes). Must start with 'Assets/'. Created automatically if missing. Treat this folder as Flock-owned — files in it will be deleted on regen/clean.")]
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

        [Tooltip("Require the game to explicitly call Analytics.SetConsent(true) before any analytics collection starts (no session, no events, no device/FPS/screen-view capture). When OFF (default), analytics behaves as it does today - collecting once authenticated - until the game calls SetConsent(false). Turn ON for a real GDPR-style opt-in flow.")]
        public bool analyticsRequireExplicitConsent = false;

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

        [Tooltip("Per-download timeout (seconds) for asset downloads. 0 = no timeout (default) so large assets aren't aborted mid-transfer; set a value to bound hung downloads. Like Unity Addressables' Timeout.")]
        public int assetDownloadTimeoutSeconds = 0;

        [Tooltip("Retry attempts for a failed asset download, independent of the HTTP Retry Policy used by API calls (like Unity Addressables' Retry Count). Default 3; 0 disables. Only transient failures retry — permanent 4xx (e.g. an expired URL) fail fast.")]
        public int assetDownloadRetryCount = 3;

        [Header("Offline Cache")]
        [Tooltip("Snapshot read-API responses to disk and serve them when the network is unavailable. Disable on WebGL — persistentDataPath there does not support synchronous writes.")]
        public bool enableOfflineCache = true;

        [Tooltip("Absolute path for snapshot storage. Leave empty to default to Application.persistentDataPath/Flock/snapshots.")]
        public string offlineCacheDirectory = "";

        [Header("HTTP Retry Policy")]
        [Tooltip("How many times to retry after the initial attempt fails. 0 disables retries. Delay between retries is exponential backoff with ±25% jitter — change the defaults via code (new RetryPolicy { ... }) if you need to tune them.")]
        public int retryMaxRetries = 3;

        [Tooltip("Adds ±25% randomness to each retry delay to avoid thundering-herd reconnects after a server outage. Leave on unless you have a specific reason.")]
        public bool retryUseJitter = true;

        [Tooltip("Per-request timeout in seconds for SDK HTTP calls. Caps how long one attempt can hang before failing (and being retried). Default 30. Asset downloads use UnityWebRequest and are unaffected.")]
        public float httpTimeoutSeconds = 30f;

        [Header("Initialization")]
        [Tooltip(
            "When ON, the SDK initializes itself automatically at startup (before the first scene " +
            "loads) from this asset — no FlockBootstrap component or Create() call needed — and " +
            "restores a persisted session in the background. Leave OFF if you use FlockBootstrap or " +
            "call FlockClient.Create yourself (e.g. after a splash screen or EULA).")]
        public bool autoInitializeOnLoad = true;

        [Header("Editor")]
        [Tooltip(
            "When ON, entering Play with Flock not set up (no/invalid FlockConfig, or a " +
            "FlockBootstrap whose config is missing/invalid) shows a fixable dialog instead " +
            "of failing at runtime. Editor-only; no effect in builds. Stored on the asset so " +
            "the team shares the setting — gitignore the asset if you don't want it shared.")]
        public bool playModeGuardEnabled = true;

        [Tooltip(
            "Fail the player build if the Game Version ID above has not been resolved, so a " +
            "build that cannot initialize the SDK can't ship. Editor-only; no effect at runtime.")]
        public bool failBuildIfVersionUnresolved = true;

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
                RequireExplicitConsent = analyticsRequireExplicitConsent,
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
                GameVersionId = gameVersionId,
                EnableAssetCache = enableAssetCache,
                AssetCacheDirectory = assetCacheDirectory,
                AssetCacheMaxSizeMB = assetCacheMaxSizeMB,
                AssetDownloadTimeout = TimeSpan.FromSeconds(assetDownloadTimeoutSeconds),
                AssetDownloadRetryCount = assetDownloadRetryCount,
                EnableOfflineCache = enableOfflineCache,
                OfflineCacheDirectory = offlineCacheDirectory,
                HttpTimeout = TimeSpan.FromSeconds(httpTimeoutSeconds),
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
                    "Re-sync from the Codegen tab in Flock > Settings.");
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
