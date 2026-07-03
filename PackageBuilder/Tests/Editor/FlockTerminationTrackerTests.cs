using System;
using UnityEngine;
using NUnit.Framework;
using Flock.Analytics;
using Flock.Logging;

namespace Flock.Tests.Editor
{
    // Locks the termination marker round-trip and the pure classifier. Lifecycle wiring
    // (FlockBehaviour subscriptions) and real dirty-exit behavior are Unity-only.
    public class FlockTerminationTrackerTests
    {
        private const string KeyMarker = "flock_termination_marker";

        [SetUp]
        public void SetUp()
        {
            PlayerPrefs.DeleteKey(KeyMarker);
        }

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(KeyMarker);
        }

        private FlockTerminationTracker CreateTracker(bool enabled = true)
        {
            return new FlockTerminationTracker(new NullFlockLogger(), enabled);
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
        public void ReadSurvivingMarker_NoMarker_ReturnsNull()
        {
            Assert.IsNull(CreateTracker().ReadSurvivingMarker());
        }

        [Test]
        public void ReadSurvivingMarker_MalformedJson_ClearsKeyAndReturnsNull()
        {
            PlayerPrefs.SetString(KeyMarker, "{not valid json");
            FlockTerminationTracker tracker = CreateTracker();
            Assert.IsNull(tracker.ReadSurvivingMarker());
            Assert.IsFalse(PlayerPrefs.HasKey(KeyMarker));
        }

        [Test]
        public void ReadSurvivingMarker_MissingSessionId_ClearsKeyAndReturnsNull()
        {
            PlayerPrefs.SetString(KeyMarker, "{\"last_state\":\"foreground\"}");
            FlockTerminationTracker tracker = CreateTracker();
            Assert.IsNull(tracker.ReadSurvivingMarker());
            Assert.IsFalse(PlayerPrefs.HasKey(KeyMarker));
        }

        [Test]
        public void ReadSurvivingMarker_ValidMarker_RoundTrips()
        {
            DateTime alive = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
            PlayerPrefs.SetString(KeyMarker,
                "{\"session_id\":\"s1\",\"last_state\":\"background\",\"last_alive_utc\":\"2026-07-02T12:00:00Z\",\"exception_count\":3}");
            FlockTerminationMarker marker = CreateTracker().ReadSurvivingMarker();
            Assert.IsNotNull(marker);
            Assert.AreEqual("s1", marker.SessionId);
            Assert.AreEqual("background", marker.LastState);
            Assert.AreEqual(alive, marker.LastAliveUtc.ToUniversalTime());
            Assert.AreEqual(3, marker.ExceptionCount);
        }

        [Test]
        public void ReadSurvivingMarker_DisabledTracker_ReturnsNull()
        {
            PlayerPrefs.SetString(KeyMarker, "{\"session_id\":\"s1\",\"last_state\":\"foreground\"}");
            Assert.IsNull(CreateTracker(enabled: false).ReadSurvivingMarker());
        }

        [Test]
        public void ClearMarker_RemovesKey()
        {
            PlayerPrefs.SetString(KeyMarker, "{\"session_id\":\"s1\"}");
            CreateTracker().ClearMarker();
            Assert.IsFalse(PlayerPrefs.HasKey(KeyMarker));
        }

        [Test]
        public void BeginTracking_WritesForegroundMarker()
        {
            CreateTracker().BeginTracking("s1");
            FlockTerminationMarker marker = CreateTracker().ReadSurvivingMarker();
            Assert.IsNotNull(marker);
            Assert.AreEqual("s1", marker.SessionId);
            Assert.AreEqual("foreground", marker.LastState);
            Assert.AreEqual(0, marker.ExceptionCount);
        }

        [Test]
        public void BeginTracking_Disabled_WritesNothing()
        {
            CreateTracker(enabled: false).BeginTracking("s1");
            Assert.IsFalse(PlayerPrefs.HasKey(KeyMarker));
        }

        [Test]
        public void StopTracking_ClearsMarker()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");
            tracker.StopTracking();
            Assert.IsFalse(PlayerPrefs.HasKey(KeyMarker));
        }

        [Test]
        public void HandleAppBackgrounded_PersistsStateTransitions()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");

            tracker.HandleAppBackgrounded(true);
            Assert.AreEqual("background", CreateTracker().ReadSurvivingMarker().LastState);

            tracker.HandleAppBackgrounded(false);
            Assert.AreEqual("foreground", CreateTracker().ReadSurvivingMarker().LastState);
        }

        [Test]
        public void HandleException_CountPersistsOnHeartbeatOnly()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");

            tracker.HandleException("boom", "stack");
            tracker.HandleException("boom2", "stack");
            // In-memory only until a persistence point — an exception loop must not hammer disk.
            Assert.AreEqual(0, CreateTracker().ReadSurvivingMarker().ExceptionCount);

            tracker.HandleHeartbeat();
            Assert.AreEqual(2, CreateTracker().ReadSurvivingMarker().ExceptionCount);
        }

        [Test]
        public void HandleHeartbeat_RefreshesLastAlive()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.BeginTracking("s1");
            DateTime before = CreateTracker().ReadSurvivingMarker().LastAliveUtc;
            tracker.HandleHeartbeat();
            Assert.GreaterOrEqual(CreateTracker().ReadSurvivingMarker().LastAliveUtc, before);
        }

        [Test]
        public void Handlers_BeforeBeginTracking_AreNoOps()
        {
            FlockTerminationTracker tracker = CreateTracker();
            tracker.HandleAppBackgrounded(true);
            tracker.HandleHeartbeat();
            tracker.HandleException("boom", "stack");
            Assert.IsFalse(PlayerPrefs.HasKey(KeyMarker));
        }
    }
}
