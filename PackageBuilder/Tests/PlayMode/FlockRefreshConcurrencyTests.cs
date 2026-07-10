using System.Collections;
using System.Threading.Tasks;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Concurrent 401 -> single-flight refresh. PlayMode because these need the player loop to pump the async
    // continuations posted to Unity's SynchronizationContext (EditMode does not pump it, so the tasks never
    // complete there). Cleanup is in [SetUp]/[TearDown] (NUnit runs those even when a UnityTest fails, unlike a
    // coroutine's finally), so a failed test can't leak the FlockClient singleton into the next one.
    public class FlockRefreshConcurrencyTests
    {
        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private static FlockTestSnapshotProvider Provider(FlockTestClient h) => new FlockTestSnapshotProvider(h.Client);

        private static FlockHttpResponse RefreshOk(string playerId)
            => FlockFakeTransport.Ok("{\"player_id\":\"" + playerId
                + "\",\"access_token\":\"" + FlockTestClient.MakeJwt(playerId, 3600, "refreshed")
                + "\",\"refresh_token\":\"r2\"}");

        // ---- RFSH-03: N concurrent 401s trigger exactly one refresh (single-flight semaphore + generation) ----
        [UnityTest]
        public IEnumerator Concurrent401s_RefreshOnce_SingleFlight()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            for (int i = 0; i < 3; i++)
                transport.OnSequence("probe/" + i,
                    FlockFakeTransport.Coded(401, "player.token_expired"),
                    FlockFakeTransport.Ok("{\"value\":\"v" + i + "\"}"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            transport.GateNext(FlockEndpoints.PlayerTokenRefresh); // hold the first refresh open so all N pile up

            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            _h.SetReachable(true);
            FlockTestSnapshotProvider p = Provider(_h);

            Task<SnapshotProbe>[] tasks = new Task<SnapshotProbe>[3];
            for (int i = 0; i < 3; i++)
                tasks[i] = p.FetchAsync("c", "k" + i, "probe/" + i);

            int guard = 0;
            while (transport.CountTo(FlockEndpoints.PlayerTokenRefresh) < 1 && guard++ < 600)
                yield return null;

            transport.ReleaseGate();

            Task all = Task.WhenAll(tasks);
            guard = 0;
            while (!all.IsCompleted && guard++ < 600)
                yield return null;

            Assert.IsTrue(all.IsCompleted, "All concurrent fetches completed.");
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "Concurrent 401s share a single refresh.");
            for (int i = 0; i < 3; i++)
                Assert.AreEqual("v" + i, tasks[i].Result.Value, "Each request completes after the shared refresh.");
        }

        // ---- RFSH-05: the late waiter piggybacks the completed refresh (stale generation), retrying with the same token ----
        [UnityTest]
        public IEnumerator Concurrent401s_LateWaiter_UsesRefreshedToken_NoSecondRefresh()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence("probe/a", FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"a\"}"));
            transport.OnSequence("probe/b", FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"b\"}"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            transport.GateNext(FlockEndpoints.PlayerTokenRefresh);

            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            _h.SetReachable(true);
            string loginBearer = _h.Client.GetBaseHeaders()["Authorization"];
            FlockTestSnapshotProvider p = Provider(_h);

            Task<SnapshotProbe> ta = p.FetchAsync("c", "ka", "probe/a");
            Task<SnapshotProbe> tb = p.FetchAsync("c", "kb", "probe/b");

            int guard = 0;
            while (transport.CountTo(FlockEndpoints.PlayerTokenRefresh) < 1 && guard++ < 600)
                yield return null;

            transport.ReleaseGate();

            Task all = Task.WhenAll(ta, tb);
            guard = 0;
            while (!all.IsCompleted && guard++ < 600)
                yield return null;

            Assert.IsTrue(all.IsCompleted, "Both fetches completed.");
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "One refresh serves both (stale-generation piggyback).");
            string retryA = transport.AllTo("probe/a")[1].Headers["Authorization"];
            string retryB = transport.AllTo("probe/b")[1].Headers["Authorization"];
            Assert.AreEqual(retryA, retryB, "Both retries use the same refreshed token.");
            Assert.AreNotEqual(loginBearer, retryA, "The refreshed token differs from the original login token.");
        }
    }
}
