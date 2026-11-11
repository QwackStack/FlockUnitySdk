using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // A signed-in player's config reads come back as the server resolves them for that player (segment and player overrides);
    // with nobody signed in the game's read is unchanged. The player's and the game's answers are the local backend's own,
    // captured verbatim (Owner-41's live run): A is in a segment with an override (lives 7) and has one of their own
    // (enemy_speed 2.5); B has neither. The game has no patch, so the patch answer is built to the patch schema.
    public class FlockPlayerConfigTests
    {
        private const string ConfigId = "01M74VYNA2ZCPX6TY2W9HSTB3Y";
        private const string PlayerRoute = "game_config/me/by-name/o41_difficulty";
        private const string ConfigByIdRoute = "game_config/" + ConfigId;
        private const string PatchesRoute = "game_patch/config/" + ConfigId;

        private const int PatchLives = 4;
        private const int ALives = 7;
        private const int BLives = 3;

        private static readonly string TokenA = FlockTestClient.MakeJwt("player-a", 3600, "a");
        private static readonly string TokenB = FlockTestClient.MakeJwt("player-b", 3600, "b");

        private class Difficulty
        {
            public int Lives { get; set; }
        }

        private const string PlayerAAnswer = "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":{\"id\":\"01M74VYNA2ZCPX6TY2W9HSTB3Y\",\"name\":\"o41_difficulty\",\"game_id\":\"01M5EM6PTHKSYB8ZFPSSRAJ7C2\",\"game_version_id\":\"01M5EM6PTY4M5PA1A93GQXQYDP\",\"schema\":[{\"type\":\"integer\",\"type_name\":\"difficulty\",\"field_name\":\"lives\"},{\"type\":\"float\",\"type_name\":\"difficulty\",\"field_name\":\"enemy_speed\"}],\"data\":[{\"type\":\"integer\",\"field_name\":\"lives\",\"type_name\":\"difficulty\",\"value\":7},{\"type\":\"float\",\"field_name\":\"enemy_speed\",\"type_name\":\"difficulty\",\"value\":2.5}],\"tag\":\"gameplay\",\"created_at\":\"2026-11-11T08:35:22.050969Z\",\"updated_at\":\"2026-11-11T08:35:22.050974Z\",\"patch_id\":null,\"override_ids\":[\"01M74VYQXNJFBNQDFPEM610DBZ\",\"01M74VYQYB469PKNGQQPPVDYV0\"]}}";
        private const string PlayerBAnswer = "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":{\"id\":\"01M74VYNA2ZCPX6TY2W9HSTB3Y\",\"name\":\"o41_difficulty\",\"game_id\":\"01M5EM6PTHKSYB8ZFPSSRAJ7C2\",\"game_version_id\":\"01M5EM6PTY4M5PA1A93GQXQYDP\",\"schema\":[{\"type\":\"integer\",\"type_name\":\"difficulty\",\"field_name\":\"lives\"},{\"type\":\"float\",\"type_name\":\"difficulty\",\"field_name\":\"enemy_speed\"}],\"data\":[{\"type\":\"integer\",\"value\":3,\"type_name\":\"difficulty\",\"field_name\":\"lives\"},{\"type\":\"float\",\"value\":1.5,\"type_name\":\"difficulty\",\"field_name\":\"enemy_speed\"}],\"tag\":\"gameplay\",\"created_at\":\"2026-11-11T08:35:22.050969Z\",\"updated_at\":\"2026-11-11T08:35:22.050974Z\",\"patch_id\":null,\"override_ids\":[]}}";
        private const string ConfigBody = "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":{\"id\":\"01M74VYNA2ZCPX6TY2W9HSTB3Y\",\"name\":\"o41_difficulty\",\"game_id\":\"01M5EM6PTHKSYB8ZFPSSRAJ7C2\",\"game_version_id\":\"01M5EM6PTY4M5PA1A93GQXQYDP\",\"schema\":[{\"type\":\"integer\",\"type_name\":\"difficulty\",\"field_name\":\"lives\"},{\"type\":\"float\",\"type_name\":\"difficulty\",\"field_name\":\"enemy_speed\"}],\"data\":[{\"type\":\"integer\",\"value\":3,\"type_name\":\"difficulty\",\"field_name\":\"lives\"},{\"type\":\"float\",\"value\":1.5,\"type_name\":\"difficulty\",\"field_name\":\"enemy_speed\"}],\"tag\":\"gameplay\",\"created_at\":\"2026-11-11T08:35:22.050969Z\",\"updated_at\":\"2026-11-11T08:35:22.050974Z\"}}";

        private const string PatchesBody = "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":[{\"id\":\"patch-1\",\"name\":\"November balance\"," +
                                           "\"game_config_id\":\"" + ConfigId + "\",\"data\":[{\"type\":\"integer\",\"value\":4,\"type_name\":\"difficulty\",\"field_name\":\"lives\"}," +
                                           "{\"type\":\"float\",\"value\":1.5,\"type_name\":\"difficulty\",\"field_name\":\"enemy_speed\"}]," +
                                           "\"created_at\":\"2026-11-06T10:00:00.000000Z\",\"updated_at\":\"2026-11-06T10:00:00.000000Z\"}]}";

        // The game's routes by URL; the player's route by the bearer it carries.
        private static FlockFakeTransport Transport()
        {
            return new FlockFakeTransport()
                .On(PatchesRoute, FlockFakeTransport.Ok(PatchesBody))
                .On(ConfigByIdRoute, FlockFakeTransport.Ok(ConfigBody))
                .Default(request =>
                {
                    if (!request.Url.Contains(PlayerRoute))
                        return FlockFakeTransport.Ok("{}");
                    string bearer = request.Headers != null && request.Headers.TryGetValue("Authorization", out string value) ? value : null;
                    if (bearer == "Bearer " + TokenA)
                        return FlockFakeTransport.Ok(PlayerAAnswer);
                    if (bearer == "Bearer " + TokenB)
                        return FlockFakeTransport.Ok(PlayerBAnswer);
                    return FlockFakeTransport.Coded(401, "player.missing_token");
                });
        }

        private static int Read(FlockTestClient sdk) => sdk.Run(() => sdk.Client.Config.GetByConfigIdAsync<Difficulty>(ConfigId)).Lives;

        private static void GoOffline(FlockTestClient sdk)
        {
            sdk.SetReachable(false);
            sdk.Transport.GoOffline();
        }

        private static void GoOnline(FlockTestClient sdk)
        {
            sdk.SetReachable(true);
            sdk.Transport.GoOnline();
        }

        private static List<string> ConfigWarnings(FlockTestClient sdk) => sdk.Logger.Warnings.FindAll(warning => warning.Contains("Config values"));

        private static IEnumerator Finished(Task task) => FlockTestWait.Until(() => task.IsCompleted, "The read finished once its answer was let through.");

        [Test]
        public void SignedIn_ReadsThePlayersRoute_AndGetsTheirOverride()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");

                Assert.AreEqual(ALives, Read(sdk), "The player's override, not the patch's value");

                FlockHttpRequest sent = sdk.Transport.LastTo(PlayerRoute);
                Assert.IsNotNull(sent, "The player's route was read");
                Assert.AreEqual("GET", sent.Method);
                Assert.AreEqual("https://test.invalid/v1/" + PlayerRoute, sent.Url, "Read by the config's name, which its id gave");
                Assert.AreEqual("Bearer " + TokenA, sent.Headers["Authorization"]);
                Assert.AreEqual("test-gvid", sent.Headers["X-Game-Version-ID"], "The build's game version names the version read");
                Assert.AreEqual("test-key", sent.Headers["X-Flock-API-Key"]);
                Assert.AreEqual(0, sdk.Transport.CountTo(PatchesRoute), "The game's patch read is not needed");

                Assert.AreEqual(ALives, Read(sdk));
                Assert.AreEqual(1, sdk.Transport.CountTo(PlayerRoute), "Kept for the sign-in: a second read sends nothing");
                Assert.AreEqual(1, sdk.Transport.CountTo(ConfigByIdRoute), "The name is asked once");
            }
        }

        [Test]
        public void ThePlayersRoute_EncodesTheConfigName()
        {
            // A space, a slash and a non-ASCII letter, each percent-encoded as UTF-8 so the name stays one path segment.
            Assert.AreEqual("game_config/me/by-name/hard%20mode%2F%C3%A9", FlockEndpoints.GameConfigForMeByName("hard mode/\u00e9"));
        }

        [Test]
        public void NobodySignedIn_ReadsTheGamesRoutes_AsBefore()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);

                Assert.AreEqual(PatchLives, Read(sdk), "The patch's value");

                Assert.AreEqual(0, sdk.Transport.CountTo("game_config/me/"), "No player route without a player");
                Assert.AreEqual(0, sdk.Transport.CountTo(ConfigByIdRoute), "A config with a patch needs no read of the config itself");
                FlockHttpRequest sent = sdk.Transport.LastTo(PatchesRoute);
                Assert.AreEqual("https://test.invalid/v1/" + PatchesRoute, sent.Url);
                Assert.IsFalse(sent.Headers.ContainsKey("Authorization"), "The game's read carries no sign-in");
                Assert.AreEqual("test-gvid", sent.Headers["X-Game-Version-ID"]);
            }
        }

        [Test]
        public void ASwitchFromAToB_NeverServesAsValues_FromMemoryOrDisk()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                Assert.AreEqual(PatchLives, Read(sdk), "Signed out first, so the game's values are on disk");

                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));

                sdk.LoginWithToken(TokenB, "refresh-b");
                GoOffline(sdk);
                int sentBefore = sdk.Transport.Requests.Count;
                Assert.AreEqual(PatchLives, Read(sdk), "B offline with no copy of their own reads the game's values, never A's");
                Assert.AreEqual(sentBefore, sdk.Transport.Requests.Count, "Offline: nothing sent");

                GoOnline(sdk);
                Assert.AreEqual(BLives, Read(sdk), "B online reads their own, not A's held value");
                Assert.AreEqual("Bearer " + TokenB, sdk.Transport.LastTo(PlayerRoute).Headers["Authorization"]);
            }
        }

        [Test]
        public void OfflineSignedIn_ServesThePlayersOwnCopy()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));

                // A new sign-in holds nothing in memory, so what answers offline is the copy on disk.
                sdk.LoginWithToken(TokenA, "refresh-a");
                GoOffline(sdk);
                int sentBefore = sdk.Transport.Requests.Count;
                Assert.AreEqual(ALives, Read(sdk), "A's own copy");
                Assert.AreEqual(sentBefore, sdk.Transport.Requests.Count, "Offline: nothing sent");
            }
        }

        [UnityTest]
        public IEnumerator ALateAnswerForA_IsKeptNowhere_AndTheReadAnswersForB()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                Assert.AreEqual(PatchLives, Read(sdk), "Signed out first, so the game's values are on disk");

                sdk.LoginWithToken(TokenA, "refresh-a");
                sdk.Transport.GateNext(PlayerRoute);
                Task<Difficulty> read = sdk.Client.Config.GetByConfigIdAsync<Difficulty>(ConfigId);
                sdk.LoginWithToken(TokenB, "refresh-b");
                sdk.Transport.ReleaseGate();
                yield return Finished(read);

                Assert.AreEqual(BLives, read.Result.Lives, "A's answer landed after B signed in, so the read answers for B");
                List<FlockHttpRequest> sent = sdk.Transport.AllTo(PlayerRoute);
                Assert.AreEqual(2, sent.Count);
                Assert.AreEqual("Bearer " + TokenA, sent[0].Headers["Authorization"]);
                Assert.AreEqual("Bearer " + TokenB, sent[1].Headers["Authorization"]);

                sdk.LoginWithToken(TokenA, "refresh-a");
                GoOffline(sdk);
                Assert.AreEqual(PatchLives, Read(sdk), "A's late answer was never written to A's copy on disk");
            }
        }

        [UnityTest]
        public IEnumerator RetriesStop_OnceThePlayerSignsOut()
        {
            int[] control = new int[2];
            yield return ReadWithAFailedFirstTry(false, control);
            Assert.AreEqual(1, control[0], "Control: the same failure with the player still signed in is retried");
            Assert.AreEqual(ALives, control[1], "Control: and the retry answers");

            int[] signedOut = new int[2];
            yield return ReadWithAFailedFirstTry(true, signedOut);
            Assert.AreEqual(0, signedOut[0], "Nothing re-sent for the player once they signed out");
            Assert.AreEqual(PatchLives, signedOut[1], "The read answers as for nobody signed in");
        }

        // A fresh client, so the player has no copy of their own and retries are on; the first try is held, then fails.
        // Outcome: [player reads sent once the held one was let go, lives read].
        private static IEnumerator ReadWithAFailedFirstTry(bool signOutWhileHeld, int[] outcome)
        {
            FlockFakeTransport transport = Transport();
            transport.OnSequence(PlayerRoute, FlockFakeTransport.Status(503, "{}"), FlockFakeTransport.Ok(PlayerAAnswer));
            using (FlockTestClient sdk = FlockTestClient.Create(transport,
                       config => config.RetryPolicy = new RetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero }))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                transport.GateNext(PlayerRoute);
                Task<Difficulty> read = sdk.Client.Config.GetByConfigIdAsync<Difficulty>(ConfigId);
                if (signOutWhileHeld)
                    sdk.Client.Authentication.Logout();
                int held = transport.CountTo(PlayerRoute);
                transport.ReleaseGate();
                yield return Finished(read);
                outcome[0] = transport.CountTo(PlayerRoute) - held;
                outcome[1] = read.Result.Lives;
            }
        }

        [TestCase("game_config.player_no_game_version")]
        [TestCase("game_config.player_not_found")]
        [TestCase("game_config.config_not_found")]
        public void ARefusalOfThePlayersRead_FallsBackToTheGamesRead_ForTheSignIn(string code)
        {
            FlockFakeTransport transport = Transport();
            transport.On(PlayerRoute, FlockFakeTransport.Coded(404, code));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                // No pause after no answer, so only a refusal remembered for the sign-in keeps the second read from being sent.
                sdk.Client.Config.PlayerReadPauseAfterNoAnswer = TimeSpan.Zero;
                sdk.LoginWithToken(TokenA, "refresh-a");

                Assert.AreEqual(PatchLives, Read(sdk), "The game's values, as before");
                Assert.AreEqual(PatchLives, Read(sdk));
                Assert.AreEqual(1, transport.CountTo(PlayerRoute), "Not asked again during this sign-in");
                Assert.AreEqual(1, transport.CountTo(PatchesRoute), "The game's read is kept as before");

                sdk.LoginWithToken(TokenA, "refresh-a");
                Read(sdk);
                Assert.AreEqual(2, transport.CountTo(PlayerRoute), "A new sign-in asks again");
            }
        }

        // Only "no Game Version" is something the developer can fix in the build, so it alone warns, once a sign-in however many configs fall back.
        [TestCase("game_config.player_no_game_version", 1)]
        [TestCase("game_config.player_not_found", 0)]
        [TestCase("game_config.config_not_found", 0)]
        public void OnlyNoGameVersion_WarnsOncePerSignIn(string code, int warningsPerSignIn)
        {
            FlockFakeTransport transport = Transport();
            transport.On("game_config/me/by-name/", FlockFakeTransport.Coded(404, code));
            transport.On("game_patch/config/cfg-speed", FlockFakeTransport.Ok(PatchesBody.Replace(ConfigId, "cfg-speed")));
            transport.On("game_config/cfg-speed", FlockFakeTransport.Ok(ConfigBody.Replace(ConfigId, "cfg-speed").Replace("\"o41_difficulty\"", "\"o41_speed\"")));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(PatchLives, Read(sdk));
                Assert.AreEqual(PatchLives, sdk.Run(() => sdk.Client.Config.GetByConfigIdAsync<Difficulty>("cfg-speed")).Lives);
                Assert.AreEqual(2, transport.CountTo("game_config/me/by-name/"), "Two configs fell back");
                Assert.AreEqual(warningsPerSignIn, ConfigWarnings(sdk).Count, "Warned " + warningsPerSignIn + " time(s) for the sign-in");

                sdk.LoginWithToken(TokenA, "refresh-a");
                Read(sdk);
                Assert.AreEqual(2 * warningsPerSignIn, ConfigWarnings(sdk).Count, "Again for the next sign-in");
                if (warningsPerSignIn > 0)
                {
                    StringAssert.Contains("no Game Version", ConfigWarnings(sdk)[0], "Names the cause");
                    StringAssert.Contains("Flock > Settings > Configuration", ConfigWarnings(sdk)[0], "Names the fix");
                }
            }
        }

        // The server resolves only gameplay and feature configs for a player; any other comes back as stored, without its patch.
        [TestCase("currency", false)]
        [TestCase("achievement", false)]
        [TestCase("asset", false)]
        [TestCase("feature", true)]
        public void OnlyGameplayAndFeatureConfigs_AreReadAsThePlayer(string tag, bool asThePlayer)
        {
            FlockFakeTransport transport = Transport();
            transport.On(ConfigByIdRoute, FlockFakeTransport.Ok(ConfigBody.Replace("\"tag\":\"gameplay\"", "\"tag\":\"" + tag + "\"")));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(asThePlayer ? ALives : PatchLives, Read(sdk), asThePlayer ? "The player's values" : "The patch's values, as signed out");
                Read(sdk);
                Assert.AreEqual(asThePlayer ? 1 : 0, transport.CountTo(PlayerRoute));
                Assert.AreEqual(1, transport.CountTo(ConfigByIdRoute), "The tag is read once");
            }
        }

        // A codeless 404 (a backend without the route) or a 403 will not change during the sign-in, so it is not sent again.
        [TestCase(404)]
        [TestCase(403)]
        public void APermanentFailure_IsNotSentAgainDuringTheSignIn(int status)
        {
            FlockFakeTransport transport = Transport();
            transport.On(PlayerRoute, FlockFakeTransport.Status(status, "{\"detail\":\"Not Found\"}"));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                // No pause after no answer, so only a failure remembered for the sign-in keeps the second read from being sent.
                sdk.Client.Config.PlayerReadPauseAfterNoAnswer = TimeSpan.Zero;
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(PatchLives, Read(sdk));
                Assert.AreEqual(PatchLives, Read(sdk));
                Assert.AreEqual(1, transport.CountTo(PlayerRoute), "Remembered for the sign-in, not merely paused");
            }
        }

        [Test]
        public void NoAnswer_IsNotSentAgainUntilThePauseEnds()
        {
            FlockFakeTransport transport = Transport();
            transport.On(PlayerRoute, FlockFakeTransport.Status(503, "{}"));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(PatchLives, Read(sdk));
                Assert.AreEqual(PatchLives, Read(sdk));
                Assert.AreEqual(1, transport.CountTo(PlayerRoute), "Within the pause the game's values answer at once");

                sdk.Client.Config.PlayerReadPauseAfterNoAnswer = TimeSpan.Zero;
                sdk.LoginWithToken(TokenA, "refresh-a");
                Read(sdk);
                Read(sdk);
                Assert.AreEqual(3, transport.CountTo(PlayerRoute), "Sent again once the pause is over");
            }
        }

        [Test]
        public void OfflineReads_ReadThePlayersSavedCopyOnce()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));

                sdk.LoginWithToken(TokenA, "refresh-a");
                GoOffline(sdk);
                Assert.AreEqual(ALives, Read(sdk));
                sdk.Client.SnapshotStore.DeleteScope("test-gvid/config");
                Assert.AreEqual(ALives, Read(sdk), "Held in memory once read: the file is not read again");
            }
        }

        [Test]
        public void AnAnswerForAnotherConfig_IsNotUsed()
        {
            FlockFakeTransport transport = Transport();
            transport.On(PlayerRoute, FlockFakeTransport.Ok(PlayerAAnswer.Replace(ConfigId, "01M74VYNA2ZCPX6TY2W9HSOTHR")));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(PatchLives, Read(sdk), "Another config's values are not this one's");
                Read(sdk);
                Assert.AreEqual(1, transport.CountTo(PlayerRoute), "Not asked again during the sign-in");
            }
        }

        [TestCase("refused")]
        [TestCase("another config")]
        public void ASavedCopyTheServerNoLongerStandsBehind_IsDeleted(string why)
        {
            FlockFakeTransport transport = Transport();
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));

                transport.On(PlayerRoute, why == "refused"
                    ? FlockFakeTransport.Coded(404, "game_config.config_not_found")
                    : FlockFakeTransport.Ok(PlayerAAnswer.Replace(ConfigId, "01M74VYNA2ZCPX6TY2W9HSOTHR")));
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(PatchLives, Read(sdk));

                sdk.LoginWithToken(TokenA, "refresh-a");
                GoOffline(sdk);
                Assert.AreEqual(PatchLives, Read(sdk), "A later offline read does not serve the deleted copy");
            }
        }

        [Test]
        public void AnIdNoConfigHas_FailsAsBefore_WhenSignedIn()
        {
            FlockFakeTransport transport = Transport();
            transport.On(ConfigByIdRoute, FlockFakeTransport.Coded(404, "game_config.config_not_found"));
            transport.On(PatchesRoute, FlockFakeTransport.Coded(404, "game_patch.game_config_not_found"));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");

                FlockException refused = Assert.Catch<FlockException>(() => Read(sdk));
                Assert.AreEqual(FlockErrorCode.GamePatchGameConfigNotFound, refused.ErrorCode, "The same failure as a player signed out gets");
                Assert.AreEqual(0, transport.CountTo(PlayerRoute), "No name, so no player read");
            }
        }

        [Test]
        public void NoAnswer_ServesThePlayersOwnCopy_ElseTheGamesRead()
        {
            FlockFakeTransport transport = Transport();
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));

                transport.On(PlayerRoute, FlockFakeTransport.Status(503, "{}"));
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk), "A's own copy when the server does not answer");

                sdk.LoginWithToken(TokenB, "refresh-b");
                Assert.AreEqual(PatchLives, Read(sdk), "B has no copy, so the game's values");
            }
        }

        // The player's route answering 200 with the envelope's result null: no answer, not a refusal, so it is tried again and asked
        // again after the pause, the player's own copy or the game's read answering meanwhile.
        private const string NoResult = "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":null}";

        [Test]
        public void AnAnswerWithNoResult_IsNoAnswer()
        {
            FlockFakeTransport transport = Transport();
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));

                transport.On(PlayerRoute, FlockFakeTransport.Ok(NoResult));
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk), "A's own copy, as when the server does not answer");
                Assert.AreEqual(ALives, Read(sdk));
                Assert.AreEqual(2, transport.CountTo(PlayerRoute), "Within the pause it is not sent again");

                // With no pause, each read asks again; a refusal would be held for the sign-in and sent once.
                sdk.Client.Config.PlayerReadPauseAfterNoAnswer = TimeSpan.Zero;
                sdk.LoginWithToken(TokenA, "refresh-a");
                Read(sdk);
                Read(sdk);
                Assert.AreEqual(4, transport.CountTo(PlayerRoute), "Sent again on each read, not held for the sign-in");

                sdk.LoginWithToken(TokenB, "refresh-b");
                Assert.AreEqual(PatchLives, Read(sdk), "B has no copy, so the game's values");
            }
        }

        [Test]
        public void AnAnswerWithNoResult_IsTriedAgain_WithNoCopyOfThePlayers()
        {
            FlockFakeTransport transport = Transport();
            transport.OnSequence(PlayerRoute, FlockFakeTransport.Ok(NoResult), FlockFakeTransport.Ok(PlayerAAnswer));
            using (FlockTestClient sdk = FlockTestClient.Create(transport,
                       config => config.RetryPolicy = new RetryPolicy { MaxRetries = 1, InitialDelay = TimeSpan.Zero }))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk), "The retry answers");
                Assert.AreEqual(2, transport.CountTo(PlayerRoute), "Tried again");
            }
        }

        [Test]
        public void ClearCacheAndEachNewSignIn_ReadAfresh()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                Read(sdk);
                Read(sdk);
                Assert.AreEqual(1, sdk.Transport.CountTo(PlayerRoute));

                sdk.Client.Config.ClearCache();
                Read(sdk);
                Assert.AreEqual(2, sdk.Transport.CountTo(PlayerRoute), "ClearCache forgets the player's values");

                sdk.Client.Authentication.Logout();
                Assert.AreEqual(PatchLives, Read(sdk), "Signed out: the game's values");
                sdk.LoginWithToken(TokenA, "refresh-a");
                Assert.AreEqual(ALives, Read(sdk));
                Assert.AreEqual(3, sdk.Transport.CountTo(PlayerRoute), "Signing in again reads afresh");
            }
        }

        [UnityTest]
        public IEnumerator AnAnswerOnItsWayWhenClearCacheRan_IsKeptNowhere()
        {
            using (FlockTestClient sdk = FlockTestClient.Create(Transport()))
            {
                sdk.SetReachable(true);
                sdk.LoginWithToken(TokenA, "refresh-a");
                sdk.Transport.GateNext(PlayerRoute);
                Task<Difficulty> read = sdk.Client.Config.GetByConfigIdAsync<Difficulty>(ConfigId);
                sdk.Client.Config.ClearCache();
                sdk.Transport.ReleaseGate();
                yield return Finished(read);
                Assert.AreEqual(ALives, read.Result.Lives, "Still the answer for the player who asked");

                // ClearCache also emptied the game's copies, so offline there is nothing to answer with unless the answer was kept.
                GoOffline(sdk);
                Assert.Catch<FlockNetworkException>(() => Read(sdk), "Kept neither in memory nor on disk");

                GoOnline(sdk);
                Assert.AreEqual(ALives, Read(sdk));
                Assert.AreEqual(2, sdk.Transport.CountTo(PlayerRoute), "Read again once online");
            }
        }
    }
}
