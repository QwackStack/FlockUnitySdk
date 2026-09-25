using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestSessionTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string EndRoute = "/end";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string FlockSessionRoute = "/analytics/sessions";
        private const string ServerSessionId = "01K5SRVSESSION000000000001";

        private const string Config =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{},\"form\":null}}";

        private static string StartAnswer(string sessionId) => "{\"result\":{\"session_id\":\"" + sessionId + "\"}}";

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private static string _realDeviceIdStamp;

        [OneTimeSetUp]
        public void RecordTheRealDeviceIdFile() => _realDeviceIdStamp = Stamp(ProtokitePlaytestDeviceIdFile.DefaultPath);

        [OneTimeTearDown]
        public void TheRealDeviceIdFileIsUntouched()
            => Assert.AreEqual(_realDeviceIdStamp, Stamp(ProtokitePlaytestDeviceIdFile.DefaultPath), "A test read or wrote the game's own device id file");

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_session_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            _settings.Dispose();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static string Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks + ":" + File.ReadAllText(path) : "none";

        private static FlockFakeTransport Transport(FlockHttpResponse start)
            => new FlockFakeTransport()
                .On(EndRoute, FlockFakeTransport.Status(204, ""))
                .On(StartRoute, start)
                .On(ConfigRoute, FlockFakeTransport.Ok(Config))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"));

        // A Flock session that reached the server, the way a game's sign-in makes one; then the playtest follows.
        private static void StartAFlockSession(FlockTestClient flock)
        {
            flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            flock.Run(() => flock.Client.Analytics.StartSessionAsync());
            ProtokitePlaytest.Refresh();
        }

        // The start goes out on a background thread and is settled on the main one, so a test waits frames for it.
        private static IEnumerator TheStartSettles()
        {
            float until = Time.realtimeSinceStartup + 5f;
            while (ProtokitePlaytest.SessionState == ProtokitePlaytestSessionState.Starting && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.AreNotEqual(ProtokitePlaytestSessionState.Starting, ProtokitePlaytest.SessionState, "The start settled within 5 s");
        }

        private static JObject StartBody(FlockFakeTransport transport) => JObject.Parse(transport.LastTo(StartRoute).JsonBody);

        // Starting

        [UnityTest]
        public IEnumerator NothingStartsUntilAFlockSessionReachesTheServer()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                LogAssert.Expect(LogType.Log, new Regex("starts once a Flock session reaches the server.*Analytics Auto Start Session"));
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the config loaded");
                Assert.IsNull(flock.Client.ServerSessionId);
                Assert.AreEqual(0, flock.Transport.CountTo(StartRoute));

                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId);
            }
        }

        [UnityTest]
        public IEnumerator AFlockSessionThatOnlyRunsLocallyStartsNothing()
        {
            FlockFakeTransport transport = new FlockFakeTransport()
                .On(StartRoute, FlockFakeTransport.Ok(StartAnswer("pk-1")))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config))
                .On(FlockSessionRoute, FlockFakeTransport.Offline());
            using (FlockTestClient flock = FlockTestClient.Create(transport))
            {
                StartAFlockSession(flock);
                Assert.IsNotNull(flock.Client.CurrentSessionId, "Precondition: a Flock session runs, with only its local id");
                for (int frame = 0; frame < 5; frame++)
                {
                    ProtokitePlaytest.Refresh();
                    yield return null;
                }
                Assert.AreEqual(0, transport.CountTo(StartRoute), "Protokite needs the server's id, so nothing starts on the local one");
            }
        }

        [UnityTest]
        public IEnumerator TheStartNamesTheServersFlockSessionAndCarriesTheDeviceIdAndBuildFacts()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                flock.LoginAs("player-1");
                StartAFlockSession(flock);
                yield return TheStartSettles();

                FlockHttpRequest sent = flock.Transport.LastTo(StartRoute);
                Assert.AreEqual("http://protokite.test/game/sdk/playtest-session", sent.Url);
                Assert.AreEqual("POST", sent.Method);
                Assert.AreEqual("test-key", sent.Headers["X-Flock-API-Key"]);
                Assert.AreEqual("test-gvid", sent.Headers["X-Game-Version-ID"]);
                Assert.IsFalse(sent.Headers.ContainsKey("Authorization"), "Protokite never sees the player's sign-in");

                JObject body = StartBody(flock.Transport);
                Assert.AreEqual(ServerSessionId, (string)body["flock_session_id"], "The server's id, never the local one");
                Assert.AreEqual(File.ReadAllText(ProtokitePlaytest.DeviceIdFilePathForTesting), (string)body["device_id"]);
                Assert.IsNull(body["steam_id"]);
                Assert.IsNull(body["player_name"], "Left out, not sent empty");
                JObject facts = (JObject)body["extra_debug"];
                Assert.AreEqual(Application.unityVersion, (string)facts["engine_version"]);
                Assert.AreEqual("Editor", (string)facts["build_configuration"]);
                Assert.AreEqual(ProtokitePlaytestVersion.Current, (string)facts["sdk_version"]);
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId);
            }
        }

        [UnityTest]
        public IEnumerator OneSessionPerLaunchAcrossSignOutSignInANewFlockSessionAndAFlockRestart()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                flock.LoginAs("player-1");
                StartAFlockSession(flock);
                yield return TheStartSettles();

                flock.Client.Authentication.Logout();
                flock.LoginAs("player-2");
                flock.Run(() => flock.Client.Analytics.EndSessionAsync());
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());
                ProtokitePlaytest.Refresh();
                yield return null;
                Assert.AreEqual(1, flock.Transport.CountTo(StartRoute));
            }

            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-2")))))
            {
                StartAFlockSession(flock);
                yield return null;
                Assert.AreEqual(0, flock.Transport.CountTo(StartRoute), "A Flock restart is still the same launch");
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId);
            }
        }

        [UnityTest]
        public IEnumerator AFailedStartIsNeverSentAgain()
        {
            foreach (int status in new[] { 503, 422, 500 })
            {
                ProtokitePlaytest.ResetForNewLaunch();
                using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Status(status, "{}")), config =>
                    config.RetryPolicy = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero }))
                {
                    LogAssert.Expect(LogType.Warning, new Regex("No Protokite session was started, and none is tried again"));
                    StartAFlockSession(flock);
                    yield return TheStartSettles();
                    flock.Run(() => flock.Client.Analytics.EndSessionAsync());
                    flock.Run(() => flock.Client.Analytics.StartSessionAsync());
                    ProtokitePlaytest.Refresh();
                    yield return null;

                    Assert.AreEqual(1, flock.Transport.CountTo(StartRoute), $"HTTP {status}: a start that reached Protokite may already have made a session");
                    Assert.IsNull(ProtokitePlaytest.PlaytestSessionId);
                }
            }
        }

        [UnityTest]
        public IEnumerator AnAnswerWithoutAUsableSessionIdIsAFailedStart()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk 1")))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("No Protokite session was started"));
                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual(ProtokitePlaytestSessionState.StartFailed, ProtokitePlaytest.SessionState);
                Assert.IsNull(ProtokitePlaytest.PlaytestSessionId);
            }
        }

        [UnityTest]
        public IEnumerator AClosedPlaytestTurnsPlaytestingOffForTheLaunch()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Status(400, "{\"detail\":\"This playtest is not launchable right now\"}"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("This playtest has closed"));
                StartAFlockSession(flock);
                yield return TheStartSettles();

                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestNoLongerCollecting, ProtokitePlaytest.Status);
                Assert.IsNull(ProtokitePlaytest.Config);
                Assert.AreEqual(1, flock.Transport.CountTo(StartRoute));

                ProtokitePlaytest.ResetForNewLaunch();
                Assert.AreNotEqual(ProtokitePlaytestStatus.PlaytestNoLongerCollecting, ProtokitePlaytest.Status, "The next launch asks again");
            }
        }

        [UnityTest]
        public IEnumerator AFlockSessionIdProtokiteCannotTakeIsLeftOut()
        {
            FlockFakeTransport transport = new FlockFakeTransport()
                .On(StartRoute, FlockFakeTransport.Ok(StartAnswer("pk-1")))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"01K5SRVSESSION000000000001X\"}"));
            using (FlockTestClient flock = FlockTestClient.Create(transport))
            {
                LogAssert.Expect(LogType.Warning, new Regex("cannot be sent to Protokite"));
                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.IsNull(StartBody(transport)["flock_session_id"]);
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId, "The session still starts");
            }
        }

        // Identity

        [UnityTest]
        public IEnumerator TheDeviceIdSurvivesARelaunch()
        {
            string first;
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                StartAFlockSession(flock);
                yield return TheStartSettles();
                first = (string)StartBody(flock.Transport)["device_id"];
            }
            Assert.IsTrue(ProtokitePlaytestDeviceIdFile.IsDeviceId(first), "A lower-case GUID");

            ProtokitePlaytest.ResetForNewLaunch();
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-2")))))
            {
                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual(first, (string)StartBody(flock.Transport)["device_id"]);
            }
        }

        [UnityTest]
        public IEnumerator ASteamIdSetBeforeTheStartIsSentInsteadOfTheDeviceId()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                Assert.IsTrue(ProtokitePlaytest.SetSteamId("76561198000000001", "Duck Player"));
                StartAFlockSession(flock);
                yield return TheStartSettles();

                JObject body = StartBody(flock.Transport);
                Assert.AreEqual("76561198000000001", (string)body["steam_id"]);
                Assert.AreEqual("Duck Player", (string)body["player_name"]);
                Assert.IsNull(body["device_id"]);
                Assert.IsFalse(File.Exists(ProtokitePlaytest.DeviceIdFilePathForTesting), "No device id is made when it is not needed");
            }
        }

        [UnityTest]
        public IEnumerator ASteamIdThatIsEmptyOrHoldsWhitespaceIsRefusedNotTrimmedAndTheDeviceIdIsSent()
        {
            foreach (string steamId in new[] { "76561198000000001\n", " 76561198000000001", "", new string('7', 65) })
            {
                ProtokitePlaytest.ResetForNewLaunch();
                using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
                {
                    Assert.IsTrue(ProtokitePlaytest.SetSteamId("76561198000000002"), "Precondition: an earlier good id");
                    LogAssert.Expect(LogType.Warning, new Regex("was refused.*device id is sent instead"));
                    Assert.IsFalse(ProtokitePlaytest.SetSteamId(steamId));
                    StartAFlockSession(flock);
                    yield return TheStartSettles();

                    JObject body = StartBody(flock.Transport);
                    Assert.IsNull(body["steam_id"], $"'{steamId}' refused, and the earlier id forgotten");
                    Assert.IsTrue(ProtokitePlaytestDeviceIdFile.IsDeviceId((string)body["device_id"]));
                }
            }
        }

        [UnityTest]
        public IEnumerator APlayerNameTooLongForProtokiteIsLeftOut()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                Assert.IsTrue(ProtokitePlaytest.SetSteamId("76561198000000001", new string('d', 201)));
                StartAFlockSession(flock);
                yield return TheStartSettles();
                JObject body = StartBody(flock.Transport);
                Assert.AreEqual("76561198000000001", (string)body["steam_id"], "The id still goes");
                Assert.IsNull(body["player_name"], "Left out rather than cut short");
            }
        }

        [UnityTest]
        public IEnumerator ASteamIdSetAfterTheStartChangesNothing()
        {
            using (FlockTestClient flock = FlockTestClient.Create(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")))))
            {
                StartAFlockSession(flock);
                yield return TheStartSettles();
                LogAssert.Expect(LogType.Warning, new Regex("after this launch's Protokite session started"));
                Assert.IsFalse(ProtokitePlaytest.SetSteamId("76561198000000001"));
            }
        }

        [Test]
        public void ADeviceIdFileHoldingSomethingElseIsReplacedAndAnUnreadableOneIsLeftAlone()
        {
            string path = ProtokitePlaytest.DeviceIdFilePathForTesting;
            Directory.CreateDirectory(_folder);
            ProtokitePlaytestDeviceIdFile file = new ProtokitePlaytestDeviceIdFile(path);

            File.WriteAllText(path, Guid.NewGuid().ToString("D").ToUpperInvariant());
            Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Replaced, file.ReadOrCreate(out string replaced), "Upper case is not a device id this package writes");
            Assert.AreEqual(replaced, File.ReadAllText(path));

            File.WriteAllText(path, replaced + "\n");
            Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Replaced, file.ReadOrCreate(out _), "A trailing newline is not a device id either");

            string kept = File.ReadAllText(path);
            Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Read, file.ReadOrCreate(out string read));
            Assert.AreEqual(kept, read);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Unreadable, file.ReadOrCreate(out string none));
                Assert.AreEqual("", none);
            }
            Assert.AreEqual(kept, File.ReadAllText(path), "An unreadable file is never replaced");
        }

        [Test]
        public void OnlyTemporaryFilesOlderThanAMinuteAreSwept()
        {
            string path = ProtokitePlaytest.DeviceIdFilePathForTesting;
            Directory.CreateDirectory(_folder);
            string old = path + ".aaaa.tmp";
            string fresh = path + ".bbbb.tmp";
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(fresh, "y");

            new ProtokitePlaytestDeviceIdFile(path).ReadOrCreate(out _);

            Assert.IsFalse(File.Exists(old), "A crashed launch's leftover");
            Assert.IsTrue(File.Exists(fresh), "Another launch may be saving right now");
        }

        [Test]
        public void TwoLaunchesReadingTheIdEndWithTheOneTheFileHolds()
        {
            string path = ProtokitePlaytest.DeviceIdFilePathForTesting;
            new ProtokitePlaytestDeviceIdFile(path).ReadOrCreate(out string a);
            new ProtokitePlaytestDeviceIdFile(path).ReadOrCreate(out string b);
            Assert.AreEqual(a, b);
            Assert.AreEqual(a, File.ReadAllText(path));
            Assert.AreEqual(0, Directory.GetFiles(_folder, "*.tmp").Length, "No temporary file is left behind");
        }

        // Ending

        [UnityTest]
        public IEnumerator QuittingEndsTheSessionOnceWithTheHeadersItStartedWith()
        {
            FlockFakeTransport transport = Transport(FlockFakeTransport.Ok(StartAnswer("pk/1")));
            using (FlockTestClient flock = FlockTestClient.Create(transport))
            {
                StartAFlockSession(flock);
                yield return TheStartSettles();
            }
            // Flock shut down first, as it may at quit: the end still goes out, with the start's headers.
            FlockHttpClient.Configure(transport);
            LogAssert.Expect(LogType.Log, new Regex("Protokite session pk/1 ended"));
            ProtokitePlaytest.HandleGameQuitting();

            FlockHttpRequest end = transport.LastTo(EndRoute);
            Assert.IsNotNull(end, "The end was sent");
            Assert.AreEqual("http://protokite.test/game/sdk/playtest-session/pk%2F1/end", end.Url);
            Assert.AreEqual("test-key", end.Headers["X-Flock-API-Key"]);
            Assert.AreEqual("test-gvid", end.Headers["X-Game-Version-ID"]);
            Assert.IsNull(ProtokitePlaytest.PlaytestSessionId);

            ProtokitePlaytest.HandleGameQuitting();
            Assert.AreEqual(1, transport.CountTo(EndRoute), "Ended once");
        }

        [Test]
        public void QuittingWithNoSessionSendsNothing()
        {
            FlockFakeTransport transport = Transport(FlockFakeTransport.Ok(StartAnswer("pk-1")));
            using (FlockTestClient.Create(transport))
            {
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.HandleGameQuitting();
                Assert.AreEqual(0, transport.CountTo(EndRoute));
            }
        }

        [UnityTest]
        public IEnumerator AStartAnsweredAfterTheLaunchEndedIsEndedAtOnce()
        {
            HeldStart transport = new HeldStart(Transport(FlockFakeTransport.Ok(StartAnswer("pk-late"))));
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockHttpClient.Configure(transport);
                StartAFlockSession(flock);
                Assert.AreEqual(ProtokitePlaytestSessionState.Starting, ProtokitePlaytest.SessionState, "Precondition: the start is held");

                // A new Play with domain reload off: the old launch is over while its start is held.
                ProtokitePlaytest.ResetForNewLaunch();
                transport.Release();
                float until = Time.realtimeSinceStartup + 5f;
                while (transport.Inner.CountTo(EndRoute) == 0 && Time.realtimeSinceStartup < until)
                    yield return null;

                Assert.AreEqual(1, transport.Inner.CountTo(EndRoute), "The session the old launch got is ended, not left in progress");
                StringAssert.Contains("/pk-late/end", transport.Inner.LastTo(EndRoute).Url);
                Assert.IsNull(ProtokitePlaytest.PlaytestSessionId, "It is not this launch's session");
            }
        }

        [UnityTest]
        public IEnumerator QuittingWhileTheStartIsOnItsWayWaitsForItAndEndsItOnce()
        {
            HeldStart transport = new HeldStart(Transport(FlockFakeTransport.Ok(StartAnswer("pk-held"))));
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockHttpClient.Configure(transport);
                StartAFlockSession(flock);
                transport.ReleaseAfter(TimeSpan.FromMilliseconds(300));
                LogAssert.Expect(LogType.Log, new Regex("Protokite session pk-held ended"));
                ProtokitePlaytest.HandleGameQuitting();
                Assert.AreEqual(1, transport.Inner.CountTo(EndRoute));

                // The start's own completion lands afterwards, and must not end the session a second time.
                for (int frame = 0; frame < 20; frame++)
                    yield return null;
                Assert.AreEqual(1, transport.Inner.CountTo(EndRoute), "Ended once");
            }
        }

        [UnityTest]
        public IEnumerator QuittingGivesUpOnAnEndThatDoesNotAnswerWithinTheBound()
        {
            HeldStart transport = new HeldStart(Transport(FlockFakeTransport.Ok(StartAnswer("pk-1"))), holdEnds: true);
            transport.Release();
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockHttpClient.Configure(transport);
                StartAFlockSession(flock);
                yield return TheStartSettles();
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId, "Precondition");

                LogAssert.Expect(LogType.Warning, new Regex("could not be ended within 3 s"));
                DateTime began = DateTime.UtcNow;
                ProtokitePlaytest.HandleGameQuitting();
                double waited = (DateTime.UtcNow - began).TotalSeconds;
                Assert.That(waited, Is.InRange(2.5, 4.0), "Bounded: the game is never held up for good");
            }
        }

        [UnityTest]
        public IEnumerator NothingMoreIsSentOnceQuittingHasGivenUp()
        {
            // An end Protokite keeps failing, so the Flock SDK's retries keep sending it.
            FlockFakeTransport transport = new FlockFakeTransport()
                .On(EndRoute, FlockFakeTransport.Status(503, "{}"))
                .On(StartRoute, FlockFakeTransport.Ok(StartAnswer("pk-1")))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"));
            using (FlockTestClient flock = FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy
            {
                MaxRetries = 10, InitialDelay = TimeSpan.FromMilliseconds(800), BackoffMultiplier = 1, UseJitter = false
            }))
            {
                StartAFlockSession(flock);
                yield return TheStartSettles();

                LogAssert.Expect(LogType.Warning, new Regex("could not be ended within 3 s"));
                ProtokitePlaytest.HandleGameQuitting();
                int sentWhileWaiting = transport.CountTo(EndRoute);
                Assert.That(sentWhileWaiting, Is.GreaterThan(1), "Precondition: the end was retried while quitting waited");

                float until = Time.realtimeSinceStartup + 2.5f;
                while (Time.realtimeSinceStartup < until)
                    yield return null;
                Assert.AreEqual(sentWhileWaiting, transport.CountTo(EndRoute), "Once quitting gives up, its retries stop");
            }
        }

        // Holds the start's answer (and, when asked, every end's) until released; everything else answers at once.
        private sealed class HeldStart : IFlockHttpAdapter
        {
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>();
            private readonly bool _holdEnds;

            public HeldStart(FlockFakeTransport inner, bool holdEnds = false)
            {
                Inner = inner;
                _holdEnds = holdEnds;
            }

            public FlockFakeTransport Inner { get; }

            public void Release() => _release.TrySetResult(true);

            public void ReleaseAfter(TimeSpan delay) => Task.Delay(delay).ContinueWith(_ => Release());

            public async Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                bool isEnd = request.Url.EndsWith(EndRoute, StringComparison.Ordinal);
                // A held end ignores cancellation, as a transport might: only quitting's own bound can stop waiting for it.
                if (isEnd && _holdEnds)
                    await Task.Delay(TimeSpan.FromSeconds(30));
                else if (!isEnd && request.Url.Contains(StartRoute))
                    await _release.Task;
                return await Inner.SendAsync(request, cancellationToken);
            }
        }
    }
}
