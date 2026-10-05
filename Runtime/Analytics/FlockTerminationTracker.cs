using System;
using System.IO;
using Flock.Logging;
using Newtonsoft.Json;

namespace Flock.Analytics
{
    /// <summary>Next-launch dirty-exit detection: a marker in this launch's folder from launch start to a clean quit, classified when a later launch takes the folder over.</summary>
    internal class FlockTerminationTracker
    {
        private const string StateForeground = "foreground";
        private const string StateBackground = "background";

        private const string ClassBackgroundKill = "background_kill";
        private const string ClassAbnormal = "abnormal";
        internal const string EventName = "app_termination";

        private readonly IFlockLogger _logger;
        private readonly bool _enabled;
        private readonly string _markerPath;
        private readonly double _heartbeatIntervalSeconds;

        private FlockBehaviour _behaviour;
        private FlockTerminationMarker _marker;
        private int _pendingExceptionCount;
        private bool _tracking;
        private double _nextHeartbeatAt;

        // enabled is computed by the owner (config + platform guards) so this class stays testable in EditMode.
        internal FlockTerminationTracker(IFlockLogger logger, bool enabled, string markerPath, float heartbeatIntervalSeconds = 60f)
        {
            _logger = logger;
            _enabled = enabled;
            _markerPath = markerPath;
            _heartbeatIntervalSeconds = heartbeatIntervalSeconds;
        }

        internal bool IsTracking => _tracking;

        // Lifecycle-only verdict: died backgrounded = OS eviction/swipe-close; anything else = foreground death.
        internal static string Classify(FlockTerminationMarker marker)
        {
            if (marker == null)
                return null;
            return marker.LastState == StateBackground ? ClassBackgroundKill : ClassAbnormal;
        }

        /// <summary>Starts the launch's marker, with no session yet, so a crash before sign-in is reported too. Again while tracking does nothing.</summary>
        internal void BeginTracking()
        {
            if (!_enabled || _tracking)
                return;

            _marker = new FlockTerminationMarker
            {
                SessionId = null,
                LastState = StateForeground,
                LastAliveUtc = DateTime.UtcNow,
                ExceptionCount = 0
            };
            _pendingExceptionCount = 0;
            _tracking = true;
            _nextHeartbeatAt = 0.0;

            Subscribe();
            SaveMarker();
        }

        /// <summary>The marker names the session now running, so the next launch's report says which one ended badly.</summary>
        internal void NoteSessionStarted(string sessionId)
        {
            if (!_tracking)
                return;
            _marker.SessionId = sessionId;
            FoldPendingExceptions();
            SaveMarker();
        }

        /// <summary>A session ended cleanly; the launch, and its marker, go on without one.</summary>
        internal void NoteSessionEnded()
        {
            if (!_tracking)
                return;
            _marker.SessionId = null;
            FoldPendingExceptions();
            SaveMarker();
        }

        /// <summary>Every frame: the marker's last-alive time is refreshed once per heartbeat interval, session or not.</summary>
        internal void HandleTick(double nowSeconds)
        {
            if (!_tracking || _heartbeatIntervalSeconds <= 0.0 || nowSeconds < _nextHeartbeatAt)
                return;
            _nextHeartbeatAt = nowSeconds + _heartbeatIntervalSeconds;
            HandleHeartbeat();
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

        // Refreshes the death-time estimate and folds in pending exceptions.
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

        // Every captured exception, repeats included; in memory only, persisted on the next heartbeat or pause so a loop can't hammer disk.
        internal void NoteException() => NoteExceptions(1);

        /// <summary>Several at once: exceptions heard but lost to a full queue count too.</summary>
        internal void NoteExceptions(int count)
        {
            if (_tracking && count > 0)
                _pendingExceptionCount += count;
        }

        // Whatever this launch's own switch says: the marker belongs to a launch that has ended.
        internal static FlockTerminationMarker ReadMarker(string markerPath, IFlockLogger logger)
        {
            string json;
            try
            {
                if (!File.Exists(markerPath))
                    return null;
                json = File.ReadAllText(markerPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Could not read termination marker: {ex.Message}");
                return null;
            }

            try
            {
                // No session id is a launch that ended before any sign-in; no last-alive time is a marker cut off while written.
                FlockTerminationMarker marker = JsonConvert.DeserializeObject<FlockTerminationMarker>(json);
                if (marker != null && marker.LastAliveUtc != default(DateTime))
                    return marker;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Discarding malformed termination marker: {ex.Message}");
            }

            // Corrupt or incomplete — delete so it can't poison future launches.
            DeleteMarker(markerPath, logger);
            return null;
        }

        internal static void DeleteMarker(string markerPath, IFlockLogger logger)
        {
            try
            {
                if (File.Exists(markerPath))
                    FlockSavedFiles.Delete(markerPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Failed to clear termination marker: {ex.Message}");
            }
        }

        internal void ClearMarker() => DeleteMarker(_markerPath, _logger);

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
                _logger.LogWarning("FlockBehaviour unavailable; termination tracking will miss pause signals");
                return;
            }

            _behaviour.OnAppBackgrounded += HandleAppBackgrounded;
        }

        private void Unsubscribe()
        {
            // Field, not Instance — Instance is null during quit, exactly when detaching must still work.
            if (_behaviour == null)
                return;

            _behaviour.OnAppBackgrounded -= HandleAppBackgrounded;
        }

        private void SaveMarker()
        {
            try
            {
                FlockTemporaryFiles.Save(_markerPath, JsonConvert.SerializeObject(_marker));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to persist termination marker: {ex.Message}");
            }
        }
    }
}
