using Flock.Analytics;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Session snapshot model: the persisted/spooled shape used for quit + next-launch recovery. Crash detection
    // was deliberately dropped, so the snapshot must not carry a was_crash field (SESS-06); and it must round-trip
    // cleanly under the stable snake_case wire names recovery relies on.
    public class FlockSessionSnapshotTests
    {
        // ---- SESS-06: crash detection deferred -> no was_crash field ----
        [Test]
        public void Snapshot_DoesNotSerialize_WasCrash()
        {
            FlockSessionSnapshot snapshot = new FlockSessionSnapshot { SessionId = "s1", IsActive = true };

            string json = JsonConvert.SerializeObject(snapshot);

            StringAssert.DoesNotContain("was_crash", json, "Crash detection is deferred; the snapshot must not carry was_crash.");
        }

        // ---- SESS-05: snapshot round-trips the fields recovery relies on ----
        [Test]
        public void Snapshot_RoundTrips_RecoveryFields()
        {
            FlockSessionSnapshot original = new FlockSessionSnapshot
            {
                SessionId = "s1",
                ServerSessionId = "srv-1",
                PlayerId = "player-a",
                SessionNumber = 3,
                IsActive = true,
                IsFirstSession = false,
                ScreensViewed = 5
            };

            string json = JsonConvert.SerializeObject(original);
            FlockSessionSnapshot restored = JsonConvert.DeserializeObject<FlockSessionSnapshot>(json);

            Assert.AreEqual("s1", restored.SessionId);
            Assert.AreEqual("srv-1", restored.ServerSessionId);
            Assert.AreEqual("player-a", restored.PlayerId);
            Assert.AreEqual(3, restored.SessionNumber);
            Assert.IsTrue(restored.IsActive);
            Assert.IsFalse(restored.IsFirstSession);
            Assert.AreEqual(5, restored.ScreensViewed);
        }

        // ---- SESS: stable snake_case wire names (recovery deserializes older payloads by these keys) ----
        [Test]
        public void Snapshot_UsesStableSnakeCaseWireNames()
        {
            FlockSessionSnapshot snapshot = new FlockSessionSnapshot { SessionId = "s1", ServerSessionId = "srv-1", IsActive = true };

            string json = JsonConvert.SerializeObject(snapshot);

            StringAssert.Contains("\"session_id\"", json);
            StringAssert.Contains("\"server_session_id\"", json);
            StringAssert.Contains("\"is_active\"", json);
        }
    }
}
