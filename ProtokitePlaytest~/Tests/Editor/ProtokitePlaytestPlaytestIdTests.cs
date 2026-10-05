using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Config;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>The Playtest ID: the build keeps its own Game Version for Flock, and only the playtest's requests to Protokite carry the playtest's version.</summary>
    public class ProtokitePlaytestPlaytestIdTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string EndRoute = "/end";
        private const string FlockSessionRoute = "/analytics/sessions";
        private const string ServerSessionId = "01K5SRVSESSION000000000001";
        private const string VersionHeader = "X-Game-Version-ID";
        private const string ByName = "/v1/game_version/by-name/";
        private const string ById = "/v1/game_version";

        private static readonly string TestId = ProtokitePlaytestSetupChecksTests.TestId;
        private static readonly string PlaytestVersionName = ProtokitePlaytestSetupChecksTests.PlaytestVersionName;
        private static readonly string PlaytestVersionId = ProtokitePlaytestSetupChecksTests.PlaytestVersionId;
        private static readonly string ReleaseVersionId = ProtokitePlaytestSetupChecksTests.ReleaseVersionId;
        private static readonly string OtherTestId = ProtokitePlaytestSetupChecksTests.Id("5A");
        private static readonly string OtherPlaytestVersionId = ProtokitePlaytestSetupChecksTests.Id("5B");

        private readonly List<Object> _made = new List<Object>();
        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;

        // This game's versions by name, and every game's by ID: Flock names an ID of any game, and resolves a name in this game only.
        private Dictionary<string, string> _thisGamesVersions;
        private Dictionary<string, string> _everyGamesVersions;

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytestIdResolver.ForgetForTesting();
            // An open Inspector or setup window would otherwise ask the resolver about the project's settings mid-test.
            ProtokitePlaytestIdResolver.ViewsWaitForTesting = true;
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_playtest_id_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            ProtokitePlaytest.FeedbackFormsFolderForTesting = Path.Combine(_folder, "FeedbackForms");
            _thisGamesVersions = new Dictionary<string, string> { { PlaytestVersionName, PlaytestVersionId }, { "1.0.0", ReleaseVersionId } };
            _everyGamesVersions = new Dictionary<string, string>
            {
                { PlaytestVersionId, PlaytestVersionName }, { ReleaseVersionId, "1.0.0" },
                { OtherPlaytestVersionId, "pt-" + OtherTestId }
            };
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytestIdResolver.ForgetForTesting();
            ProtokitePlaytestIdResolver.ViewsWaitForTesting = false;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.FeedbackFormsFolderForTesting = null;
            _settings.Dispose();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            foreach (Object made in _made)
                Object.DestroyImmediate(made);
            _made.Clear();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static string ConfigFor(string versionId)
            => "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"" + versionId + "\",\"features\":{},\"form\":null}}";

        private static FlockFakeTransport Protokite(string configVersionId)
            => new FlockFakeTransport()
                .On(EndRoute, FlockFakeTransport.Status(204, ""))
                .On(StartRoute, FlockFakeTransport.Ok("{\"result\":{\"session_id\":\"pk-1\"}}"))
                .On(ConfigRoute, FlockFakeTransport.Ok(ConfigFor(configVersionId)))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"));

        private void ChoosePlaytest(string playtestId, string resolvedFrom, string resolvedVersionId, string withFlockSettings = "")
        {
            _settings.Settings.PlaytestId = playtestId;
            _settings.Settings.KeepResolvedPlaytestVersion(resolvedFrom, resolvedVersionId, withFlockSettings);
        }

        // The fingerprint of the project's own Flock settings, which a build is checked against.
        private static string ProjectsFlockSettings()
        {
            FlockConfigAsset flock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            Assert.IsNotNull(flock, "Precondition: the project has Flock settings");
            return ProtokitePlaytestIdLookup.FlockSettingsFingerprint(flock.apiUrl, flock.apiKey);
        }

        // A Flock session that reached the server, the way a game's sign-in makes one; then the playtest follows.
        private static void StartAFlockSession(FlockTestClient flock)
        {
            flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            flock.Run(() => flock.Client.Analytics.StartSessionAsync());
            ProtokitePlaytest.Refresh();
        }

        private static IEnumerator TheStartSettles()
        {
            float until = Time.realtimeSinceStartup + 5f;
            while (ProtokitePlaytest.SessionState == ProtokitePlaytestSessionState.Starting && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.AreNotEqual(ProtokitePlaytestSessionState.Starting, ProtokitePlaytest.SessionState, "The start settled within 5 s");
        }

        // ---- In the game

        [UnityTest]
        public IEnumerator OnlyThePlaytestsRequestsCarryThePlaytestIdsVersion()
        {
            ChoosePlaytest(TestId, TestId, PlaytestVersionId);
            using (FlockTestClient flock = FlockTestClient.Create(Protokite(PlaytestVersionId)))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the config loaded");
                FlockHttpRequest config = flock.Transport.LastTo(ConfigRoute);
                Assert.AreEqual(PlaytestVersionId, config.Headers[VersionHeader], "Protokite is asked for the Playtest ID's playtest");
                Assert.AreEqual("test-key", config.Headers["X-Flock-API-Key"], "With the Flock SDK's own key");

                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId);
                Assert.AreEqual(PlaytestVersionId, flock.Transport.LastTo(StartRoute).Headers[VersionHeader], "The session goes to the same playtest");
                Assert.AreEqual("test-gvid", flock.Transport.LastTo(FlockSessionRoute).Headers[VersionHeader], "The Flock SDK keeps its own Game Version");
                Assert.AreEqual("test-gvid", flock.Client.GetGameHeaders()[VersionHeader], "and the headers it hands out are its own");
            }
        }

        [UnityTest]
        public IEnumerator APlaytestIdChangedAfterThePlaytestLoadedLeavesTheSessionOnTheLoadedOne()
        {
            ChoosePlaytest(TestId, TestId, PlaytestVersionId);
            using (FlockTestClient flock = FlockTestClient.Create(Protokite(PlaytestVersionId)))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the config loaded");

                // Changed in Play Mode, and resolved, before the game's sign-in starts a Flock session.
                ChoosePlaytest(OtherTestId, OtherTestId, OtherPlaytestVersionId);
                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual(PlaytestVersionId, flock.Transport.LastTo(StartRoute).Headers[VersionHeader], "The session goes to the playtest the config was loaded for");
            }
        }

        [TestCase("01KX00000000000000000005A0", "01KX000000000000000000007E", "01KX00000000000000000000AB", "resolved for another Playtest ID")]
        [TestCase("01KX000000000000000000007E", "01KX000000000000000000007E", "", "resolved to none")]
        [TestCase("01kx000000000000000000007e", "01KX000000000000000000007E", "01KX00000000000000000000AB", "resolved for the same ID in other letters")]
        [TestCase("01KX000000000000000000007E", "", "", "never resolved")]
        public void APlaytestIdNotResolvedAsksForNoPlaytest(string typed, string resolvedFrom, string resolvedVersionId, string why)
        {
            ChoosePlaytest(typed, resolvedFrom, resolvedVersionId);
            using (FlockTestClient flock = FlockTestClient.Create(Protokite(PlaytestVersionId)))
            {
                LogAssert.Expect(LogType.Warning, new Regex("Playtest ID in Protokite > Playtest > Settings has not been resolved.*Playtest ID is '" + Regex.Escape(typed) + "'"));
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();

                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestIdNotResolved, ProtokitePlaytest.Status, why);
                Assert.AreEqual(0, flock.Transport.CountTo(ConfigRoute), "Neither the Flock SDK's version nor none is sent instead: " + why);
                LogAssert.NoUnexpectedReceived();
            }
        }

        [Test]
        public void AnEmptyPlaytestIdAsksForTheNewestPlaytest()
        {
            ChoosePlaytest("", "", "");
            using (FlockTestClient flock = FlockTestClient.Create(Protokite("newest-gvid")))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Assert.IsFalse(flock.Transport.LastTo(ConfigRoute).Headers.ContainsKey(VersionHeader));
            }
        }

        [UnityTest]
        public IEnumerator WithNoPlaytestIdTheNewestPlaytestIsAskedForAndKeptForTheLaunch()
        {
            // Spaces only, and a resolution left over from an earlier Playtest ID, change nothing once the field is cleared.
            ChoosePlaytest("   ", TestId, PlaytestVersionId);
            using (FlockTestClient flock = FlockTestClient.Create(Protokite("newest-gvid")))
            {
                LogAssert.Expect(LogType.Log, new Regex("Playtesting is ready: .*It is the game's newest playtest, as Playtest ID is empty"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "The newest playtest's answer is taken, whatever its version");
                Assert.IsFalse(flock.Transport.LastTo(ConfigRoute).Headers.ContainsKey(VersionHeader), "No version, so Protokite answers with the game's newest playtest");
                Assert.AreEqual("1.0.0", flock.Client.GameVersion, "Precondition: the game's own Game Version, not a playtest's");

                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual("newest-gvid", flock.Transport.LastTo(StartRoute).Headers[VersionHeader],
                    "The session goes to the playtest that answered, so one created meanwhile never takes the rest of the launch");
                Assert.AreEqual("test-gvid", flock.Transport.LastTo(FlockSessionRoute).Headers[VersionHeader], "The Flock SDK keeps its own");
            }
        }

        [Test]
        public void WithNoPlaytestIdAPlaytestsGameVersionNamesItsPlaytest()
        {
            ChoosePlaytest("", "", "");
            using (FlockTestClient flock = FlockTestClient.Create(Protokite("test-gvid"), config => config.GameVersion = PlaytestVersionName))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Assert.AreEqual("test-gvid", flock.Transport.LastTo(ConfigRoute).Headers[VersionHeader], "A pt- Game Version still names its playtest, as before 1.68.0");
            }
        }

        [Test]
        public void ANewestPlaytestAnswerIsNotTakenForAVersionThatWasSent()
        {
            // Control for the rule above: once a version is sent, an answer for another is still refused (a proxy dropping the header).
            ChoosePlaytest(TestId, TestId, PlaytestVersionId);
            using (FlockTestClient flock = FlockTestClient.Create(Protokite("newest-gvid")))
            {
                LogAssert.Expect(LogType.Warning, new Regex("different Game Version ID than this build sent"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestConfigForAnotherVersion, ProtokitePlaytest.Status);
            }
        }

        [Test]
        public void AnUnresolvedPlaytestIdIsReportedBeforeWaitingForFlock()
        {
            ChoosePlaytest(TestId, "", "");
            Assert.AreEqual(ProtokitePlaytestStatus.PlaytestIdNotResolved,
                ProtokitePlaytest.StatusFor(_settings.Settings, false, ProtokitePlaytestConsentChoice.VideoAndPlayData), "A setting to fix is said before anything is waited for");
            string described = ProtokitePlaytest.Describe(ProtokitePlaytestStatus.PlaytestIdNotResolved);
            StringAssert.Contains("Playtest ID", described);
            StringAssert.Contains("Protokite > Playtest > Settings", described, "Naming where it is fixed");

            _settings.Settings.PlaytestingEnabled = false;
            Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.StatusFor(_settings.Settings, true, ProtokitePlaytestConsentChoice.VideoAndPlayData),
                "Playtesting off says so first");
        }

        [Test]
        public void TheVersionAPlaytestIdResolvedToCountsOnlyForThatId()
        {
            ProtokitePlaytestSettings settings = _settings.Settings;
            ChoosePlaytest(" " + TestId + " ", " " + TestId + " ", PlaytestVersionId);
            Assert.AreEqual(PlaytestVersionId, settings.PlaytestVersionId, "Resolved for the value as it was typed, spaces and all");
            ChoosePlaytest(TestId, TestId, PlaytestVersionId + " ");
            Assert.IsNull(settings.PlaytestVersionId, "A version ID holding whitespace is not one");
            ChoosePlaytest(TestId, TestId, PlaytestVersionId + "X");
            Assert.IsNull(settings.PlaytestVersionId, "Nor is one longer than an ID");
            ChoosePlaytest("", "", PlaytestVersionId);
            Assert.IsNull(settings.PlaytestVersionId, "No Playtest ID chooses no version");
        }

        // ---- Finding the playtest's version

        private FlockFakeTransport UseFlock()
        {
            FlockFakeTransport flock = new FlockFakeTransport().Default(AnswerAsFlock);
            FlockHttpClient.Configure(flock);
            return flock;
        }

        private FlockHttpResponse AnswerAsFlock(FlockHttpRequest request)
        {
            // Two keys of this one game, as a studio's development and release keys can be.
            if (!request.Headers.TryGetValue("X-Flock-API-Key", out string key) || (key != "key-1" && key != "key-2"))
                return FlockFakeTransport.Coded(401, "game.invalid_api_key");
            int byName = request.Url.IndexOf(ByName, StringComparison.Ordinal);
            if (byName >= 0)
            {
                string name = Uri.UnescapeDataString(request.Url.Substring(byName + ByName.Length));
                return _thisGamesVersions.TryGetValue(name, out string id) ? Version(id, name) : FlockFakeTransport.Coded(404, "game_version.game_version_by_name_not_found");
            }
            if (request.Url.EndsWith(ById, StringComparison.Ordinal))
            {
                request.Headers.TryGetValue(VersionHeader, out string id);
                return id != null && _everyGamesVersions.TryGetValue(id, out string name) ? Version(id, name) : FlockFakeTransport.Coded(404, "game_version.game_version_not_found");
            }
            return FlockFakeTransport.Status(404, "{\"detail\":\"Not Found\"}");
        }

        // The route's own body, copied off the local Flock (2026-10-05).
        private static FlockHttpResponse Version(string id, string name)
            => FlockFakeTransport.Ok("{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":{\"id\":\"" + id + "\",\"name\":\"" + name +
                                     "\",\"release_type\":\"preview\",\"env\":\"dev\",\"features\":{\"heavy_analytics\":true,\"video_recording\":true,\"exception_capturing\":true}," +
                                     "\"created_at\":\"2026-09-16T09:49:59.975742\",\"updated_at\":\"2026-09-16T09:49:59.975742\"}}");

        private static ProtokitePlaytestIdAnswer Ask(string playtestId, string apiKey = "key-1")
        {
            Task<ProtokitePlaytestIdAnswer> asking = ProtokitePlaytestIdLookup.AskAsync("https://flock.example.com/", apiKey, playtestId, CancellationToken.None);
            Assert.IsTrue(asking.IsCompleted, "Precondition: the fake answered at once");
            return asking.Result;
        }

        [TestCase("01KX000000000000000000007E", "the test's ID")]
        [TestCase("01kx000000000000000000007e", "in lower case, as Flock keeps IDs in upper")]
        [TestCase(" 01KX000000000000000000007E\n", "with spaces and a line break around it, as a copy from a page brings")]
        [TestCase("pt-01KX000000000000000000007E", "as the version's name")]
        [TestCase("PT-01kx000000000000000000007e", "as the version's name in other letters")]
        public void ATestsIdIsFoundByTheNameProtokiteGivesItsVersion(string pasted, string how)
        {
            FlockFakeTransport flock = UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(pasted);
            Assert.IsNull(answer.Problem, how);
            Assert.AreEqual(PlaytestVersionId, answer.VersionId, how);
            Assert.AreEqual(PlaytestVersionName, answer.VersionName);
            Assert.AreEqual(1, flock.Requests.Count, "One question is enough: " + how);
            Assert.AreEqual("https://flock.example.com/v1/game_version/by-name/" + PlaytestVersionName, flock.Requests[0].Url);
            Assert.AreEqual("key-1", flock.Requests[0].Headers["X-Flock-API-Key"]);
        }

        [Test]
        public void TheVersionIdTheSdkBlockShowsIsNamedAndResolvedBack()
        {
            FlockFakeTransport flock = UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(PlaytestVersionId.ToLowerInvariant());
            Assert.IsNull(answer.Problem);
            Assert.AreEqual(PlaytestVersionId, answer.VersionId);
            Assert.AreEqual(PlaytestVersionName, answer.VersionName);
            FlockHttpRequest byId = flock.Requests.Single(request => request.Url.EndsWith(ById, StringComparison.Ordinal));
            Assert.AreEqual(PlaytestVersionId, byId.Headers[VersionHeader], "Asked about in upper case");
            Assert.AreEqual(1, flock.Requests.Count(request => request.Url.EndsWith(ByName + PlaytestVersionName, StringComparison.Ordinal)), "Its name was resolved back");
        }

        [Test]
        public void AnotherGamesPlaytestVersionIsRefused()
        {
            UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(OtherPlaytestVersionId);
            Assert.IsNull(answer.Problem);
            Assert.IsNull(answer.VersionId, "Flock names an ID of any game; its name is no version of this game");
            StringAssert.Contains("of another game", answer.WhyNoPlaytest);
        }

        [Test]
        public void AnotherGamesVersionWhoseNameThisGameAlsoHasIsRefused()
        {
            // The round trip, not the name alone: this game has its own version of the same name, with another ID.
            UseFlock();
            string othersCopy = ProtokitePlaytestSetupChecksTests.Id("5C");
            _everyGamesVersions[othersCopy] = PlaytestVersionName;
            ProtokitePlaytestIdAnswer answer = Ask(othersCopy);
            Assert.IsNull(answer.VersionId);
            StringAssert.Contains("of another game", answer.WhyNoPlaytest);
        }

        [Test]
        public void AReleasesVersionIdIsRefused()
        {
            UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(ReleaseVersionId);
            Assert.IsNull(answer.VersionId);
            StringAssert.Contains("named '1.0.0', which is not a playtest's version", answer.WhyNoPlaytest);
        }

        [Test]
        public void AnIdNoPlaytestOrVersionHasIsRefused()
        {
            UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(ProtokitePlaytestSetupChecksTests.Id("42"));
            Assert.IsNull(answer.Problem, "A 404 the route sends is an answer, not a problem");
            Assert.IsNull(answer.VersionId);
            StringAssert.Contains("No playtest of this game has the ID", answer.WhyNoPlaytest);
        }

        [TestCase("01KX0000000000000000007E")]
        [TestCase("01KX0000 000000000000007E")]
        [TestCase("https://protokite.example.com/games/x/tests/01KX000000000000000000007E")]
        public void WhatIsNotShapedLikeAnIdIsRefusedWithoutAsking(string pasted)
        {
            FlockFakeTransport flock = UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(pasted);
            Assert.IsNull(answer.VersionId);
            StringAssert.Contains("is not an ID from Protokite", answer.WhyNoPlaytest);
            Assert.IsEmpty(flock.Requests);
        }

        [Test]
        public void A404ThatIsNotTheRoutesOwnIsAProblemNotANo()
        {
            FlockFakeTransport flock = new FlockFakeTransport().Default(request => FlockFakeTransport.Status(404, "{\"detail\":\"Not Found\"}"));
            FlockHttpClient.Configure(flock);
            ProtokitePlaytestIdAnswer answer = Ask(TestId);
            StringAssert.StartsWith("Flock answered HTTP 404.", answer.Problem, "A wrong API URL says nothing about the playtest");
            Assert.IsNull(answer.WhyNoPlaytest);
            Assert.AreEqual(1, flock.Requests.Count, "Nothing more is asked once one question failed");
        }

        [Test]
        public void ARefusedKeyIsAProblemNotANo()
        {
            FlockFakeTransport flock = UseFlock();
            ProtokitePlaytestIdAnswer answer = Ask(TestId, apiKey: "wrong-key");
            Assert.AreEqual("Flock refused the API key in Flock > Settings (HTTP 401).", answer.Problem);
            Assert.AreEqual("wrong-key", flock.Requests[0].Headers["X-Flock-API-Key"], "The counter-case sent the wrong key, and was refused for it");
        }

        // ---- Keeping what Flock found in the settings

        private (ProtokitePlaytestSettings, FlockConfigAsset) Project(string playtestId, string apiKey = "key-1")
        {
            ProtokitePlaytestSettings settings = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
            FlockConfigAsset flock = ScriptableObject.CreateInstance<FlockConfigAsset>();
            _made.Add(settings);
            _made.Add(flock);
            settings.PlaytestId = playtestId;
            flock.apiUrl = "https://flock.example.com";
            flock.apiKey = apiKey;
            flock.gameVersion = "1.0.0";
            flock.gameVersionId = ReleaseVersionId;
            return (settings, flock);
        }

        [Test]
        public void APlaytestIdIsResolvedAndKeptInTheSettings()
        {
            UseFlock();
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.AreEqual(PlaytestVersionId, settings.PlaytestVersionId);
            Assert.AreEqual(TestId, settings.ResolvedFromPlaytestId);
            Assert.IsTrue(ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock), "With a fingerprint of the Flock settings asked with");
            StringAssert.DoesNotContain("key-1", settings.ResolvedWithFlockSettings, "Never the key itself");
            Assert.AreEqual(ReleaseVersionId, flock.gameVersionId, "The Flock settings are not touched");
            Assert.AreEqual("1.0.0", flock.gameVersion);
        }

        [Test]
        public void AnotherFlockKeyIsAnotherResolution()
        {
            FlockFakeTransport transport = UseFlock();
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.IsTrue(ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock), "Precondition: resolved with this key");

            // Another key of the same game finds the same version, and the resolution is then this key's.
            flock.apiKey = "key-2";
            Assert.IsFalse(ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock), "Precondition: kept for the other key");
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.AreEqual(2, transport.Requests.Count, "Asked again with the new key");
            Assert.AreEqual(PlaytestVersionId, settings.PlaytestVersionId);
            Assert.IsTrue(ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock), "The same version, kept with the new key's fingerprint");

            // A key Flock refuses: the version kept is not this key's, whatever was kept before.
            flock.apiKey = "wrong-key";
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.IsNotNull(ProtokitePlaytestIdResolver.Answer.Problem, "Precondition: refused");
            Assert.IsFalse(ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock), "A refusal never makes the old version this key's");
        }

        [Test]
        public void ViewsResolveOnlyTheGamesSettingsAndNothingWhileATestOwnsTheResolver()
        {
            FlockFakeTransport transport = UseFlock();
            FlockConfigAsset projectsFlock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            (ProtokitePlaytestSettings another, FlockConfigAsset flock) = Project(TestId);
            ProtokitePlaytestIdResolver.ViewsWaitForTesting = false;
            ProtokitePlaytestIdResolver.ResolveForAView(another, flock);
            Assert.IsEmpty(transport.Requests, "Settings no build loads are never resolved");

            _settings.Settings.PlaytestId = TestId;
            ProtokitePlaytestIdResolver.ViewsWaitForTesting = true;
            ProtokitePlaytestIdResolver.ResolveForAView(_settings.Settings, projectsFlock);
            Assert.IsEmpty(transport.Requests, "Nothing while a test owns the resolver");

            ProtokitePlaytestIdResolver.ViewsWaitForTesting = false;
            ProtokitePlaytestIdResolver.ResolveForAView(_settings.Settings, projectsFlock);
            Assert.AreEqual(1, transport.Requests.Count, "Control: the game's own settings are resolved");
            ProtokitePlaytestIdResolver.ViewsWaitForTesting = true;
        }

        [Test]
        public void ThePlaytestIdIsAskedAboutOnceUntilAskedAgainOrChanged()
        {
            FlockFakeTransport transport = UseFlock();
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.AreEqual(1, transport.Requests.Count, "A redraw asks nothing");
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock, askAgain: true);
            Assert.AreEqual(2, transport.Requests.Count, "Resolve Again asks again");
            flock.apiKey = "wrong-key";
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.AreEqual(3, transport.Requests.Count, "Another API key is another question");
        }

        [Test]
        public void NothingIsAskedWithoutAPlaytestIdOrAKey()
        {
            FlockFakeTransport transport = UseFlock();
            (ProtokitePlaytestSettings blank, FlockConfigAsset flock) = Project("  ");
            ProtokitePlaytestIdResolver.ResolveIfChanged(blank, flock);
            (ProtokitePlaytestSettings settings, FlockConfigAsset noKey) = Project(TestId, apiKey: " ");
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, noKey);
            Assert.IsEmpty(transport.Requests);
            Assert.IsFalse(ProtokitePlaytestIdResolver.IsAsking);
        }

        [Test]
        public void FlockOutOfReachKeepsWhatWasResolvedBefore()
        {
            FlockFakeTransport transport = UseFlock();
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            settings.KeepResolvedPlaytestVersion(TestId, PlaytestVersionId);
            transport.GoOffline();
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            StringAssert.StartsWith("Flock could not be reached.", ProtokitePlaytestIdResolver.Answer.Problem, "Precondition: Flock was out of reach");
            Assert.AreEqual(PlaytestVersionId, settings.PlaytestVersionId, "An editor offline does not undo a resolved Playtest ID");
        }

        [Test]
        public void AnIdThatFindsNoPlaytestClearsWhatWasResolvedBefore()
        {
            UseFlock();
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(ProtokitePlaytestSetupChecksTests.Id("42"));
            settings.KeepResolvedPlaytestVersion(settings.PlaytestId, PlaytestVersionId);
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Assert.IsNull(settings.PlaytestVersionId, "Flock said no playtest has it, so a build asks for none");
            Assert.AreEqual(settings.PlaytestId, settings.ResolvedFromPlaytestId, "and that answer is kept for this Playtest ID");
        }

        [UnityTest]
        public IEnumerator AnAnswerLandingAfterThePlaytestIdChangedIsNotKept()
        {
            FlockFakeTransport transport = UseFlock();
            transport.GateNext(ByName + PlaytestVersionName);
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
            Task held = ProtokitePlaytestIdResolver.LastQuestionForTesting;
            Assert.IsTrue(ProtokitePlaytestIdResolver.IsAsking, "Precondition: the answer is held");

            // Changed with nothing drawn since, so no newer question was asked.
            settings.PlaytestId = OtherTestId;
            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => held.IsCompleted, "the held answer landed");
            Assert.AreEqual("", settings.ResolvedFromPlaytestId, "Nothing is kept for an ID the settings no longer hold");
            Assert.AreEqual("", settings.ResolvedPlaytestVersionId);
        }

        [UnityTest]
        public IEnumerator AnAnswerToAQuestionANewerOneReplacedIsDropped()
        {
            FlockFakeTransport transport = UseFlock();
            transport.GateNext(ByName + PlaytestVersionName);
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            int answered = 0;
            Action count = () => answered++;
            ProtokitePlaytestIdResolver.Answered += count;
            try
            {
                ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
                Task held = ProtokitePlaytestIdResolver.LastQuestionForTesting;
                settings.PlaytestId = ReleaseVersionId;
                ProtokitePlaytestIdResolver.ResolveIfChanged(settings, flock);
                string newer = ProtokitePlaytestIdLookup.KeyFor(settings, flock);
                Assert.AreEqual(newer, ProtokitePlaytestIdResolver.Answer?.AskedFor, "Precondition: the newer question was answered");

                transport.ReleaseGate();
                yield return FlockTestWait.Until(() => held.IsCompleted, "the held answer landed");
                Assert.AreEqual(1, answered, "The older answer, landing last, is not taken");
                Assert.AreEqual(newer, ProtokitePlaytestIdResolver.Answer.AskedFor);
            }
            finally
            {
                ProtokitePlaytestIdResolver.Answered -= count;
            }
        }

        [Test]
        public void TheInspectorSaysWhatFlockFound()
        {
            (ProtokitePlaytestSettings settings, FlockConfigAsset flock) = Project(TestId);
            string key = ProtokitePlaytestIdLookup.KeyFor(settings, flock);

            string fingerprint = ProtokitePlaytestIdLookup.FlockSettingsFingerprint(flock.apiUrl, flock.apiKey);

            (string empty, MessageType emptyKind) = ProtokitePlaytestIdFieldDrawer.WhatToSay(Project("").Item1, flock, false, null, true);
            StringAssert.Contains("joins the game's newest playtest", empty, "Saying what an empty Playtest ID does");
            Assert.AreEqual(MessageType.Info, emptyKind);

            (string elsewhere, MessageType elsewhereKind) = ProtokitePlaytestIdFieldDrawer.WhatToSay(settings, flock, false, null, false);
            StringAssert.Contains("not the playtest settings a build loads", elsewhere);
            Assert.AreEqual(MessageType.Warning, elsewhereKind);

            settings.KeepResolvedPlaytestVersion(TestId, PlaytestVersionId, fingerprint);
            string found = ProtokitePlaytestIdFieldDrawer.WhatToSay(settings, flock, false,
                new ProtokitePlaytestIdAnswer { AskedFor = key, VersionId = PlaytestVersionId, VersionName = PlaytestVersionName }, true).Item1;
            StringAssert.Contains(PlaytestVersionName, found);
            StringAssert.Contains("Game Version '1.0.0'", found, "Saying the game keeps its own");

            (string offline, MessageType offlineKind) = ProtokitePlaytestIdFieldDrawer.WhatToSay(settings, flock, false,
                new ProtokitePlaytestIdAnswer { AskedFor = key, Problem = "Flock could not be reached." }, true);
            StringAssert.Contains("resolved to before, " + PlaytestVersionId, offline, "Saying what a build still asks for");
            Assert.AreEqual(MessageType.Warning, offlineKind);

            settings.KeepResolvedPlaytestVersion(TestId, PlaytestVersionId, "another environment");
            string otherFlock = ProtokitePlaytestIdFieldDrawer.WhatToSay(settings, flock, false, null, true).Item1;
            StringAssert.Contains("with other Flock settings", otherFlock, "A resolution made with another URL or key is said to be one");
            StringAssert.Contains("is refused", otherFlock);

            settings.KeepResolvedPlaytestVersion(TestId, "", fingerprint);
            (string none, MessageType noneKind) = ProtokitePlaytestIdFieldDrawer.WhatToSay(settings, flock, false,
                new ProtokitePlaytestIdAnswer { AskedFor = key, WhyNoPlaytest = "No playtest of this game has the ID X." }, true);
            StringAssert.StartsWith("No playtest of this game has the ID X.", none);
            StringAssert.Contains("playtesting stays off", none);
            Assert.AreEqual(MessageType.Error, noneKind);
        }

        [Test]
        public void TheSetupWindowSaysWhetherFlockWasAsked()
        {
            ProtokitePlaytestSettings settings = Project(TestId).Item1;
            ProtokitePlaytestSetupInput input = ProtokitePlaytestSetupInput.From(settings, Project(TestId).Item2, BuildTarget.StandaloneWindows64, "");
            string key = ProtokitePlaytestIdLookup.KeyFor(input.FlockApiUrl, input.FlockApiKey, input.PlaytestId);
            Assert.AreEqual("Checking with Flock...", ProtokitePlaytestWindow.PlaytestIdQuestionSaid(input, null, true));
            Assert.AreEqual("Not checked with Flock in this editor session yet.",
                ProtokitePlaytestWindow.PlaytestIdQuestionSaid(input, new ProtokitePlaytestIdAnswer { AskedFor = key + "x" }, false), "An answer for other settings");
            Assert.AreEqual("Could not check with Flock: down.",
                ProtokitePlaytestWindow.PlaytestIdQuestionSaid(input, new ProtokitePlaytestIdAnswer { AskedFor = key, Problem = "down." }, false));
            Assert.AreEqual("Checked with Flock.", ProtokitePlaytestWindow.PlaytestIdQuestionSaid(input, new ProtokitePlaytestIdAnswer { AskedFor = key }, false));
        }

        // ---- Building

        [Test]
        public void ABuildWithPlaytestingOnAndAnUnresolvedPlaytestIdIsRefused()
        {
            ChoosePlaytest(TestId, "", "");
            BuildFailedException refused = Assert.Throws<BuildFailedException>(() => new ProtokitePlaytestBuildCheck().OnPreprocessBuild(null));
            StringAssert.Contains($"Playtest ID '{TestId}' has not been resolved", refused.Message);

            ChoosePlaytest(TestId, TestId, PlaytestVersionId, "another environment");
            refused = Assert.Throws<BuildFailedException>(() => new ProtokitePlaytestBuildCheck().OnPreprocessBuild(null));
            StringAssert.Contains("was resolved with other Flock settings", refused.Message, "Resolved with another URL or key: refused");

            ChoosePlaytest(TestId, TestId, PlaytestVersionId, ProjectsFlockSettings());
            Assert.DoesNotThrow(() => new ProtokitePlaytestBuildCheck().OnPreprocessBuild(null), "Resolved with the project's own Flock settings: built");
        }

        [Test]
        public void OnlyPlaytestingOnWithAPlaytestIdCanRefuseABuild()
        {
            ProtokitePlaytestSettings settings = _settings.Settings;
            FlockConfigAsset flock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            ChoosePlaytest(TestId, "", "");
            Assert.IsNotNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(settings, flock), "Control: these settings are refused");
            settings.PlaytestingEnabled = false;
            Assert.IsNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(settings, flock), "Playtesting off builds");
            ChoosePlaytest(TestId, TestId, PlaytestVersionId, "another environment");
            Assert.IsNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(settings, flock), "Playtesting off builds whatever the resolution");
            settings.PlaytestingEnabled = true;
            Assert.IsNotNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(settings, flock), "Control: resolved with other Flock settings is refused");
            Assert.IsNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(settings, null), "With no Flock settings there is nothing to compare");
            ChoosePlaytest(" ", "", "");
            Assert.IsNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(settings, flock), "No Playtest ID builds, as before");
            Assert.IsNull(ProtokitePlaytestBuildCheck.WhyTheBuildIsRefused(null, flock), "No settings build");
        }

        // ---- The package's own source

        [Test]
        public void ThePackageNeverSavesEveryAsset()
        {
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly).resolvedPath;
            string[] sources = new[] { "Runtime", "Editor" }.SelectMany(folder => Directory.GetFiles(Path.Combine(package, folder), "*.cs", SearchOption.AllDirectories)).ToArray();
            Assert.IsTrue(sources.Any(path => File.ReadAllText(path).Contains("AssetDatabase.SaveAssetIfDirty(")), "Control: the scan reads the files that save");
            string[] savingEverything = sources.Where(path => File.ReadAllText(path).Contains("AssetDatabase.SaveAssets(")).Select(Path.GetFileName).ToArray();
            Assert.IsEmpty(savingEverything, "Saving every asset writes the developer's other unsaved edits too");
        }
    }
}
