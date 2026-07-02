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
