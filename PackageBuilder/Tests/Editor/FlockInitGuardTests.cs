using Flock;
using Flock.Exceptions;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // Init/lifecycle contract: single init, pre-init access throws, token replacement on re-login.
    // (AUTH-G1/G2/G4, INIT-01/02.)
    public class FlockInitGuardTests
    {
        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        // ---- AUTH-G1 / INIT-01: Create is single-shot ----
        [Test]
        public void DoubleCreate_Throws()
        {
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
            {
                Assert.Throws<FlockException>(() => FlockTestClient.Create(new FlockFakeTransport()));
            }
        }

        // ---- AUTH-G2 / INIT-02: Instance before init throws (intended contract, no null-guard) ----
        [Test]
        public void InstanceBeforeInit_ThrowsFlockException()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();

            Assert.Throws<FlockException>(() =>
            {
                FlockClient ignored = FlockClient.Instance;
            });
        }

        // ---- AUTH-G4: a second sign-in replaces the player cleanly and stays authenticated ----
        [Test]
        public void SignInAgain_ReplacesPlayer_StaysAuthenticated()
        {
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
            {
                h.LoginAs("player-a");
                Assert.AreEqual("player-a", h.Client.CurrentPlayerId);

                h.LoginAs("player-b");
                Assert.AreEqual("player-b", h.Client.CurrentPlayerId, "Re-sign-in swaps the active player.");
                Assert.IsTrue(h.Client.IsAuthenticated);
            }
        }
    }
}
