using Flock.Analytics;
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
        [SerializeField, HideInInspector]
        private string apiKey;

        [Tooltip("Your Game ID")]
        public string gameId;

        [Tooltip("Your Game Version ID")]
        public string gameVersionId;

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

        public string ApiKey
        {
            get => apiKey;
            set => apiKey = value;
        }

        public FlockInitConfig ToInitConfig()
        {
            return new FlockInitConfig(apiUrl, apiKey, gameId, gameVersionId, enableDebugLogs,
                analyticsConfig: new FlockAnalyticsConfig
                {
                    Enabled = analyticsEnabled,
                    AutoStartSession = analyticsAutoStartSession,
                    AutoEndSessionOnQuit = analyticsAutoEndOnQuit,
                    SessionTimeoutSeconds = analyticsSessionTimeout,
                    HeartbeatIntervalSeconds = analyticsHeartbeatInterval,
                    BounceThresholdSeconds = analyticsBounceThreshold,
                    PersistSessionOnDisk = analyticsPersistSession,
                    TrackFps = analyticsTrackFps,
                    FpsSampleIntervalSeconds = analyticsFpsSampleInterval
                });
        }

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

            if (string.IsNullOrEmpty(gameVersionId))
            {
                errorMessage = "Game Version ID is required";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
    }
}
