using System;
using System.Collections.Generic;
using Flock;
using Flock.Exceptions;
using Flock.Providers;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Protokite.Playtest
{
    public static partial class ProtokitePlaytest
    {
        /// <summary>The hitch threshold where the project has no playtest settings.</summary>
        private const double DefaultHitchFrameTimeMs = 60.0;

        /// <summary>The least hitch threshold the settings can hold.</summary>
        private const double MinimumHitchFrameTimeMs = 1.0;

        private static ProtokitePlaytestPerformanceTimeline _performanceTimeline;
        private static ProfilerRecorder _memoryRecorder;
        private static readonly List<SceneLoad> _sceneLoadsWaitingForTheirFrame = new List<SceneLoad>();
        private static string _currentSceneName;
        private static int _activeSceneHandle;
        private static bool _warnedHeavyAnalyticsWithoutFlockAnalytics;

        // Written on the main thread each frame; read by RecordPlaytestEvent from any thread.
        private static volatile bool _measuringPerformance;

        /// <summary>Stands in for the process's memory use, in bytes, so tests choose it; null reads as not reported.</summary>
        internal static Func<long?> MemoryUsedBytesForTesting;

        /// <summary>Whether heavy analytics is measuring right now.</summary>
        internal static bool IsMeasuringPerformance => _measuringPerformance;

        /// <summary>The frame times the window in progress has counted, for tests; null while not measuring.</summary>
        internal static IReadOnlyList<double> PerformanceFrameTimesMsForTesting => _measuringPerformance ? _performanceTimeline.FrameTimesMsSoFarForTesting : null;

        // A scene load waits for the next Update, whose frame time carries it and is left out.
        private struct SceneLoad
        {
            public string SceneName;
            public string PreviousSceneName;
            public bool ReplacedTheScene;
        }

        /// <summary>Records one of the game's own events with the playtest's, while heavy analytics runs; true when it was queued. Any thread.</summary>
        public static bool RecordPlaytestEvent(string eventName, Dictionary<string, object> properties = null)
        {
            // One sender per name, so a chart built on the playtest's own events never counts one of the game's.
            if (string.Equals(eventName, ProtokitePlaytestEvents.PerformanceWindow, StringComparison.Ordinal)
                || string.Equals(eventName, ProtokitePlaytestEvents.LevelLoaded, StringComparison.Ordinal))
            {
                Debug.LogWarning(LogPrefix + $"Playtest event '{eventName}' refused: the playtest sends events with that name itself.");
                return false;
            }
            try
            {
                return _measuringPerformance && SendPlaytestEvent(eventName, properties);
            }
            catch (FlockException)
            {
                // The Flock SDK shut down on the main thread between this thread's check and its read of the client.
                return false;
            }
        }

        /// <summary>One frame of play, the real time since the driver's last Update: measured while the playtest and the Flock SDK's analytics allow it.</summary>
        internal static void UpdateHeavyAnalytics(double frameSeconds)
        {
            bool featureOn = HeavyAnalyticsIsOnInTheLoadedConfig();
            bool flockAnalyticsOn = featureOn && !(RunningFlock()?.Analytics is NullAnalyticsProvider);
            if (featureOn && !flockAnalyticsOn && !_warnedHeavyAnalyticsWithoutFlockAnalytics)
            {
                _warnedHeavyAnalyticsWithoutFlockAnalytics = true;
                Debug.LogWarning(LogPrefix + "This playtest turns heavy analytics on, but the Flock SDK's analytics is off, so no performance or playtest event is recorded. Turn on Analytics Enabled in Flock > Settings.");
            }

            bool shouldMeasure = featureOn && flockAnalyticsOn;
            if (shouldMeasure != _measuringPerformance)
            {
                if (shouldMeasure)
                    StartMeasuringPerformance();
                else
                    StopMeasuringPerformance();
            }
            if (!_measuringPerformance)
                return;

            FollowTheActiveScene();
            SendSceneLoadsThisFrameCarries(frameSeconds);
            if (_performanceTimeline.AddFrame(frameSeconds, out ProtokitePlaytestPerformanceWindow window))
                SendPerformanceWindow(window);
        }

        /// <summary>A scene finished loading; the next Update's frame time carries the load and is left out.</summary>
        internal static void NoteSceneLoaded(string sceneName, bool replacedTheScene)
        {
            if (!_measuringPerformance)
                return;
            string previousSceneName = _currentSceneName;
            if (replacedTheScene)
                _currentSceneName = sceneName;
            _sceneLoadsWaitingForTheirFrame.Add(new SceneLoad
            {
                SceneName = sceneName,
                PreviousSceneName = previousSceneName,
                ReplacedTheScene = replacedTheScene
            });
        }

        private static bool HeavyAnalyticsIsOnInTheLoadedConfig() => FeatureIsOnInTheLoadedConfig(ProtokitePlaytestFeatures.HeavyAnalytics);

        private static void StartMeasuringPerformance()
        {
            _measuringPerformance = true;
            // A new timeline each start drops the window a stop cut short, which would read as a short stretch of play.
            _performanceTimeline = new ProtokitePlaytestPerformanceTimeline(ReadHitchFrameTimeMs, ReadMemoryUsedBytes);
            _activeSceneHandle = 0;
            FollowTheActiveScene();
            if (MemoryUsedBytesForTesting == null)
                _memoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            Debug.Log(LogPrefix + $"Heavy analytics is on: a performance window for every {ProtokitePlaytestPerformanceTimeline.WindowSeconds:0} seconds of play, and every scene the game loads in place of another, go to the Flock SDK as '{ProtokitePlaytestEvents.Category}' events.");
        }

        // The window in progress is never sent: the next start replaces it. The loads still waiting for their Update are dropped here.
        private static void StopMeasuringPerformance()
        {
            _measuringPerformance = false;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            _sceneLoadsWaitingForTheirFrame.Clear();
            if (_memoryRecorder.Valid)
                _memoryRecorder.Dispose();
            _memoryRecorder = default;
        }

        /// <summary>Puts heavy analytics back as a fresh launch finds it.</summary>
        private static void ResetHeavyAnalyticsForNewLaunch()
        {
            StopMeasuringPerformance();
            _warnedHeavyAnalyticsWithoutFlockAnalytics = false;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
            => NoteSceneLoaded(scene.name, mode == LoadSceneMode.Single);

        // The map is the active scene, whichever way it became active; its name is read only when it changes, as each read makes a string.
        private static void FollowTheActiveScene()
        {
            Scene active = SceneManager.GetActiveScene();
            if (active.handle == _activeSceneHandle)
                return;
            _activeSceneHandle = active.handle;
            _currentSceneName = active.name;
        }

        // Every load noted since the last Update happened since it, so this frame's time carries them all.
        private static void SendSceneLoadsThisFrameCarries(double frameSeconds)
        {
            if (_sceneLoadsWaitingForTheirFrame.Count == 0)
                return;
            foreach (SceneLoad load in _sceneLoadsWaitingForTheirFrame)
            {
                // A scene added beside the current one is no new level; its load is still not play.
                if (!load.ReplacedTheScene)
                    continue;
                Dictionary<string, object> properties = new Dictionary<string, object>
                {
                    { "map", load.SceneName },
                    { "load_seconds", RoundToHundredths(frameSeconds) }
                };
                if (!string.IsNullOrEmpty(load.PreviousSceneName))
                    properties["previous_map"] = load.PreviousSceneName;
                SendPlaytestEvent(ProtokitePlaytestEvents.LevelLoaded, properties);
            }
            _sceneLoadsWaitingForTheirFrame.Clear();
            _performanceTimeline.LeaveOutNextFrame();
        }

        private static void SendPerformanceWindow(ProtokitePlaytestPerformanceWindow window)
        {
            // Frame times and memory only. The session's length, pauses and frame rate are the Flock SDK's, and are never sent twice.
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "window_seconds", RoundToHundredths(window.Seconds) },
                { "frames", window.Frames },
                { "median_frame_time_ms", RoundToHundredths(window.MedianFrameTimeMs) },
                { "frame_time_95th_percentile_ms", RoundToHundredths(window.FrameTime95thPercentileMs) },
                { "frame_time_99th_percentile_ms", RoundToHundredths(window.FrameTime99thPercentileMs) },
                { "hitches", window.Hitches },
                { "hitch_threshold_ms", RoundToHundredths(window.HitchThresholdMs) }
            };
            // Left out where the platform does not report memory, rather than sent as 0.
            if (window.MemoryUsedMb.HasValue)
                properties["memory_used_mb"] = window.MemoryUsedMb.Value;
            if (window.MemoryPeakMb.HasValue)
                properties["memory_peak_mb"] = window.MemoryPeakMb.Value;
            if (!string.IsNullOrEmpty(_currentSceneName))
                properties["map"] = _currentSceneName;
            SendPlaytestEvent(ProtokitePlaytestEvents.PerformanceWindow, properties);
        }

        private static bool SendPlaytestEvent(string eventName, Dictionary<string, object> properties)
        {
            FlockClient flock = RunningFlock();
            return flock != null && flock.Analytics.TrackEvent(eventName, properties, ProtokitePlaytestEvents.Category);
        }

        private static double ReadHitchFrameTimeMs()
        {
            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            if (settings == null)
                return DefaultHitchFrameTimeMs;
            // The inspector's minimum binds only the inspector: a value set from code under it, or not a number, reads as it.
            return settings.HitchFrameTimeMs >= MinimumHitchFrameTimeMs ? settings.HitchFrameTimeMs : MinimumHitchFrameTimeMs;
        }

        private static long? ReadMemoryUsedBytes()
        {
            if (MemoryUsedBytesForTesting != null)
                return MemoryUsedBytesForTesting();
            // The counter's value now: the last finished frame's value is 0 until the profiler has finished one.
            long usedBytes = _memoryRecorder.Valid ? _memoryRecorder.CurrentValue : 0;
            return usedBytes > 0 ? usedBytes : (long?)null;
        }

        private static double RoundToHundredths(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }
}
