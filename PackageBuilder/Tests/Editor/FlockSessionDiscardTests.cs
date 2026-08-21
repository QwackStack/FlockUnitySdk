using Flock.Analytics;
using Flock.Logging;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Discard() is the consent-revoke path: it must stop the session locally without
    // firing OnSessionEnded (which End()/Reset() do, spooling a final record for delivery).
    public class FlockSessionDiscardTests
    {
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
            FlockSession session = new FlockSession(PersistingConfig(), new NullFlockLogger());
            session.OnSessionEnded += _ => session.ReportEndSpoolFailed();

            session.Start("player-1");
            session.End(FlockSessionEndReason.Quit);

            FlockSession nextLaunch = new FlockSession(PersistingConfig(), new NullFlockLogger());
            Assert.IsNotNull(nextLaunch.RecoverOrphanedSession(),
                "The live marker must survive a failed spool so the session is recovered rather than lost.");

            nextLaunch.ClearPersistedState();
        }

        // ---- SESS-11: the normal path still clears, so a delivered end is not recovered twice ----
        [Test]
        public void End_SpoolSucceeded_ClearsMarker()
        {
            FlockSession session = new FlockSession(PersistingConfig(), new NullFlockLogger());
            session.OnSessionEnded += _ => { };

            session.Start("player-1");
            session.End(FlockSessionEndReason.Quit);

            FlockSession nextLaunch = new FlockSession(PersistingConfig(), new NullFlockLogger());
            Assert.IsNull(nextLaunch.RecoverOrphanedSession(),
                "A spooled end clears the marker — otherwise the next launch re-reports a session already delivered.");
        }

        [Test]
        public void Discard_ActiveSession_StopsSessionWithoutFiringOnSessionEnded()
        {
            FlockSession session = new FlockSession(Config(), new NullFlockLogger());
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
            FlockSession session = new FlockSession(Config(), new NullFlockLogger());

            // Must not throw when called with nothing active.
            session.Discard();

            Assert.IsFalse(session.IsActive);
        }
    }
}
