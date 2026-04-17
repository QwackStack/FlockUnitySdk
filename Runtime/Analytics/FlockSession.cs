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

        private readonly IFlockLogger _logger;
        private readonly FlockAnalyticsConfig _config;
        private readonly FlockBehaviour _behaviour;

        private bool _active;
        private float _totalPauseDuration;
        private int _pauseCount;
        private float _lastHeartbeatTime;

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

        internal string SessionId { get; private set; }
        internal string ServerSessionId { get; set; }
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
        internal event Action<FlockSessionSnapshot> OnSessionTimedOut;
        internal event Action<FlockSessionSnapshot> OnSessionEnding;

        internal FlockSession(FlockAnalyticsConfig config, IFlockLogger logger)
        {
            _config = config;
            _logger = logger;
            _behaviour = FlockBehaviour.Instance;
        }

        internal FlockSessionSnapshot RecoverCrashedSession()
        {
            if (!_config.PersistSessionOnDisk)
                return null;

            string json = PlayerPrefs.GetString(PrefKeySessionData, null);
            if (string.IsNullOrEmpty(json))
                return null;

            PlayerPrefs.DeleteKey(PrefKeySessionData);
            PlayerPrefs.Save();

            try
            {
                FlockSessionSnapshot recovered = JsonConvert.DeserializeObject<FlockSessionSnapshot>(json);
                if (recovered != null && recovered.IsActive)
                {
                    recovered.IsActive = false;
                    recovered.WasCrash = true;
                    recovered.EndTimeUtc = recovered.LastHeartbeatUtc ?? recovered.StartTimeUtc;
                    _logger.LogWarning($"Recovered crashed session: {recovered.SessionId}");
                    return recovered;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to recover crashed session: {ex.Message}");
            }

            return null;
        }

        internal string Start()
        {
            if (_active)
            {
                _logger.LogWarning("Session already active, ending previous session before starting new one");
                End();
            }

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
            PersistState();

            _logger.LogInfo($"Session started: {SessionId} (#{SessionNumber})");

            return SessionId;
        }

        internal FlockSessionSnapshot End()
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

            ClearPersistedState();

            _logger.LogInfo($"Session ended: {SessionId} | Duration: {snapshot.DurationSeconds:F1}s | Screens: {snapshot.ScreensViewed} | Pauses: {snapshot.PauseCount} | AvgFPS: {snapshot.AverageFps:F0}{(snapshot.IsBounce ? " [BOUNCE]" : "")}");

            return snapshot;
        }

        internal void Reset()
        {
            if (_active)
                End();

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;

            Unsubscribe();
            OnHeartbeat = null;
            OnSessionTimedOut = null;
            OnSessionEnding = null;
        }

        internal void RecordScreenView(string screenName)
        {
            if (!_active)
                return;

            _screensViewed++;
            if (!string.IsNullOrEmpty(screenName))
                _screenNames.Add(screenName);
        }

        internal FlockSessionSnapshot TakeSnapshot()
        {
            return new FlockSessionSnapshot
            {
                SessionId = SessionId,
                ServerSessionId = ServerSessionId,
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
                WasCrash = false,
                IsFirstSession = SessionNumber == 1
            };
        }

        private void Subscribe()
        {
            _behaviour.OnTick += HandleTick;
            _behaviour.OnPause += HandlePause;
            _behaviour.OnQuit += HandleQuit;
        }

        private void Unsubscribe()
        {
            if (!FlockBehaviour.IsAvailable)
                return;

            _behaviour.OnTick -= HandleTick;
            _behaviour.OnPause -= HandlePause;
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
                    PersistState();
                    OnHeartbeat?.Invoke();
                }
            }
        }

        private void HandlePause(bool paused)
        {
            if (!_active)
                return;

            if (paused)
            {
                _pauseCount++;
                _isPaused = true;
                _pausedAtRealtime = Time.realtimeSinceStartup;
                PersistState();
                _logger.LogDebug($"Session paused: {SessionId}");
            }
            else
            {
                if (!_isPaused)
                    return;

                float pausedDuration = Time.realtimeSinceStartup - _pausedAtRealtime;

                if (pausedDuration > _config.SessionTimeoutSeconds)
                {
                    _logger.LogInfo($"Session timeout exceeded ({pausedDuration:F0}s > {_config.SessionTimeoutSeconds:F0}s). New session required.");

                    FlockSessionSnapshot snapshot = End();
                    if (snapshot != null)
                        OnSessionTimedOut?.Invoke(snapshot);
                }
                else
                {
                    FinalizePause();
                    _isPaused = false;
                    _logger.LogDebug($"Session resumed: {SessionId}");
                }
            }
        }

        private void HandleQuit()
        {
            if (!_active || !_config.AutoEndSessionOnQuit)
                return;

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;

            FinalizePause();
            EndTimeUtc = DateTime.UtcNow;
            EndRealtimeSinceStartup = Time.realtimeSinceStartup;

            FlockSessionSnapshot snapshot = TakeSnapshot();
            snapshot.IsBounce = snapshot.DurationSeconds < _config.BounceThresholdSeconds;

            OnSessionEnding?.Invoke(snapshot);

            PersistState();

            _active = false;

            _logger.LogInfo($"Session persisted on quit: {SessionId} | Duration: {ElapsedSeconds:F1}s");
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

        private void PersistState()
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
            PlayerPrefs.DeleteKey(PrefKeySessionData);
            PlayerPrefs.Save();
        }
    }
}
