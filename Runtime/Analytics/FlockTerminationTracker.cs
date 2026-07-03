using System;
using Flock.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace Flock.Analytics
{
    /// <summary>Next-launch dirty-exit detection: keeps a tombstone marker alive during the session, classifies a survivor on the following boot.</summary>
    internal class FlockTerminationTracker
    {
        private const string PrefKeyMarker = "flock_termination_marker";
        private const string StateForeground = "foreground";
        private const string StateBackground = "background";

        private const string ClassBackgroundKill = "background_kill";
        private const string ClassAbnormal = "abnormal";
        internal const string EventName = "app_termination";

        private readonly IFlockLogger _logger;
        private readonly bool _enabled;

        private FlockBehaviour _behaviour;
        private FlockTerminationMarker _marker;
        private int _pendingExceptionCount;
        private bool _tracking;

        // enabled is computed by the owner (config + platform guards) so this class stays testable in EditMode.
        internal FlockTerminationTracker(IFlockLogger logger, bool enabled)
        {
            _logger = logger;
            _enabled = enabled;
        }

        // Lifecycle-only verdict: died backgrounded = OS eviction/swipe-close; anything else = foreground death.
        internal static string Classify(FlockTerminationMarker marker)
        {
            if (marker == null)
                return null;
            return marker.LastState == StateBackground ? ClassBackgroundKill : ClassAbnormal;
        }

        internal void BeginTracking(string sessionId)
        {
            if (!_enabled)
                return;

            _marker = new FlockTerminationMarker
            {
                SessionId = sessionId,
                LastState = StateForeground,
                LastAliveUtc = DateTime.UtcNow,
                ExceptionCount = 0
            };
            _pendingExceptionCount = 0;
            _tracking = true;

            Subscribe();
            SaveMarker();
        }

        internal void StopTracking()
        {
            if (!_tracking)
                return;

            Unsubscribe();
            _tracking = false;
            _marker = null;
            _pendingExceptionCount = 0;
            ClearMarker();
        }

        // Piggybacks the session heartbeat: refreshes death-time estimate and folds in pending exceptions.
        internal void HandleHeartbeat()
        {
            if (!_tracking || _marker == null)
                return;

            _marker.LastAliveUtc = DateTime.UtcNow;
            FoldPendingExceptions();
            SaveMarker();
        }

        // Pause is often the last managed code before a mobile death — persist immediately.
        internal void HandleAppBackgrounded(bool isBackgrounded)
        {
            if (!_tracking || _marker == null)
                return;

            _marker.LastState = isBackgrounded ? StateBackground : StateForeground;
            _marker.LastAliveUtc = DateTime.UtcNow;
            FoldPendingExceptions();
            SaveMarker();
        }

        // In-memory only; persisted on the next heartbeat/pause so exception loops can't hammer disk.
        internal void HandleException(string message, string stackTrace)
        {
            if (_tracking)
                _pendingExceptionCount++;
        }

        internal FlockTerminationMarker ReadSurvivingMarker()
        {
            if (!_enabled)
                return null;

            string json = PlayerPrefs.GetString(PrefKeyMarker, null);
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                FlockTerminationMarker marker = JsonConvert.DeserializeObject<FlockTerminationMarker>(json);
                if (marker != null && !string.IsNullOrEmpty(marker.SessionId))
                    return marker;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Discarding malformed termination marker: {ex.Message}");
            }

            // Corrupt or incomplete — clear so it can't poison future launches.
            ClearMarker();
            return null;
        }

        // Not gated on _enabled: the provider must be able to drop undeliverable markers.
        internal void ClearMarker()
        {
            try
            {
                PlayerPrefs.DeleteKey(PrefKeyMarker);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to clear termination marker: {ex.Message}");
            }
        }

        private void FoldPendingExceptions()
        {
            if (_pendingExceptionCount > 0)
            {
                _marker.ExceptionCount += _pendingExceptionCount;
                _pendingExceptionCount = 0;
            }
        }

        private void Subscribe()
        {
            // -= first so a restart can't double-subscribe.
            Unsubscribe();

            _behaviour = FlockBehaviour.Instance;
            if (_behaviour == null)
            {
                _logger.LogWarning("FlockBehaviour unavailable; termination tracking will miss pause/exception signals");
                return;
            }

            _behaviour.OnAppBackgrounded += HandleAppBackgrounded;
            _behaviour.OnException += HandleException;
        }

        private void Unsubscribe()
        {
            // Field, not Instance — Instance is null during quit, exactly when detaching must still work.
            if (_behaviour == null)
                return;

            _behaviour.OnAppBackgrounded -= HandleAppBackgrounded;
            _behaviour.OnException -= HandleException;
        }

        private void SaveMarker()
        {
            try
            {
                PlayerPrefs.SetString(PrefKeyMarker, JsonConvert.SerializeObject(_marker));
                // Explicit Save is load-bearing: a crash loses anything not flushed to disk.
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to persist termination marker: {ex.Message}");
            }
        }
    }
}
