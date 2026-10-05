using System;
using System.IO;
using Flock.Analytics;
using Flock.Logging;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Discard() is the consent-revoke path: it must stop the session locally without
    // firing OnSessionEnded (which End()/Reset() do, spooling a final record for delivery).
    public class FlockSessionDiscardTests
    {
        private string _folder;
        private string _statePath;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "flock_session_" + Guid.NewGuid().ToString("N"));
            _statePath = Path.Combine(_folder, FlockAnalyticsLaunches.SessionStateFileName);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private FlockSession NewSession(FlockAnalyticsConfig config) => new FlockSession(config, new NullFlockLogger(), _statePath);
        private static FlockAnalyticsConfig Config() => new FlockAnalyticsConfig
        {
            PersistSessionOnDisk = false,
            TrackFps = false,
            HeartbeatIntervalSeconds = 0f,
            EventBufferFlushIntervalSeconds = 0f
        };

        // Persisting variant: the live marker only exists when the session is written to disk.
        private static FlockAnalyticsConfig PersistingConfig() => new FlockAnalyticsConfig
        {
            PersistSessionOnDisk = true,
            TrackFps = false,
            HeartbeatIntervalSeconds = 0f,
            EventBufferFlushIntervalSeconds = 0f
        };

        // ---- SESS-10: a failed spool must keep the live marker ----
        // End() clears the marker on the invariant that the handler persisted the end durably. When the handler
        // could not (disk full, permissions), clearing anyway loses the session from both places at once — and the
        // old code logged "Session end spooled" regardless, so the loss was silent.
        [Test]
        public void End_SpoolFailed_KeepsMarker_SoNextLaunchRecovers()
        {
            FlockSession session = NewSession(PersistingConfig());
            session.OnSessionEnded += _ => session.ReportEndSpoolFailed();

            session.Start("player-1");
            session.End(FlockSessionEndReason.Quit);

            Assert.IsNotNull(FlockSession.ReadOrphanedSession(_statePath, new NullFlockLogger()),
                "The live marker must survive a failed spool so the session is recovered rather than lost.");
        }

        // ---- SESS-11: the normal path still clears, so a delivered end is not recovered twice ----
        [Test]
        public void End_SpoolSucceeded_ClearsMarker()
        {
            FlockSession session = NewSession(PersistingConfig());
            session.OnSessionEnded += _ => { };

            session.Start("player-1");
            Assert.IsTrue(File.Exists(_statePath), "A running session keeps its record in the launch's folder.");
            session.End(FlockSessionEndReason.Quit);

            Assert.IsNull(FlockSession.ReadOrphanedSession(_statePath, new NullFlockLogger()),
                "A spooled end clears the marker — otherwise the next launch re-reports a session already delivered.");
            Assert.IsFalse(File.Exists(_statePath));
        }

        [Test]
        public void Discard_ActiveSession_StopsSessionWithoutFiringOnSessionEnded()
        {
            FlockSession session = NewSession(Config());
            bool onSessionEndedFired = false;
            session.OnSessionEnded += _ => onSessionEndedFired = true;

            session.Start("player-1");
            Assert.IsTrue(session.IsActive);

            session.Discard();

            Assert.IsFalse(session.IsActive);
            Assert.IsFalse(onSessionEndedFired);
        }

        [Test]
        public void Discard_NoActiveSession_IsNoOp()
        {
            FlockSession session = NewSession(Config());

            // Must not throw when called with nothing active.
            session.Discard();

            Assert.IsFalse(session.IsActive);
        }
    }
}
