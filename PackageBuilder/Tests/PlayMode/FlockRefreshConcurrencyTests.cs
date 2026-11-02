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

        // ---- A restart while a refresh is on its way: the server takes each refresh token once ----

        private static FlockHttpResponse Granted(string accessToken, string refreshToken)
            => FlockFakeTransport.Ok("{\"player_id\":\"player-a\",\"access_token\":\"" + accessToken + "\",\"refresh_token\":\"" + refreshToken + "\"}");

        // Flock shut down while its refresh waited for an answer and started again at once: the next client restores the same saved
        // pair, whose access token has run out. It waits for that answer and takes the pair it saved, rather than sending the same
        // refresh token a second time, which the server could grant twice or refuse.
        [UnityTest]
        public IEnumerator ARestoreAfterARestartWaitsForTheEarlierRefreshAndTakesItsPair()
        {
            string granted = FlockTestClient.MakeJwt("player-a", 3600, "granted");
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerTokenRefresh, Granted(granted, "refresh-a-2"));
            transport.GateNext(FlockEndpoints.PlayerTokenRefresh);
            _h = FlockTestClient.Create(transport);
            _h.LoginWithToken(FlockTestClient.MakeJwt("player-a", -60), "refresh-a");
            Task<bool> earlier = _h.Client.TryRefreshTokenAsync(System.Threading.CancellationToken.None);
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.PlayerTokenRefresh) >= 1, "The first client's refresh is on its way");

            FlockClient.Shutdown();
            FlockClient next = _h.CreateAgain();
            Task<bool> restore = next.Authentication.TryRestoreSessionAsync();
            for (int frame = 0; frame < 5; frame++)
                yield return null;
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "The next client waits for the answer to the same refresh token");

            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => restore.IsCompleted, "The restore ended");

            Assert.IsTrue(restore.Result, "Restored: " + restore.Exception);
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "One refresh in all");
            Assert.AreEqual(granted, next.PlayerAccessToken, "Signed in with the pair the first refresh was granted");
            Assert.AreEqual("refresh-a-2", next.LoadPersistedTokens()?.RefreshToken, "And that pair is saved");
            Assert.IsFalse(earlier.Result, "The first client's refresh ended with its client");
        }

        // The earlier refresh got no answer, so nothing new was saved: the next client sends its own once it has waited. Asked with a
        // token the caller could cancel, which waits the other way.
        [UnityTest]
        public IEnumerator ARestoreAfterARestartSendsItsOwnRefreshWhenTheEarlierOneGotNoAnswer()
        {
            string granted = FlockTestClient.MakeJwt("player-a", 3600, "granted");
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerTokenRefresh, FlockFakeTransport.Offline(), Granted(granted, "refresh-a-2"));
            transport.GateNext(FlockEndpoints.PlayerTokenRefresh);
            _h = FlockTestClient.Create(transport);
            _h.LoginWithToken(FlockTestClient.MakeJwt("player-a", -60), "refresh-a");
            Task<bool> earlier = _h.Client.TryRefreshTokenAsync(System.Threading.CancellationToken.None);
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.PlayerTokenRefresh) >= 1, "The first client's refresh is on its way");

            FlockClient.Shutdown();
            FlockClient next = _h.CreateAgain();
            System.Threading.CancellationTokenSource couldCancel = new System.Threading.CancellationTokenSource();
            Task<bool> restore = next.Authentication.TryRestoreSessionAsync(couldCancel.Token);
            for (int frame = 0; frame < 5; frame++)
                yield return null;
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "The next client waits for the earlier answer first");

            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => restore.IsCompleted, "The restore ended");

            Assert.IsTrue(restore.Result, "Restored: " + restore.Exception);
            Assert.AreEqual(2, transport.CountTo(FlockEndpoints.PlayerTokenRefresh), "Its own refresh went once the earlier one had its answer");
            Assert.AreEqual(granted, next.PlayerAccessToken);
            Assert.IsFalse(earlier.Result);
        }
    }
}
