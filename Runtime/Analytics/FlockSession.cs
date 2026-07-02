using System;
using System.Collections.Generic;
using System.Threading;
using Flock.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace Flock.Analytics
{
    internal class FlockSession
    {
        private const string PrefKeySessionData = "flock_session_active";
        private const string PrefKeySessionNumber = "flock_session_number";
        // The name list is serialized into PlayerPrefs on every heartbeat, so it is capped;
        // ScreensViewed keeps the full count.
        private const int MaxTrackedScreenNames = 100;

        private readonly IFlockLogger _logger;
        private readonly FlockAnalyticsConfig _config;
        private readonly FlockBehaviour _behaviour;

        private bool _active;
        private float _totalPauseDuration;
        private int _pauseCount;
        private float _lastHeartbeatTime;
        private float _lastFlushTime;

        private int _frameCount;
        private float _fpsAccumulator;
        private float _fpsSampleTimer;
        private float _fpsMin;
        private float _fpsMax;
        private float _fpsSum;
        private int _fpsSampleCount;

        private int _screensViewed;
        private readonly List<string> _screenNames = new List<string>();

        private CancellationTokenSource _sessionCts;

        private bool _isPaused;
        private float _pausedAtRealtime;
        private string _playerId;

        internal string SessionId { get; private set; }
        internal string ServerSessionId { get; private set; }
        internal DateTime StartTimeUtc { get; private set; }
        internal DateTime? EndTimeUtc { get; private set; }
        internal float StartRealtimeSinceStartup { get; private set; }
        internal float EndRealtimeSinceStartup { get; private set; }
        internal int SessionNumber { get; private set; }
        internal FlockDeviceInfo DeviceInfo { get; private set; }

        internal bool IsActive => _active;

        internal float ElapsedSeconds
        {
            get
            {
                float raw = _active
                    ? Time.realtimeSinceStartup - StartRealtimeSinceStartup
                    : EndRealtimeSinceStartup - StartRealtimeSinceStartup;

                return raw - FinalizedPauseDuration;
            }
        }

        internal float FinalizedPauseDuration
        {
            get
            {
                if (_isPaused)
                    return _totalPauseDuration + (Time.realtimeSinceStartup - _pausedAtRealtime);
                return _totalPauseDuration;
            }
        }

        internal int PauseCount => _pauseCount;
        internal int ScreensViewed => _screensViewed;

        internal CancellationToken SessionToken => _sessionCts?.Token ?? CancellationToken.None;

        internal float AverageFps => _fpsSampleCount > 0 ? _fpsSum / _fpsSampleCount : 0f;
        internal float MinFps => _fpsSampleCount > 0 ? _fpsMin : 0f;
        internal float MaxFps => _fpsSampleCount > 0 ? _fpsMax : 0f;

        internal event Action OnHeartbeat;
        internal event Action OnFlushInterval;
        internal event Action OnSessionPaused;
        internal event Action<FlockSessionSnapshot> OnSessionTimedOut;
        // Fired synchronously from inside End() for every end path (including logout via
        // Reset); handlers must persist the snapshot before returning.
        internal event Action<FlockSessionSnapshot> OnSessionEnded;
        // Quit-only: requests a best-effort delivery attempt before the process dies.
        internal event Action<FlockSessionSnapshot> OnQuitFlush;

        internal FlockSession(FlockAnalyticsConfig config, IFlockLogger logger)
        {
            _config = config;
            _logger = logger;
            _behaviour = FlockBehaviour.Instance;
        }

        internal FlockSessionSnapshot RecoverOrphanedSession()
        {
            if (!_config.PersistSessionOnDisk)
                return null;

            string json = PlayerPrefs.GetString(PrefKeySessionData, null);
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                FlockSessionSnapshot recovered = JsonConvert.DeserializeObject<FlockSessionSnapshot>(json);
                if (recovered != null && recovered.IsActive)
                {
                    // Not cleared here: the caller spools the end durably, then clears.
                    recovered.IsActive = false;
                    recovered.EndTimeUtc = recovered.LastHeartbeatUtc ?? recovered.StartTimeUtc;
                    _logger.LogWarning($"Recovering orphaned session: {recovered.SessionId}");
                    return recovered;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to recover orphaned session: {ex.Message}");
            }

            // Corrupt or non-active payload — clear it so it can't poison future launches.
            ClearPersistedState();
            return null;
        }

        internal string Start(string playerId)
        {
            if (_active)
            {
                // The end is spooled via OnSessionEnded; callers should still End() and
                // deliver explicitly so the send is awaited.
                _logger.LogWarning("Session already active, ending previous session before starting new one");
                End(FlockSessionEndReason.Restarted);
            }

            _playerId = playerId;
            SessionId = Guid.NewGuid().ToString();
            ServerSessionId = null;
            StartTimeUtc = DateTime.UtcNow;
            EndTimeUtc = null;
            StartRealtimeSinceStartup = Time.realtimeSinceStartup;
            EndRealtimeSinceStartup = 0f;

            _active = true;
            _isPaused = false;
            _pausedAtRealtime = 0f;
            _totalPauseDuration = 0f;
            _pauseCount = 0;
            _lastHeartbeatTime = Time.realtimeSinceStartup;
            _lastFlushTime = Time.realtimeSinceStartup;

            _frameCount = 0;
            _fpsAccumulator = 0f;
            _fpsSampleTimer = 0f;
            _fpsMin = float.MaxValue;
            _fpsMax = 0f;
            _fpsSum = 0f;
            _fpsSampleCount = 0;

            _screensViewed = 0;
            _screenNames.Clear();

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();

            SessionNumber = PlayerPrefs.GetInt(PrefKeySessionNumber, 0) + 1;
            PlayerPrefs.SetInt(PrefKeySessionNumber, SessionNumber);
            PlayerPrefs.Save();

            DeviceInfo = FlockDeviceInfo.Capture();

            Subscribe();
            SaveState();

            _logger.LogInfo($"Session started: {SessionId} (#{SessionNumber})");

            FlockEvents.InvokeSessionStarted(SessionId);

            return SessionId;
        }

        internal void SetServerSessionId(string serverSessionId)
        {
            ServerSessionId = serverSessionId;
            // Persist immediately so a crash can't strand a registered session without its id.
            SaveState();
        }

        internal FlockSessionSnapshot End(FlockSessionEndReason reason)
        {
            if (!_active)
            {
                _logger.LogWarning("No active session to end");
                return null;
            }

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;

            FinalizePause();

            _active = false;
            EndTimeUtc = DateTime.UtcNow;
            EndRealtimeSinceStartup = Time.realtimeSinceStartup;

            Unsubscribe();

            FlockSessionSnapshot snapshot = TakeSnapshot();
            snapshot.IsBounce = snapshot.DurationSeconds < _config.BounceThresholdSeconds;

            // Spool-before-clear: the handler persists the end durably; only then is the
            // live marker safe to drop.
            OnSessionEnded?.Invoke(snapshot);

            ClearPersistedState();

            _logger.LogInfo($"Session ended: {SessionId} | Duration: {snapshot.DurationSeconds:F1}s | Screens: {snapshot.ScreensViewed} | Pauses: {snapshot.PauseCount} | AvgFPS: {snapshot.AverageFps:F0}{(snapshot.IsBounce ? " [BOUNCE]" : "")}");

            FlockEvents.InvokeSessionEnded(new FlockSessionEndedArgs(snapshot, reason));

            return snapshot;
        }

        // Consent-revoke path: stops the session locally without spooling/sending a final
        // record — unlike End(), this does not invoke OnSessionEnded.
        internal void Discard()
        {
            if (!_active)
                return;

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;

            FinalizePause();

            _active = false;
            EndTimeUtc = DateTime.UtcNow;
            EndRealtimeSinceStartup = Time.realtimeSinceStartup;

            Unsubscribe();
            ClearPersistedState();

            _logger.LogInfo($"Session discarded (consent revoked): {SessionId}");
        }

        internal void Reset(FlockSessionEndReason reason)
        {
            if (_active)
                End(reason);

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;

            Unsubscribe();
            OnHeartbeat = null;
            OnFlushInterval = null;
            OnSessionPaused = null;
            OnSessionTimedOut = null;
            OnSessionEnded = null;
            OnQuitFlush = null;
        }

        internal void RecordScreenView(string screenName)
        {
            if (!_active)
                return;

            _screensViewed++;
            if (!string.IsNullOrEmpty(screenName) && _screenNames.Count < MaxTrackedScreenNames)
            {
                _screenNames.Add(screenName);
                if (_screenNames.Count == MaxTrackedScreenNames)
                    _logger.LogDebug($"Screen name list reached cap ({MaxTrackedScreenNames}); further names are dropped, ScreensViewed keeps counting");
            }
        }

        internal FlockSessionSnapshot TakeSnapshot()
        {
            return new FlockSessionSnapshot
            {
                SessionId = SessionId,
                ServerSessionId = ServerSessionId,
                PlayerId = _playerId,
                SessionNumber = SessionNumber,
                StartTimeUtc = StartTimeUtc,
                EndTimeUtc = EndTimeUtc,
                LastHeartbeatUtc = DateTime.UtcNow,
                DurationSeconds = ElapsedSeconds,
                TotalPauseDurationSeconds = FinalizedPauseDuration,
                PauseCount = _pauseCount,
                ScreensViewed = _screensViewed,
                ScreenNames = new List<string>(_screenNames),
                AverageFps = AverageFps,
                MinFps = MinFps,
                MaxFps = MaxFps,
                DeviceInfo = DeviceInfo,
                IsActive = _active,
                IsBounce = false,
                IsFirstSession = SessionNumber == 1
            };
        }

        private void Subscribe()
        {
            if (_behaviour == null)
            {
                _logger.LogWarning("FlockBehaviour unavailable; session lifecycle events (tick/pause/quit) will not fire");
                return;
            }

            _behaviour.OnTick += HandleTick;
            _behaviour.OnAppBackgrounded += HandleAppBackgrounded;
            _behaviour.OnQuit += HandleQuit;
        }

        private void Unsubscribe()
        {
            // Not IsAvailable — that is false during quit, exactly when detaching must still work.
            if (_behaviour == null)
                return;

            _behaviour.OnTick -= HandleTick;
            _behaviour.OnAppBackgrounded -= HandleAppBackgrounded;
            _behaviour.OnQuit -= HandleQuit;
        }



        private void HandleTick()
        {
            if (!_active || _isPaused)
                return;

            float dt = Time.unscaledDeltaTime;

            if (_config.TrackFps)
            {
                _frameCount++;
                _fpsAccumulator += dt;
                _fpsSampleTimer += dt;

                if (_fpsSampleTimer >= _config.FpsSampleIntervalSeconds && _fpsAccumulator > 0f)
                {
                    float currentFps = _frameCount / _fpsAccumulator;
                    _fpsSum += currentFps;
                    _fpsSampleCount++;

                    if (currentFps < _fpsMin) _fpsMin = currentFps;
                    if (currentFps > _fpsMax) _fpsMax = currentFps;

                    _frameCount = 0;
                    _fpsAccumulator = 0f;
                    _fpsSampleTimer = 0f;
                }
            }

            if (_config.HeartbeatIntervalSeconds > 0f)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastHeartbeatTime >= _config.HeartbeatIntervalSeconds)
                {
                    _lastHeartbeatTime = now;
                    SaveState();
                    OnHeartbeat?.Invoke();
                }
            }

            if (_config.EventBufferFlushIntervalSeconds > 0f)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastFlushTime >= _config.EventBufferFlushIntervalSeconds)
                {
                    _lastFlushTime = now;
                    OnFlushInterval?.Invoke();
                }
            }
        }

        private void HandleAppBackgrounded(bool isBackgrounded)
        {
            if (!_active)
                return;

            if (isBackgrounded)
            {
                _pauseCount++;
                _isPaused = true;
                _pausedAtRealtime = Time.realtimeSinceStartup;
                SaveState();
                OnSessionPaused?.Invoke();
                FlockEvents.InvokeSessionPaused();
                _logger.LogDebug($"Session backgrounded: {SessionId}");
            }
            else
            {
                if (!_isPaused)
                    return;

                float pausedDuration = Time.realtimeSinceStartup - _pausedAtRealtime;

                if (pausedDuration > _config.SessionTimeoutSeconds)
                {
                    _logger.LogInfo($"Session timeout exceeded ({pausedDuration:F0}s > {_config.SessionTimeoutSeconds:F0}s). New session required.");

                    FlockSessionSnapshot snapshot = End(FlockSessionEndReason.Timeout);
                    if (snapshot != null)
                        OnSessionTimedOut?.Invoke(snapshot);
                }
                else
                {
                    FinalizePause();
                    _isPaused = false;
                    _logger.LogDebug($"Session resumed: {SessionId}");
                    FlockEvents.InvokeSessionResumed();
                }
            }
        }

        private void HandleQuit()
        {
            if (!_active || !_config.AutoEndSessionOnQuit)
                return;

            // End() spools the snapshot (OnSessionEnded) and clears the live marker; the
            // quit flush is just the last-chance network attempt.
            FlockSessionSnapshot snapshot = End(FlockSessionEndReason.Quit);
            if (snapshot == null)
                return;

            OnQuitFlush?.Invoke(snapshot);

            _logger.LogInfo($"Session ending on quit: {SessionId} | Duration: {snapshot.DurationSeconds:F1}s");
        }

     
        private void FinalizePause()
        {
            if (_isPaused)
            {
                _totalPauseDuration += Time.realtimeSinceStartup - _pausedAtRealtime;
                _isPaused = false;
                _pausedAtRealtime = 0f;
            }
        }

        private void SaveState()
        {
            if (!_config.PersistSessionOnDisk || !_active)
                return;

            try
            {
                FlockSessionSnapshot snapshot = TakeSnapshot();
                string json = JsonConvert.SerializeObject(snapshot);
                PlayerPrefs.SetString(PrefKeySessionData, json);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to persist session state: {ex.Message}");
            }
        }

        internal void ClearPersistedState()
        {
            try
            {
                PlayerPrefs.DeleteKey(PrefKeySessionData);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                // PlayerPrefs throws off the main thread; a surviving marker is re-delivered next launch.
                _logger.LogWarning($"Failed to clear persisted session state: {ex.Message}");
            }
        }
    }
}
