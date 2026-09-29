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

        private FlockTerminationTracker CreateTracker(bool enabled = true, float heartbeatIntervalSeconds = 60f)
        {
            return new FlockTerminationTracker(new NullFlockLogger(), enabled, _markerPath, heartbeatIntervalSeconds);
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
        public void ReadMarker_NoLastAliveTime_DeletesFileAndReturnsNull()
        {
            PlantMarker("{\"session_id\":\"s1\",\"last_state\":\"foreground\"}");
            Assert.IsNull(ReadMarker());
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void ReadMarker_NoSessionId_IsALaunchThatEndedBeforeSignIn()
        {
            PlantMarker("{\"session_id\":null,\"last_state\":\"foreground\",\"last_alive_utc\":\"2026-09-28T12:00:00Z\",\"exception_count\":2}");
            FlockTerminationMarker marker = ReadMarker();
            Assert.IsNotNull(marker, "A crash before any sign-in is still a crash");
            Assert.IsNull(marker.SessionId);
            Assert.AreEqual(2, marker.ExceptionCount);
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
        public void BeginTracking_WritesForegroundMarkerWithNoSessionYet()
        {
            CreateTracker().BeginTracking();
            FlockTerminationMarker marker = ReadMarker();
            Assert.IsNotNull(marker, "Tracking starts with the launch, before anyone signs in");
            Assert.IsNull(marker.SessionId);
            Assert.AreEqual("foreground", marker.LastState);
            Assert.AreEqual(0, marker.ExceptionCount);
            Assert.AreEqual(1, Directory.GetFiles(_folder).Length, "The marker is saved through a temporary file that is moved into place.");
        }

        [Test]
        public void BeginTracking_Disabled_WritesNothing()
        {
            CreateTracker(enabled: false).BeginTracking();
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void BeginTracking_AgainWhileTracking_KeepsTheLaunchsMarker()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking();
            tracker.NoteSessionStarted("s1");
            tracker.NoteException();
            tracker.HandleHeartbeat();

            tracker.BeginTracking();
            Assert.AreEqual("s1", ReadMarker().SessionId);
            Assert.AreEqual(1, ReadMarker().ExceptionCount, "Not started over");
        }

        [Test]
        public void ASessionNamesItselfInTheMarkerAndItsEndLeavesTheMarkerTracking()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking();

            tracker.NoteSessionStarted("s1");
            Assert.AreEqual("s1", ReadMarker().SessionId);

            tracker.NoteSessionEnded();
            FlockTerminationMarker afterSignOut = ReadMarker();
            Assert.IsNotNull(afterSignOut, "A crash after sign-out is still this launch's crash");
            Assert.IsNull(afterSignOut.SessionId);
            Assert.IsTrue(tracker.IsTracking);
        }

        [Test]
        public void StopTracking_ClearsMarker()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking();
            tracker.StopTracking();
            Assert.IsFalse(File.Exists(_markerPath));
        }

        [Test]
        public void HandleAppBackgrounded_PersistsStateTransitions()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking();

            tracker.HandleAppBackgrounded(true);
            Assert.AreEqual("background", ReadMarker().LastState);

            tracker.HandleAppBackgrounded(false);
            Assert.AreEqual("foreground", ReadMarker().LastState);
        }

        [Test]
        public void NoteException_CountPersistsOnHeartbeatOnly()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking();

            tracker.NoteException();
            tracker.NoteException();
            // In-memory only until a persistence point — an exception loop must not hammer disk.
            Assert.AreEqual(0, ReadMarker().ExceptionCount);

            tracker.HandleHeartbeat();
            Assert.AreEqual(2, ReadMarker().ExceptionCount);
        }

        [Test]
        public void HandleHeartbeat_RefreshesLastAlive()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking();
            DateTime before = ReadMarker().LastAliveUtc;
            tracker.HandleHeartbeat();
            Assert.GreaterOrEqual(ReadMarker().LastAliveUtc, before);
        }

        [Test]
        public void HandleTick_BeatsOncePerInterval_WithNoSessionRunning()
        {
            FlockTerminationTracker tracker = CreateTracker(heartbeatIntervalSeconds: 60f);
            tracker.BeginTracking();
            tracker.HandleTick(100.0);

            tracker.NoteException();
            tracker.HandleTick(159.9);
            Assert.AreEqual(0, ReadMarker().ExceptionCount, "Not yet a minute since the last beat");

            tracker.HandleTick(160.0);
            Assert.AreEqual(1, ReadMarker().ExceptionCount, "A beat a minute later, before anyone signed in");
        }

        [Test]
        public void HandleTick_NoHeartbeatInterval_NeverBeats()
        {
            FlockTerminationTracker tracker = CreateTracker(heartbeatIntervalSeconds: 0f);
            tracker.BeginTracking();
            tracker.NoteException();
            tracker.HandleTick(1000.0);
            Assert.AreEqual(0, ReadMarker().ExceptionCount);
        }

        [Test]
        public void Handlers_BeforeBeginTracking_AreNoOps()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.HandleAppBackgrounded(true);
            tracker.HandleHeartbeat();
            tracker.HandleTick(1000.0);
            tracker.NoteException();
            tracker.NoteSessionStarted("s1");
            tracker.NoteSessionEnded();
            Assert.IsFalse(File.Exists(_markerPath));
        }
    }
}
