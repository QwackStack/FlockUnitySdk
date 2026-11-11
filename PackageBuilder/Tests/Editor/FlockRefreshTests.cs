using UnityEngine.TestTools;
using System.Collections;
using System;
using System.Threading;
using Flock.Auth;
using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // 401 -> silent refresh -> retry plumbing (FlockProviderBase.ExecuteAsync + FlockClient.TryRefreshTokenAsync),
    // driven through the snapshot test provider. Sequential cases only. The concurrent single-flight cases
    // (RFSH-03/05) live in Flock.Tests.PlayMode — they need the player loop to pump async continuations, which
    // EditMode does not do.
    public class FlockRefreshTests
    {
        // A refresh still on its way would hand its answer to the next test's client.
        [UnityTearDown]
        public IEnumerator EveryRefreshIsAnsweredBeforeTheNextTest()
        {
            yield return FlockTestClient.UntilEveryRefreshIsAnswered();
        }

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

        // ---- The server keeps one refresh token per player and replaces it on every refresh ----

        private const string Refresh = FlockEndpoints.PlayerTokenRefresh;

        private static readonly FlockHttpResponse Refused = FlockFakeTransport.Coded(401, "player.invalid_refresh_token");

        private static FlockHttpResponse Renewed(string playerId, string accessToken, string refreshToken)
            => FlockFakeTransport.Ok("{\"player_id\":\"" + playerId + "\",\"access_token\":\"" + accessToken + "\",\"refresh_token\":\"" + refreshToken + "\"}");

        // Every refresh is answered by `answer`, which runs while that refresh is on its way; any other request by 200 {}.
        private static FlockFakeTransport RefreshAnsweredBy(Func<FlockHttpResponse> answer)
            => new FlockFakeTransport().Default(request => request.Url.Contains(Refresh) ? answer() : FlockFakeTransport.Ok("{}"));

        private static string SentRefreshToken(FlockHttpRequest refresh) => (string)JObject.Parse(refresh.JsonBody)["refresh_token"];

        // Counts the sign-in events while it lives; Listen again after a shutdown, which clears every subscription.
        private sealed class SignInEvents : IDisposable
        {
            public int Refreshed;
            public int Expired;

            public SignInEvents() => Listen();

            public void Listen()
            {
                Dispose();
                FlockEvents.OnTokenRefreshed += OnRefreshed;
                FlockEvents.OnAuthExpired += OnExpired;
            }

            private void OnRefreshed() => Refreshed++;
            private void OnExpired() => Expired++;

            public void Dispose()
            {
                FlockEvents.OnTokenRefreshed -= OnRefreshed;
                FlockEvents.OnAuthExpired -= OnExpired;
            }
        }

        // A shutdown keeps the saved sign-in, so a refresh answered after it saves its new pair there and the next start restores a
        // sign-in the server still takes. The client that shut down keeps and raises nothing.
        [Test]
        public void ARefreshAnsweredAfterShutdownSavesItsNewPairAndRaisesNothing()
        {
            string renewed = FlockTestClient.MakeJwt("player-a", 3600, "renewed");
            Func<FlockHttpResponse> answer = null;
            using (FlockTestClient h = FlockTestClient.Create(RefreshAnsweredBy(() => answer())))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                FlockClient client = h.Client;
                int sessionExpired = 0;
                client.OnSessionExpired += () => sessionExpired++;
                answer = () =>
                {
                    FlockClient.Shutdown();
                    events.Listen();
                    return Renewed("player-a", renewed, "refresh-a-2");
                };

                Assert.IsFalse(h.Run(() => client.TryRefreshTokenAsync(CancellationToken.None)), "Its sign-in ended with the shutdown");

                StoredTokens saved = client.LoadPersistedTokens();
                Assert.AreEqual(renewed, saved?.AccessToken, "The next start restores the renewed sign-in");
                Assert.AreEqual("refresh-a-2", saved?.RefreshToken, "With the one refresh token the server still takes");
                Assert.IsFalse(client.IsAuthenticated, "No tokens kept in the client that shut down");
                Assert.AreEqual(0, events.Refreshed, "No refresh announced");
                Assert.AreEqual(0, events.Expired + sessionExpired, "No sign-in reported expired");
            }
        }

        [Test]
        public void ARefreshAnsweredAfterASignOutSavesNothing()
        {
            Func<FlockHttpResponse> answer = null;
            using (FlockTestClient h = FlockTestClient.Create(RefreshAnsweredBy(() => answer())))
            {
                h.LoginAs("player-a");
                answer = () =>
                {
                    h.Client.Authentication.Logout();
                    return Renewed("player-a", FlockTestClient.MakeJwt("player-a", 3600, "renewed"), "refresh-a-2");
                };

                Assert.IsFalse(h.Run(() => h.Client.TryRefreshTokenAsync(CancellationToken.None)));

                Assert.IsNull(h.Client.LoadPersistedTokens(), "The sign-out's save stays cleared");
                Assert.IsFalse(h.Client.IsAuthenticated, "Still signed out");
            }
        }

        // The save then holds the new sign-in's own refresh token, which the server took after the one sent here.
        [Test]
        public void ARefreshAnsweredAfterTheSamePlayerSignedInAgainLeavesTheNewSignInSaved()
        {
            string signedInAgain = FlockTestClient.MakeJwt("player-a", 3600, "again");
            Func<FlockHttpResponse> answer = null;
            using (FlockTestClient h = FlockTestClient.Create(RefreshAnsweredBy(() => answer())))
            {
                h.LoginAs("player-a");
                answer = () =>
                {
                    h.Client.Authentication.Logout();
                    h.LoginWithToken(signedInAgain, "refresh-a-again");
                    return Renewed("player-a", FlockTestClient.MakeJwt("player-a", 3600, "renewed"), "refresh-a-2");
                };

                Assert.IsFalse(h.Run(() => h.Client.TryRefreshTokenAsync(CancellationToken.None)));

                StoredTokens saved = h.Client.LoadPersistedTokens();
                Assert.AreEqual(signedInAgain, saved?.AccessToken);
                Assert.AreEqual("refresh-a-again", saved?.RefreshToken);
                Assert.AreEqual(signedInAgain, h.Client.PlayerAccessToken, "The new sign-in is untouched in the client too");
            }
        }

        // A store can hold the same refresh token for two players (a test's does), so the save is matched on its player as well.
        [Test]
        public void ARefreshForOnePlayerAnsweredAfterAnotherSignedInWithTheSameRefreshTokenLeavesTheirSaveAlone()
        {
            string playerB = FlockTestClient.MakeJwt("player-b", 3600);
            Func<FlockHttpResponse> answer = null;
            using (FlockTestClient h = FlockTestClient.Create(RefreshAnsweredBy(() => answer())))
            {
                h.LoginWithToken(FlockTestClient.MakeJwt("player-a", 3600), "refresh-shared");
                answer = () =>
                {
                    h.LoginWithToken(playerB, "refresh-shared");
                    return Renewed("player-a", FlockTestClient.MakeJwt("player-a", 3600, "renewed"), "refresh-a-2");
                };

                Assert.IsFalse(h.Run(() => h.Client.TryRefreshTokenAsync(CancellationToken.None)));

                StoredTokens saved = h.Client.LoadPersistedTokens();
                Assert.AreEqual(playerB, saved?.AccessToken, "Player B's saved sign-in is kept");
                Assert.AreEqual("refresh-shared", saved?.RefreshToken);
                Assert.AreEqual("player-b", h.Client.CurrentPlayerId);
            }
        }

        // The control for the cases below: the save still holds the refresh token the server refused, so the player is signed out.
        [Test]
        public void ARefusalWithTheSentRefreshTokenStillSavedSignsThePlayerOut()
        {
            FlockFakeTransport transport = RefreshAnsweredBy(() => Refused);
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockAuthException>(() => h.Run(() => Provider(h).FetchAsync("c", "k", Path)));

                Assert.IsFalse(h.Client.IsAuthenticated, "Signed out");
                Assert.AreEqual(1, events.Expired);
                Assert.AreEqual(1, transport.CountTo(Refresh));
                Assert.IsNull(h.Client.LoadPersistedTokens());
            }
        }

        // Another start of Flock, or another run of the game sharing the save, renewed the sign-in first, so the server refused the
        // refresh token this one sent: the saved pair is the same player's, so this sign-in carries on with it.
        [Test]
        public void ARefusalWhileTheSaveHoldsANewerPairForTheSamePlayerCarriesOnWithIt()
        {
            string renewedElsewhere = FlockTestClient.MakeJwt("player-a", 3600, "elsewhere");
            FlockTestClient client = null;
            FlockFakeTransport transport = RefreshAnsweredBy(() =>
            {
                client.TokenStore.Save(renewedElsewhere, "refresh-a-2");
                return Refused;
            });
            transport.OnSequence(Path, FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"after\"}"));
            using (FlockTestClient h = client = FlockTestClient.Create(transport))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                int signIn = h.Client.SignInNumber;

                SnapshotProbe r = h.Run(() => Provider(h).FetchAsync("c", "k", Path));

                Assert.AreEqual("after", r.Value, "The request went again");
                Assert.AreEqual("Bearer " + renewedElsewhere, transport.LastTo(Path).Headers["Authorization"], "With the saved token");
                Assert.IsTrue(h.Client.IsAuthenticated, "Not signed out");
                Assert.AreEqual(signIn, h.Client.SignInNumber, "The same sign-in");
                Assert.AreEqual(1, transport.CountTo(Refresh), "The saved access token is good, so no other refresh is sent");
                Assert.AreEqual(1, events.Refreshed, "Announced as a refresh");
                Assert.AreEqual(0, events.Expired);
                Assert.AreEqual("refresh-a-2", h.Client.LoadPersistedTokens()?.RefreshToken);
            }
        }

        [Test]
        public void ARefusalWhileTheSaveHoldsANewerPairWhoseAccessTokenRanOutRefreshesWithItOnce()
        {
            string renewed = FlockTestClient.MakeJwt("player-a", 3600, "renewed");
            FlockTestClient client = null;
            int refreshes = 0;
            FlockFakeTransport transport = RefreshAnsweredBy(() =>
            {
                if (++refreshes > 1)
                    return Renewed("player-a", renewed, "refresh-a-3");
                client.TokenStore.Save(FlockTestClient.MakeJwt("player-a", -60, "elsewhere"), "refresh-a-2");
                return Refused;
            });
            transport.OnSequence(Path, FlockFakeTransport.Coded(401, "player.token_expired"), FlockFakeTransport.Ok("{\"value\":\"after\"}"));
            using (FlockTestClient h = client = FlockTestClient.Create(transport))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                int signIn = h.Client.SignInNumber;

                SnapshotProbe r = h.Run(() => Provider(h).FetchAsync("c", "k", Path));

                Assert.AreEqual("after", r.Value);
                Assert.AreEqual(2, transport.CountTo(Refresh), "Exactly one more refresh");
                Assert.AreEqual("refresh-a-2", SentRefreshToken(transport.LastTo(Refresh)), "Sent with the saved refresh token");
                Assert.AreEqual("Bearer " + renewed, transport.LastTo(Path).Headers["Authorization"]);
                Assert.AreEqual(signIn, h.Client.SignInNumber, "The same sign-in");
                Assert.AreEqual("refresh-a-3", h.Client.LoadPersistedTokens()?.RefreshToken);
                Assert.AreEqual(1, events.Refreshed);
                Assert.AreEqual(0, events.Expired);
            }
        }

        // The refresh sent with a saved pair is refused too, though the save meanwhile holds a newer one again: a saved pair is taken
        // once, so the player is signed out rather than refreshed for ever.
        [Test]
        public void ASavedPairIsTakenOnceAndASecondRefusalSignsThePlayerOut()
        {
            FlockTestClient client = null;
            int refreshes = 0;
            FlockFakeTransport transport = RefreshAnsweredBy(() =>
            {
                if (++refreshes <= 2)
                    client.TokenStore.Save(FlockTestClient.MakeJwt("player-a", -60, "elsewhere-" + refreshes), "refresh-a-" + (refreshes + 1));
                return Refused;
            });
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired"));
            using (FlockTestClient h = client = FlockTestClient.Create(transport))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockAuthException>(() => h.Run(() => Provider(h).FetchAsync("c", "k", Path)));

                Assert.AreEqual(2, transport.CountTo(Refresh), "The refused one and the one sent with the saved pair");
                Assert.IsFalse(h.Client.IsAuthenticated, "Signed out");
                Assert.AreEqual(1, events.Expired);
            }
        }

        // A saved pair without a refresh token could never be renewed, so it is not taken.
        [Test]
        public void ARefusalWhileTheSaveHoldsAPairWithNoRefreshTokenSignsThePlayerOut()
        {
            FlockTestClient client = null;
            FlockFakeTransport transport = RefreshAnsweredBy(() =>
            {
                client.TokenStore.Save(FlockTestClient.MakeJwt("player-a", 3600, "elsewhere"), "");
                return Refused;
            });
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired"));
            using (FlockTestClient h = client = FlockTestClient.Create(transport))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockAuthException>(() => h.Run(() => Provider(h).FetchAsync("c", "k", Path)));

                Assert.IsFalse(h.Client.IsAuthenticated, "Signed out, not carried on with no way to renew");
                Assert.AreEqual(1, events.Expired);
                Assert.AreEqual(0, events.Refreshed);
            }
        }

        [Test]
        public void ARefusalWhileTheSaveHoldsAnotherPlayersPairSignsThePlayerOut()
        {
            FlockTestClient client = null;
            FlockFakeTransport transport = RefreshAnsweredBy(() =>
            {
                client.TokenStore.Save(FlockTestClient.MakeJwt("player-b", 3600), "refresh-b");
                return Refused;
            });
            transport.On(Path, FlockFakeTransport.Coded(401, "player.token_expired"));
            using (FlockTestClient h = client = FlockTestClient.Create(transport))
            using (SignInEvents events = new SignInEvents())
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockAuthException>(() => h.Run(() => Provider(h).FetchAsync("c", "k", Path)));

                Assert.IsFalse(h.Client.IsAuthenticated, "Player A is signed out, not carried on as player B");
                Assert.AreEqual(1, transport.CountTo(Refresh));
                Assert.AreEqual(1, events.Expired);
                Assert.AreEqual(0, events.Refreshed);
            }
        }
    }
}
