using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // Local players: several players signed in at once on one machine, each with a sign-in of their own.
    public class FlockLocalPlayersTests
    {
        [UnityTearDown]
        public IEnumerator EveryRefreshIsAnsweredBeforeTheNextTest()
        {
            yield return FlockTestClient.UntilEveryRefreshIsAnswered();
        }

        // Access tokens made once per test, so a request's bearer is compared against the very token the answer handed out.
        private readonly Dictionary<string, string> _tokens = new Dictionary<string, string>();

        private string Token(string playerId, string nonce)
        {
            string key = playerId + "/" + nonce;
            if (!_tokens.TryGetValue(key, out string token))
                _tokens[key] = token = FlockTestClient.MakeJwt(playerId, 3600, nonce);
            return token;
        }

        private FlockHttpResponse SignedInAs(string playerId, string nonce)
            => FlockFakeTransport.Ok("{\"player_id\":\"" + playerId + "\",\"access_token\":\"" + Token(playerId, nonce)
                + "\",\"refresh_token\":\"refresh-" + nonce + "\"}");

        private const string NoAccounts = "{\"accounts\":[]}";

        private static FlockLocalPlayer Add(FlockTestClient h)
        {
            FlockLocalPlayer player = h.Client.AddLocalPlayer();
            player.Client.ReachabilityProbe = () => true;
            return player;
        }

        private static string BearerOfLast(FlockFakeTransport transport, string path)
            => transport.LastTo(path).Headers.TryGetValue("Authorization", out string bearer) ? bearer : null;

        [Test]
        public void EachLocalPlayerHasSurfacesOfTheirOwn()
        {
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockLocalPlayer first = Add(h);
                FlockLocalPlayer second = Add(h);

                Assert.AreNotSame(first.Client, second.Client);
                Assert.AreNotSame(h.Client.Authentication, first.Authentication);
                Assert.AreNotSame(first.Authentication, second.Authentication);
                Assert.AreNotSame(first.Player, second.Player);
                Assert.AreNotSame(first.Commands, second.Commands);
                Assert.AreNotSame(first.Leaderboard, second.Leaderboard);
                Assert.IsNull(first.Client.Analytics, "Analytics stays with the main player.");
                Assert.IsFalse(first.IsSignedIn);
                Assert.IsNull(first.PlayerId);
                Assert.AreEqual(2, h.Client.LocalPlayers.Count);
            }
        }

        [Test]
        public void ALocalSignInChangesNoOneElse_AndIsRaisedOnItsOwnEvents()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"), SignedInAs("player-main", "main"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer first = Add(h);
                FlockLocalPlayer second = Add(h);
                int mainSignIns = 0, firstSignIns = 0;
                Action<FlockAuthInfo> countMain = _ => mainSignIns++;
                FlockEvents.OnAuthenticated += countMain;
                first.OnAuthenticated += _ => firstSignIns++;
                try
                {
                    h.Run(() => first.Authentication.LoginWithDeviceAsync("device-one"));

                    Assert.AreEqual("player-one", first.PlayerId);
                    Assert.IsFalse(second.IsSignedIn, "The other local player stays signed out.");
                    Assert.IsFalse(h.Client.IsAuthenticated, "The main player stays signed out.");
                    Assert.AreEqual(1, firstSignIns);
                    Assert.AreEqual(0, mainSignIns, "A local sign-in is not raised on FlockEvents.");

                    // Counter-case: the main player's sign-in is still raised on FlockEvents, and not on the local player's.
                    h.Run(() => h.Client.Authentication.LoginWithDeviceAsync("device-main"));
                    Assert.AreEqual(1, mainSignIns);
                    Assert.AreEqual(1, firstSignIns);
                    Assert.AreEqual("player-one", first.PlayerId, "The main player's sign-in leaves the local player as they were.");
                }
                finally
                {
                    FlockEvents.OnAuthenticated -= countMain;
                }
            }
        }

        [Test]
        public void EachPlayersRequestsCarryTheirOwnSignIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"), SignedInAs("player-two", "two"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-main");
                FlockLocalPlayer first = Add(h);
                FlockLocalPlayer second = Add(h);
                h.Run(() => first.Authentication.LoginWithDeviceAsync("device-one"));
                h.Run(() => second.Authentication.LoginWithDeviceAsync("device-two"));

                h.Run(() => first.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("player-one", "one"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));
                h.Run(() => second.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("player-two", "two"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));
                h.Run(() => h.Client.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + h.Client.PlayerAccessToken, BearerOfLast(transport, FlockEndpoints.PlayerAccounts),
                    "The main player's call carries the main player's sign-in.");
                Assert.AreEqual("player-main", h.Client.CurrentPlayerId);
                Assert.AreEqual("refresh-player-main", h.TokenStore.Load().RefreshToken, "A local player's sign-in is never saved.");
            }
        }

        [Test]
        public void ARemovedLocalPlayerRefusesEveryCall_SayingHowToCarryOn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-two", "two"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer second = Add(h);
                h.Run(() => second.Authentication.LoginWithDeviceAsync("device-two"));
                int loggedOut = 0;
                second.OnLoggedOut += () => loggedOut++;

                h.Client.RemoveLocalPlayer(second);

                Assert.AreEqual(1, loggedOut, "Removing a signed-in player raises their own sign-out.");
                Assert.IsFalse(second.IsSignedIn);
                Assert.AreEqual(0, h.Client.LocalPlayers.Count);
                int sent = transport.Requests.Count;

                FlockAuthException login = Assert.Throws<FlockAuthException>(() => h.Run(() => second.Authentication.LoginWithDeviceAsync("device-two")));
                StringAssert.Contains("RemoveLocalPlayer", login.Message);
                StringAssert.Contains("AddLocalPlayer", login.Hint);

                // Calls that check for a signed-in player first give the same cause and fix, not "sign in first".
                FlockAuthException guarded = Assert.Throws<FlockAuthException>(() => h.Run(() => second.Authentication.GetLinkedAccountsAsync()));
                StringAssert.Contains("AddLocalPlayer", guarded.Hint);
                FlockAuthException read = Assert.Throws<FlockAuthException>(() => h.Run(() => second.Player.GetMyDataByTemplateAsync("template-1")));
                StringAssert.Contains("AddLocalPlayer", read.Hint);
                FlockAuthException board = Assert.Throws<FlockAuthException>(() => h.Run(() => second.Leaderboard.GetByNameAsync("Board")));
                StringAssert.Contains("AddLocalPlayer", board.Hint);

                // Offline, a write would otherwise be queued for no one.
                second.Client.ReachabilityProbe = () => false;
                FlockAuthException write = Assert.Throws<FlockAuthException>(() => h.Run(() => second.Commands.UpdatePlayerDataFieldAsync("row-1", "Score", 1)));
                StringAssert.Contains("AddLocalPlayer", write.Hint);

                Assert.AreEqual(sent, transport.Requests.Count, "Nothing is sent for a removed player.");
            }
        }

        [Test]
        public void RemovingALocalPlayerTwiceIsRefused_WithAFix()
        {
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockLocalPlayer player = Add(h);
                h.Client.RemoveLocalPlayer(player);

                FlockValidationException twice = Assert.Throws<FlockValidationException>(() => h.Client.RemoveLocalPlayer(player));
                Assert.IsFalse(string.IsNullOrEmpty(twice.Hint));
                Assert.Throws<FlockValidationException>(() => h.Client.RemoveLocalPlayer(null));
            }
        }

        [Test]
        public void ALocalPlayerNotSignedInYetIsToldToSignInThroughTheirOwnAuthentication()
        {
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockLocalPlayer fresh = Add(h);

                FlockAuthException local = Assert.Throws<FlockAuthException>(() => h.Run(() => fresh.Player.GetMyDataByTemplateAsync("template-1")));
                StringAssert.Contains("LoginWithDeviceAsync", local.Hint);
                StringAssert.DoesNotContain("AddLocalPlayer", local.Hint, "Not ended, only not signed in yet.");

                // Counter-case: the main player keeps the general advice, and the exception it always gave.
                FlockValidationException main = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Player.GetMyDataByTemplateAsync("template-1")));
                Assert.AreEqual(FlockErrorHints.SignInFirst, main.Hint);
            }
        }

        [Test]
        public void ALocalSignInAsTheMainPlayerIsRefused_AndTheMainSignInKeepsTheNewTokens()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("shared-player", "newest"), SignedInAs("player-three", "three"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("shared-player");
                FlockLocalPlayer twin = Add(h);
                int twinSignIns = 0;
                twin.OnAuthenticated += _ => twinSignIns++;

                FlockAuthException refused = Assert.Throws<FlockAuthException>(() => h.Run(() => twin.Authentication.LoginWithDeviceAsync("device-shared")));

                Assert.AreEqual(FlockErrorCode.FlockPlayerAlreadySignedIn, refused.ErrorCode);
                Assert.AreEqual(FlockErrorHints.For(FlockErrorCode.FlockPlayerAlreadySignedIn), refused.Hint);
                Assert.IsFalse(twin.IsSignedIn);
                Assert.AreEqual(0, twinSignIns);
                Assert.AreEqual("shared-player", h.Client.CurrentPlayerId);
                Assert.AreEqual("refresh-newest", h.TokenStore.Load().RefreshToken,
                    "The server now holds only the newest refresh token, so the main sign-in saves and carries it.");
                h.Run(() => h.Client.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("shared-player", "newest"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));

                // Counter-case: a player no one here is signed in as is let in.
                h.Run(() => twin.Authentication.LoginWithDeviceAsync("device-three"));
                Assert.AreEqual("player-three", twin.PlayerId);
            }
        }

        [Test]
        public void TwoLocalPlayersAsOnePlayer_TheSecondIsRefused_TheFirstKeepsTheNewTokens()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"), SignedInAs("player-one", "one-newest"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer first = Add(h);
                FlockLocalPlayer second = Add(h);
                h.Run(() => first.Authentication.LoginWithDeviceAsync("device-one"));

                FlockAuthException refused = Assert.Throws<FlockAuthException>(() => h.Run(() => second.Authentication.LoginWithDeviceAsync("device-one")));

                Assert.AreEqual(FlockErrorCode.FlockPlayerAlreadySignedIn, refused.ErrorCode);
                Assert.IsFalse(second.IsSignedIn);
                Assert.IsNull(h.TokenStore.Load(), "Neither local sign-in is saved.");
                h.Run(() => first.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("player-one", "one-newest"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));
            }
        }

        [UnityTest]
        public IEnumerator ASignInAnsweredAfterRemovalDoesNotSignThePlayerBackIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("late-player", "late"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer fresh = Add(h);
                int signIns = 0;
                fresh.OnAuthenticated += _ => signIns++;
                transport.GateNext(FlockEndpoints.PlayerLoginDevice);
                Task<PlayerLoginResponse> signingIn = fresh.Authentication.LoginWithDeviceAsync("device-late");

                h.Client.RemoveLocalPlayer(fresh);
                transport.ReleaseGate();
                yield return FlockTestWait.UntilDone(signingIn);

                Assert.IsTrue(signingIn.IsFaulted);
                FlockAuthException late = signingIn.Exception.InnerException as FlockAuthException;
                Assert.IsNotNull(late);
                StringAssert.Contains("RemoveLocalPlayer", late.Message);
                Assert.IsFalse(fresh.IsSignedIn);
                Assert.IsNull(fresh.PlayerId);
                Assert.AreEqual(0, signIns);
            }
        }

        [UnityTest]
        public IEnumerator ASignInAsTheMainPlayerAnsweredAfterRemoval_StillHandsTheMainSignInTheNewTokens()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("shared-player", "newest"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("shared-player");
                FlockLocalPlayer twin = Add(h);
                transport.GateNext(FlockEndpoints.PlayerLoginDevice);
                Task<PlayerLoginResponse> signingIn = twin.Authentication.LoginWithDeviceAsync("device-shared");

                h.Client.RemoveLocalPlayer(twin);
                transport.ReleaseGate();
                yield return FlockTestWait.UntilDone(signingIn);

                StringAssert.Contains("RemoveLocalPlayer", signingIn.Exception.InnerException.Message);
                Assert.AreEqual("refresh-newest", h.TokenStore.Load().RefreshToken,
                    "The late answer still replaced the server's refresh token, so the main sign-in takes it.");
            }
        }

        [UnityTest]
        public IEnumerator RetriesStopOnceALocalPlayerIsRemoved([Values(true, false)] bool removed)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("player/name", FlockFakeTransport.Status(503, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport,
                       config => config.RetryPolicy = new RetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.FromMilliseconds(300), UseJitter = false }))
            {
                FlockLocalPlayer player = Add(h);
                Task<bool> asking = player.Authentication.IsNameAvailableAsync("sara");
                Assert.AreEqual(1, transport.CountTo("player/name"));

                if (removed)
                    h.Client.RemoveLocalPlayer(player);
                yield return FlockTestWait.Until(() => asking.IsCompleted, "The call finished.", 10f);

                if (removed)
                {
                    Assert.AreEqual(1, transport.CountTo("player/name"), "No retry goes out once the player is removed.");
                    StringAssert.Contains("RemoveLocalPlayer", asking.Exception.InnerException.Message);
                }
                else
                {
                    Assert.AreEqual(3, transport.CountTo("player/name"), "Control: the retries go out while the player is there.");
                }
            }
        }

        [UnityTest]
        public IEnumerator ARefreshAnsweredAfterTheTokensWereHandedOver_KeepsTheHandedOverTokens([Values(200, 401)] int refreshStatus)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"), SignedInAs("player-one", "handed"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, refreshStatus == 200
                ? SignedInAs("player-one", "from-refresh")
                : FlockFakeTransport.Coded(401, "player.invalid_refresh_token"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer first = Add(h);
                FlockLocalPlayer second = Add(h);
                h.Run(() => first.Authentication.LoginWithDeviceAsync("device-one"));

                transport.GateNext(FlockEndpoints.PlayerTokenRefresh);
                Task<bool> refreshing = first.Client.TryRefreshTokenAsync(CancellationToken.None);
                Assert.Throws<FlockAuthException>(() => h.Run(() => second.Authentication.LoginWithDeviceAsync("device-one")));
                Assert.IsFalse(refreshing.IsCompleted, "The refresh is still on its way when the tokens are handed over.");
                transport.ReleaseGate();
                yield return FlockTestWait.UntilDone(refreshing);

                Assert.IsTrue(refreshing.Result, "The refresh carries on with the handed-over tokens.");
                Assert.IsTrue(first.IsSignedIn, "The handed-over tokens are the server's now, so the sign-in carries on.");
                h.Run(() => first.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("player-one", "handed"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));
            }
        }

        [Test]
        public void ShuttingDownEndsEveryLocalPlayer_WithItsOwnCause()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer first = Add(h);
                h.Run(() => first.Authentication.LoginWithDeviceAsync("device-one"));

                FlockClient.Shutdown();

                Assert.IsFalse(first.IsSignedIn);
                Assert.AreEqual(0, h.Client.LocalPlayers.Count);
                FlockAuthException ended = Assert.Throws<FlockAuthException>(() => h.Run(() => first.Leaderboard.GetByNameAsync("Board")));
                StringAssert.Contains("shut down", ended.Message, "The cause is the shutdown, not a removal.");
                StringAssert.Contains("AddLocalPlayer", ended.Hint);
                Assert.IsFalse(h.Run(() => first.Authentication.TryRestoreSessionAsync()), "An ended local player has no sign-in to restore.");
                Assert.Throws<FlockException>(() => h.Client.AddLocalPlayer(), "A client that was shut down adds no local player.");
            }
        }

        [Test]
        public void ALocalPlayersRestoreLeavesTheMainPlayersFlagAndEventAlone()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-local", "local"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-main"); // saved, so a local restore that read the save would find it
                FlockLocalPlayer player = Add(h);
                int restores = 0;
                Action<bool> count = _ => restores++;
                FlockEvents.OnSessionRestored += count;
                try
                {
                    Assert.IsFalse(h.Run(() => player.Authentication.TryRestoreSessionAsync()), "A local player never restores the main player's saved sign-in.");
                    Assert.AreEqual(0, restores);
                    Assert.IsFalse(player.IsSignedIn);
                    // A local sign-in and sign-out leave the main player's saved sign-in alone too.
                    h.Run(() => player.Authentication.LoginWithDeviceAsync("device-local"));
                    player.Authentication.Logout();
                    Assert.AreEqual("refresh-player-main", h.TokenStore.Load()?.RefreshToken, "The main player's saved sign-in is untouched.");

                    // Counter-case: the main player's restore still finds it, and is raised.
                    Assert.IsTrue(h.Run(() => h.Client.Authentication.TryRestoreSessionAsync()));
                    Assert.AreEqual(1, restores);
                }
                finally
                {
                    FlockEvents.OnSessionRestored -= count;
                }
            }
        }

        [Test]
        public void ALocalPlayersOtherEventsAreTheirOwnToo()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"), SignedInAs("player-one", "again"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            transport.On("player/link", FlockFakeTransport.Ok(NoAccounts));
            transport.On("player/unlink", FlockFakeTransport.Ok(NoAccounts));
            transport.OnSequence(FlockEndpoints.PlayerTokenRefresh, SignedInAs("player-one", "renewed"),
                FlockFakeTransport.Coded(401, "player.invalid_refresh_token"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer player = Add(h);
                List<string> main = new List<string>();
                List<string> local = new List<string>();
                Action refreshed = () => main.Add("refreshed");
                Action expired = () => main.Add("expired");
                Action loggedOut = () => main.Add("logged out");
                Action<FlockCredentialProvider> linked = _ => main.Add("linked");
                Action<FlockCredentialProvider> unlinked = _ => main.Add("unlinked");
                FlockEvents.OnTokenRefreshed += refreshed;
                FlockEvents.OnAuthExpired += expired;
                FlockEvents.OnLoggedOut += loggedOut;
                FlockEvents.OnAccountLinked += linked;
                FlockEvents.OnAccountUnlinked += unlinked;
                player.OnTokenRefreshed += () => local.Add("refreshed");
                player.OnAuthExpired += () => local.Add("expired");
                player.OnLoggedOut += () => local.Add("logged out");
                player.OnAccountLinked += _ => local.Add("linked");
                player.OnAccountUnlinked += _ => local.Add("unlinked");
                try
                {
                    h.Run(() => player.Authentication.LoginWithDeviceAsync("device-one"));
                    h.Run(() => player.Authentication.LinkDeviceAsync("device-two"));
                    h.Run(() => player.Authentication.UnlinkAsync(FlockCredentialProvider.DeviceId));
                    Assert.IsTrue(h.Run(() => player.Client.TryRefreshTokenAsync(CancellationToken.None)));
                    player.Authentication.Logout();
                    h.Run(() => player.Authentication.LoginWithDeviceAsync("device-one"));
                    Assert.IsFalse(h.Run(() => player.Client.TryRefreshTokenAsync(CancellationToken.None)));

                    CollectionAssert.AreEqual(new[] { "linked", "unlinked", "refreshed", "logged out", "expired" }, local);
                    CollectionAssert.IsEmpty(main, "None of a local player's events reach FlockEvents.");
                }
                finally
                {
                    FlockEvents.OnTokenRefreshed -= refreshed;
                    FlockEvents.OnAuthExpired -= expired;
                    FlockEvents.OnLoggedOut -= loggedOut;
                    FlockEvents.OnAccountLinked -= linked;
                    FlockEvents.OnAccountUnlinked -= unlinked;
                }
            }
        }

        [Test]
        public void TwoSignInsAsOnePlayerAnsweredInTheOtherOrder_TheHolderFallsBackToItsOwnTokens([Values(true, false)] bool serverKeptTheHolders)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"), SignedInAs("player-one", "two"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            // The server keeps one refresh token: the holder's when it took the holder's sign-in last, else the one handed over.
            string live = serverKeptTheHolders ? "refresh-one" : "refresh-two";
            FlockHttpResponse renewed = SignedInAs("player-one", "renewed");
            transport.Default(request => request.Url.Contains(FlockEndpoints.PlayerTokenRefresh) && request.JsonBody.Contains("\"" + live + "\"")
                ? renewed
                : FlockFakeTransport.Coded(401, "player.invalid_refresh_token"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer first = Add(h);
                FlockLocalPlayer second = Add(h);
                h.Run(() => first.Authentication.LoginWithDeviceAsync("device-one"));
                Assert.Throws<FlockAuthException>(() => h.Run(() => second.Authentication.LoginWithDeviceAsync("device-one")));

                Assert.IsTrue(h.Run(() => first.Client.TryRefreshTokenAsync(CancellationToken.None)), "Whichever order the server took them in, the sign-in renews.");
                h.Run(() => first.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("player-one", "renewed"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));
                Assert.AreEqual(serverKeptTheHolders ? 2 : 1, transport.CountTo(FlockEndpoints.PlayerTokenRefresh),
                    "The tokens held before are tried only when the handed-over ones are refused.");
            }
        }

        [Test]
        public void AnOfflineSavedAnswerIsNotServedToARemovedLocalPlayer()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("probe/thing", FlockFakeTransport.Ok("{\"value\":\"saved\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer player = Add(h);
                FlockTestSnapshotProvider reads = new FlockTestSnapshotProvider(player.Client);
                Assert.AreEqual("saved", h.Run(() => reads.FetchAsync("c", "k", "probe/thing")).Value);
                player.Client.ReachabilityProbe = () => false;
                Assert.AreEqual("saved", h.Run(() => reads.FetchAsync("c", "k", "probe/thing")).Value, "Control: offline, the saved answer is served.");

                h.Client.RemoveLocalPlayer(player);

                FlockAuthException ended = Assert.Throws<FlockAuthException>(() => h.Run(() => reads.FetchAsync("c", "k", "probe/thing")));
                StringAssert.Contains("AddLocalPlayer", ended.Hint);
            }
        }

        [UnityTest]
        public IEnumerator ASignInAsTheMainPlayerAnsweredAfterShutdown_UpdatesTheSavedSignIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("shared-player", "newest"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("shared-player");
                FlockLocalPlayer twin = Add(h);
                transport.GateNext(FlockEndpoints.PlayerLoginDevice);
                Task<PlayerLoginResponse> signingIn = twin.Authentication.LoginWithDeviceAsync("device-shared");

                FlockClient.Shutdown();
                Assert.AreEqual("refresh-shared-player", h.TokenStore.Load().RefreshToken, "Shutdown keeps the saved sign-in.");
                transport.ReleaseGate();
                yield return FlockTestWait.UntilDone(signingIn);

                StringAssert.Contains("shut down", signingIn.Exception.InnerException.Message);
                Assert.AreEqual("refresh-newest", h.TokenStore.Load().RefreshToken,
                    "The server now takes only the newest token, so the next start restores that.");
            }
        }

        [Test]
        public void TheMainPlayerSigningInAsALocalPlayersPlayer_EndsThatLocalPlayerOnly()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.PlayerLoginDevice, SignedInAs("shared-player", "local"), SignedInAs("player-other", "other"),
                SignedInAs("shared-player", "main"));
            transport.On(FlockEndpoints.PlayerAccounts, FlockFakeTransport.Ok(NoAccounts));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer twin = Add(h);
                FlockLocalPlayer other = Add(h);
                h.Run(() => twin.Authentication.LoginWithDeviceAsync("device-shared"));
                h.Run(() => other.Authentication.LoginWithDeviceAsync("device-other"));
                int twinLoggedOut = 0;
                twin.OnLoggedOut += () => twinLoggedOut++;
                int sent = transport.Requests.Count;

                h.Run(() => h.Client.Authentication.LoginWithDeviceAsync("device-shared"));

                Assert.AreEqual("shared-player", h.Client.CurrentPlayerId, "The main sign-in goes through.");
                Assert.AreEqual(sent + 1, transport.Requests.Count, "Only the main sign-in is sent: nothing signs the player out on the server.");
                Assert.IsFalse(twin.IsSignedIn);
                Assert.AreEqual(1, twinLoggedOut);
                CollectionAssert.DoesNotContain(h.Client.LocalPlayers, twin);
                FlockAuthException ended = Assert.Throws<FlockAuthException>(() => h.Run(() => twin.Authentication.GetLinkedAccountsAsync()));
                StringAssert.Contains(FlockClient.LocalPlayerSignedInAsTheMainPlayerCause, ended.Message);
                StringAssert.Contains("Sign each player in once on a machine", ended.Hint);

                // Counter-case: a local player signed in as someone else is untouched.
                Assert.IsTrue(other.IsSignedIn);
                h.Run(() => other.Authentication.GetLinkedAccountsAsync());
                Assert.AreEqual("Bearer " + Token("player-other", "other"), BearerOfLast(transport, FlockEndpoints.PlayerAccounts));
            }
        }

        [Test]
        public void EachLocalPlayerAsksTheirOwnClientWhetherTheServerIsReachable()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one", "one"));
            transport.On(FlockEndpoints.CommandUpdatePlayerDataKey, FlockFakeTransport.Ok("{\"id\":\"row-1\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockLocalPlayer player = Add(h);
                h.Run(() => player.Authentication.LoginWithDeviceAsync("device-one"));
                h.SetReachable(false);

                h.Run(() => player.Commands.UpdatePlayerDataFieldAsync("row-1", "Score", 1));
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerDataKey), "The main client being offline does not hold the local player's write.");

                // Counter-case: the local player's own client being offline queues it.
                player.Client.ReachabilityProbe = () => false;
                h.Run(() => player.Commands.UpdatePlayerDataFieldAsync("row-1", "Score", 2));
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.CommandUpdatePlayerDataKey));
            }
        }

        [Test]
        public void ARemovedLocalPlayerIsFreed_AndOneStillAddedIsKept()
        {
            using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
            {
                WeakReference removed = AddThenMaybeRemove(h, remove: true);
                WeakReference kept = AddThenMaybeRemove(h, remove: false);
                // A few rounds: the collector can keep an object one round longer through a stale stack slot.
                for (int round = 0; round < 5 && removed.IsAlive; round++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                Assert.IsFalse(removed.IsAlive, "Nothing in the SDK holds a removed player.");
                Assert.IsTrue(kept.IsAlive, "Control: a player still added is held by the client.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AddThenMaybeRemove(FlockTestClient h, bool remove)
        {
            FlockLocalPlayer player = h.Client.AddLocalPlayer();
            if (remove)
                h.Client.RemoveLocalPlayer(player);
            return new WeakReference(player);
        }
    }
}
