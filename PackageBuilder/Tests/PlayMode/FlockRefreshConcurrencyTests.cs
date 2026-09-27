using System;
using System.Collections;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
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
            // A test that failed while a request was held lets it go, so the run ends with that test's own failure.
            _h?.Transport.ReleaseGate();
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

        private static bool FailedWithAuth(Task task) => task.IsFaulted && task.Exception.GetBaseException() is FlockAuthException;

        // ---- RFSH-07: a refresh answered after sign-out does not sign the player back in ----
        [UnityTest]
        public IEnumerator ARefreshAnsweredAfterSignOutDoesNotSignThePlayerBackIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence("probe/x", FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"after\"}"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-a"));
            transport.GateNext(FlockEndpoints.PlayerTokenRefresh);
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            _h.SetReachable(true);
            int refreshedRaised = 0;
            Action countRefreshed = () => refreshedRaised++;
            FlockEvents.OnTokenRefreshed += countRefreshed;
            try
            {
                Task<SnapshotProbe> fetch = Provider(_h).FetchAsync("c", "k", "probe/x");
                yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.PlayerTokenRefresh) >= 1, "The refresh is on its way");

                _h.Client.Authentication.Logout();
                transport.ReleaseGate();
                yield return FlockTestWait.Until(() => fetch.IsCompleted, "The request ended");

                Assert.IsTrue(FailedWithAuth(fetch), "The request's own refusal: " + fetch.Exception);
                Assert.IsFalse(_h.Client.IsAuthenticated, "Still signed out");
                Assert.IsNull(_h.Client.LoadPersistedTokens(), "Nothing saved for the next launch to sign in with");
                Assert.AreEqual(1, transport.CountTo("probe/x"), "Not sent again");
                Assert.AreEqual(0, refreshedRaised, "No refresh is announced");
            }
            finally
            {
                FlockEvents.OnTokenRefreshed -= countRefreshed;
            }
        }

        // ---- RFSH-08: a refresh answered after another player signed in leaves that player signed in ----
        [UnityTest]
        public IEnumerator ARefreshAnsweredAfterAnotherPlayerSignedInLeavesThemSignedIn()
        {
            foreach (bool refreshRefused in new[] { false, true })
            {
                FlockFakeTransport transport = new FlockFakeTransport();
                transport.OnSequence("probe/x", FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"after\"}"));
                transport.On(FlockEndpoints.PlayerTokenRefresh, refreshRefused ? FlockFakeTransport.Coded(401, "player.refresh_token_invalid") : RefreshOk("player-a"));
                transport.GateNext(FlockEndpoints.PlayerTokenRefresh);
                _h?.Dispose();
                _h = FlockTestClient.Create(transport);
                _h.LoginAs("player-a");
                _h.SetReachable(true);
                int expiredRaised = 0;
                Action countExpired = () => expiredRaised++;
                FlockEvents.OnAuthExpired += countExpired;
                try
                {
                    Task<SnapshotProbe> fetch = Provider(_h).FetchAsync("c", "k", "probe/x");
                    yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.PlayerTokenRefresh) >= 1, "The refresh is on its way");

                    _h.Client.Authentication.Logout();
                    _h.LoginAs("player-b");
                    string bearerB = _h.Client.GetBaseHeaders()["Authorization"];
                    transport.ReleaseGate();
                    yield return FlockTestWait.Until(() => fetch.IsCompleted, "The request ended");

                    string when = refreshRefused ? " (refresh refused)" : " (refresh granted)";
                    Assert.IsTrue(FailedWithAuth(fetch), "Player A's request fails as A's" + when + ": " + fetch.Exception);
                    Assert.AreEqual("player-b", _h.Client.CurrentPlayerId, "Player B is still the one signed in" + when);
                    Assert.AreEqual(bearerB, _h.Client.GetBaseHeaders()["Authorization"], "With B's own token" + when);
                    Assert.AreEqual(bearerB, "Bearer " + _h.Client.LoadPersistedTokens()?.AccessToken, "And B's tokens are what is saved" + when);
                    Assert.AreEqual(1, transport.CountTo("probe/x"), "A's request is not sent again as B" + when);
                    Assert.AreEqual(0, expiredRaised, "B's session is not reported expired" + when);
                }
                finally
                {
                    FlockEvents.OnAuthExpired -= countExpired;
                }
            }
        }

        // ---- RFSH-09: a request refused after another player signed in is not refreshed and sent again as them ----
        [UnityTest]
        public IEnumerator ARequestRefusedAfterAnotherPlayerSignedInIsNotSentAgainAsThem()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence("probe/x", FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"as-b\"}"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, RefreshOk("player-b"));
            transport.GateNext("probe/x");
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            _h.SetReachable(true);

            Task<SnapshotProbe> fetch = Provider(_h).FetchAsync("c", "k", "probe/x");
            yield return FlockTestWait.Until(() => transport.CountTo("probe/x") >= 1, "Player A's request is on its way");

            // Straight to another player, with no sign-out between: a sign-in on its own ends the one before it.
            _h.LoginAs("player-b");
            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => fetch.IsCompleted, "The request ended");

            Assert.IsTrue(FailedWithAuth(fetch), "A's request fails as A's: " + fetch.Exception);
            Assert.AreEqual(0, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "B's tokens are not refreshed for A's request");
            Assert.AreEqual(1, transport.CountTo("probe/x"), "Nor is A's request sent again under B's sign-in");
            Assert.AreEqual("player-b", _h.Client.CurrentPlayerId);
        }
    }
}
