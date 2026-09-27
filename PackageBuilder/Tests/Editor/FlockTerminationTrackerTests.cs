using System;
using System.IO;
using NUnit.Framework;
using Flock.Analytics;
using Flock.Logging;

namespace Flock.Tests.Editor
{
    // Locks the termination marker round-trip and the pure classifier. Lifecycle wiring
    // (FlockBehaviour subscriptions) and real dirty-exit behavior are Unity-only.
    public class FlockTerminationTrackerTests
    {
        private string _folder;
        private string _markerPath;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "flock_marker_" + Guid.NewGuid().ToString("N"));
            _markerPath = Path.Combine(_folder, FlockAnalyticsLaunches.TerminationMarkerFileName);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private FlockTerminationTracker CreateTracker(bool enabled = true)
        {
            return new FlockTerminationTracker(new NullFlockLogger(), enabled, _markerPath);
        }

        private FlockTerminationMarker ReadMarker() => FlockTerminationTracker.ReadMarker(_markerPath, new NullFlockLogger());

        private void PlantMarker(string json)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(_markerPath, json);
        }

        [Test]
        public void Classify_NullMarker_ReturnsNull()
        {
            Assert.IsNull(FlockTerminationTracker.Classify(null));
        }

        [Test]
        public void Classify_BackgroundState_ReturnsBackgroundKill()
        {
            FlockTerminationMarker marker = new FlockTerminationMarker { SessionId = "s1", LastState = "background" };
            Assert.AreEqual("background_kill", FlockTerminationTracker.Classify(marker));
        }

        [Test]
        public void Classify_ForegroundState_ReturnsAbnormal()
        {
            FlockTerminationMarker marker = new FlockTerminationMarker { SessionId = "s1", LastState = "foreground" };
            Assert.AreEqual("abnormal", FlockTerminationTracker.Classify(marker));
        }

        [Test]
        public void Classify_UnknownState_ReturnsAbnormal()
        {
            // Defensive: anything that isn't provably background counts as foreground death.
            FlockTerminationMarker marker = new FlockTerminationMarker { SessionId = "s1", LastState = "garbage" };
            Assert.AreEqual("abnormal", FlockTerminationTracker.Classify(marker));
        }

        [Test]
        public void ReadMarker_NoMarker_ReturnsNull()
        {
            Assert.IsNull(ReadMarker());
        }

        [Test]
        public void ReadMarker_MalformedJson_DeletesFileAndReturnsNull()
        {
            PlantMarker("{not valid json");
            Assert.IsNull(ReadMarker());
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void ReadMarker_MissingSessionId_DeletesFileAndReturnsNull()
        {
            PlantMarker("{\"last_state\":\"foreground\"}");
            Assert.IsNull(ReadMarker());
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void ReadMarker_ValidMarker_RoundTrips()
        {
            DateTime alive = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
            PlantMarker("{\"session_id\":\"s1\",\"last_state\":\"background\",\"last_alive_utc\":\"2026-07-02T12:00:00Z\",\"exception_count\":3}");
            FlockTerminationMarker marker = ReadMarker();
            Assert.IsNotNull(marker);
            Assert.AreEqual("s1", marker.SessionId);
            Assert.AreEqual("background", marker.LastState);
            Assert.AreEqual(alive, marker.LastAliveUtc.ToUniversalTime());
            Assert.AreEqual(3, marker.ExceptionCount);
        }

        [Test]
        public void ClearMarker_DeletesFile()
        {
            PlantMarker("{\"session_id\":\"s1\"}");
            CreateTracker().ClearMarker();
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void BeginTracking_WritesForegroundMarker()
        {
            CreateTracker().BeginTracking("s1");
            FlockTerminationMarker marker = ReadMarker();
            Assert.IsNotNull(marker);
            Assert.AreEqual("s1", marker.SessionId);
            Assert.AreEqual("foreground", marker.LastState);
            Assert.AreEqual(0, marker.ExceptionCount);
            Assert.AreEqual(1, Directory.GetFiles(_folder).Length, "The marker is saved through a temporary file that is moved into place.");
        }

        [Test]
        public void BeginTracking_Disabled_WritesNothing()
        {
            CreateTracker(enabled: false).BeginTracking("s1");
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void StopTracking_ClearsMarker()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");
            tracker.StopTracking();
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void HandleAppBackgrounded_PersistsStateTransitions()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");

            tracker.HandleAppBackgrounded(true);
            Assert.AreEqual("background", ReadMarker().LastState);

            tracker.HandleAppBackgrounded(false);
            Assert.AreEqual("foreground", ReadMarker().LastState);
        }

        [Test]
        public void HandleException_CountPersistsOnHeartbeatOnly()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");

            tracker.HandleException("boom", "stack");
            tracker.HandleException("boom2", "stack");
            // In-memory only until a persistence point — an exception loop must not hammer disk.
            Assert.AreEqual(0, ReadMarker().ExceptionCount);

            tracker.HandleHeartbeat();
            Assert.AreEqual(2, ReadMarker().ExceptionCount);
        }

        [Test]
        public void HandleHeartbeat_RefreshesLastAlive()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");
            DateTime before = ReadMarker().LastAliveUtc;
            tracker.HandleHeartbeat();
            Assert.GreaterOrEqual(ReadMarker().LastAliveUtc, before);
        }

        [Test]
        public void Handlers_BeforeBeginTracking_AreNoOps()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.HandleAppBackgrounded(true);
            tracker.HandleHeartbeat();
            tracker.HandleException("boom", "stack");
            Assert.IsFalse(File.Exists(_markerPath));
        }
    }
}
