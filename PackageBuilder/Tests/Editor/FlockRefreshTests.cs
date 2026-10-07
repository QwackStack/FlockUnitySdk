using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // 401 -> silent refresh -> retry plumbing (FlockProviderBase.ExecuteAsync + FlockClient.TryRefreshTokenAsync),
    // driven through the snapshot test provider. Sequential cases only. The concurrent single-flight cases
    // (RFSH-03/05) live in Flock.Tests.PlayMode — they need the player loop to pump async continuations, which
    // EditMode does not do.
    public class FlockRefreshTests
    {
        private const string Path = "probe/thing";

        private static FlockTestSnapshotProvider Provider(FlockTestClient h) => new FlockTestSnapshotProvider(h.Client);

        private static FlockHttpResponse RefreshOk(string playerId)
            => FlockFakeTransport.Ok("{\"player_id\":\"" + playerId
                + "\",\"access_token\":\"" + FlockTestClient.MakeJwt(playerId, 3600, "refreshed")
                + "\",\"refresh_token\":\"r2\"}");

        // ---- RFSH-01 ----
        [Test]
        public void DataRequest_401_RefreshesThenRetries_Succeeds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(Path,
                FlockFakeTransport.Coded(401, "player.token_expired"),
                FlockFakeTransport.Ok("{\"value\":\"after\"}"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                SnapshotProbe r = h.Run(() => p.FetchAsync("c", "k", Path));

                Assert.AreEqual("after", r.Value);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "Exactly one refresh.");
                Assert.AreEqual(2, transport.CountTo(Path), "Original 401 + one retry.");
            }
        }

        // ---- RFSH-02 ----
        [Test]
        public void Data401_WhileUnauthenticated_DoesNotRefresh()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true); // NOT logged in
                FlockTestSnapshotProvider p = Provider(h);

                Assert.Catch<FlockAuthException>(() => h.Run(() => p.FetchAsync("c", "k", Path)));
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "No refresh when unauthenticated.");
            }
        }

        // ---- RFSH-04 ----
        [Test]
        public void RefreshItselfFails_SurfacesAuth_NoLoop()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, FlockFakeTransport.Ok("{}")); // empty -> refresh fails
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                Assert.Catch<FlockAuthException>(() => h.Run(() => p.FetchAsync("c", "k", Path)));
                Assert.AreEqual(1, transport.CountTo(Path), "No retry loop after a failed refresh.");
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "One refresh attempt.");
            }
        }

        // ---- RFSH-06 ----
        [Test]
        public void RefreshSucceeds_ButDataStill401_SurfacesAfterOneRetry()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired")); // always 401
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                Assert.Catch<FlockAuthException>(() => h.Run(() => p.FetchAsync("c", "k", Path)));
                Assert.AreEqual(2, transport.CountTo(Path), "Original 401 + one post-refresh retry, then surface (no loop).");
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "Refresh attempted once, not in a loop.");
            }
        }

        // ---- RFSH-07 ----
        // A 403 means "not allowed" (not the host, not the party leader, a proxy's refusal), never a lapsed sign-in, so no
        // new token is asked for and the request is not sent again. RFSH-01 is the control: a 401 still refreshes and retries.
        [TestCase(true)]
        [TestCase(false)]
        public void Data403_IsNotAnsweredWithARefresh(bool coded)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Path, coded
                ? FlockFakeTransport.Coded(403, "multiplayer.not_host")
                : FlockFakeTransport.Status(403, "<html>Forbidden</html>"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                FlockTestSnapshotProvider p = Provider(h);

                FlockAuthException refused = Assert.Throws<FlockAuthException>(() => h.Run(() => p.FetchAsync("c", "k", Path)));

                Assert.AreEqual(403, refused.StatusCode);
                Assert.AreEqual(1, transport.CountTo(Path), "Sent once, not again after a refresh.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "No refresh for a 403.");
                Assert.IsTrue(h.Client.IsAuthenticated, "The player stays signed in.");
                if (coded)
                    Assert.AreEqual(FlockErrorCode.MultiplayerNotHost, refused.ErrorCode);
            }
        }
    }
}
