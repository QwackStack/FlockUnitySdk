using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestConfigTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";

        // Copied off a real answer from a local Protokite (2026-09-25), nulls and all. Only the Game Version ID is swapped for the
        // test client's own, so the answer belongs to the build that asked.
        private const string RealConfig =
            @"{""error"":{""code"":null},""response"":{""message"":null,""code"":null},""result"":{""session_started_event"":""session_started"",""test_id"":""01M2MZFADT7Y9WWQCKNS6VK3HK"",""flock_game_version_id"":""test-gvid"",""features"":{""heavy_analytics"":true,""video_recording"":true,""exception_capturing"":true},""form"":{""id"":""01M2MZFAFH3Z8EPV14T6XDSXEG"",""test_id"":""01M2MZFADT7Y9WWQCKNS6VK3HK"",""game_id"":""01KVCSANMD9V0CRW85ADE1WJA1"",""title"":""How was this playtest?"",""description"":""Tell the studio what broke, what confused you, or what you liked."",""is_published"":true,""fields"":[{""id"":""rating"",""type"":""rating"",""label"":""How was this session?"",""required"":true,""help_text"":null,""options"":[]},{""id"":""category"",""type"":""select"",""label"":""What is this about?"",""required"":true,""help_text"":null,""options"":[""Bug"",""Crash"",""Feedback"",""Other""]},{""id"":""title"",""type"":""text"",""label"":""Short title"",""required"":true,""help_text"":null,""options"":[]},{""id"":""description"",""type"":""textarea"",""label"":""What happened?"",""required"":true,""help_text"":""Describe the bug or your feedback."",""options"":[]},{""id"":""steps"",""type"":""textarea"",""label"":""Steps to reproduce"",""required"":false,""help_text"":null,""options"":[]},{""id"":""field_v1hun9"",""type"":""textarea"",""label"":""huh what"",""required"":true,""help_text"":null,""options"":[]},{""id"":""field_dy402v"",""type"":""select"",""label"":""test"",""required"":true,""help_text"":""tete"",""options"":[""tetet""]}],""created_at"":""2026-09-16T11:26:15.537365"",""updated_at"":""2026-09-22T11:27:28.538970""}}}";

        private static string ConfigAnswer(string testId, string gameVersionId = "test-gvid", string features = "{}", string form = "null")
            => "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"" + testId + "\",\"flock_game_version_id\":"
               + (gameVersionId == null ? "null" : "\"" + gameVersionId + "\"") + ",\"features\":" + features + ",\"form\":" + form + "}}";

        private ProtokitePlaytestSettingsForTests _settings;

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            // A Flock session started here starts a playtest session too, which must never use the game's own device id.
            ProtokitePlaytest.DeviceIdFilePathForTesting = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "protokite_config_" + Guid.NewGuid().ToString("N"), "device_id.txt");
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            string folder = System.IO.Path.GetDirectoryName(ProtokitePlaytest.DeviceIdFilePathForTesting);
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            if (System.IO.Directory.Exists(folder))
                System.IO.Directory.Delete(folder, true);
            _settings.Dispose();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
        }

        private static ProtokitePlaytestConfig Parse(string answer) => ProtokitePlaytestConfig.FromJson((JObject)JObject.Parse(answer)["result"]);

        // Reading the answer

        [Test]
        public void EveryMemberOfARealAnswerIsRead()
        {
            ProtokitePlaytestConfig config = Parse(RealConfig);

            Assert.AreEqual("01M2MZFADT7Y9WWQCKNS6VK3HK", config.TestId);
            Assert.AreEqual("session_started", config.SessionStartedEvent);
            Assert.AreEqual("test-gvid", config.FlockGameVersionId);
            Assert.IsTrue(config.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));
            Assert.IsTrue(config.IsFeatureEnabled(ProtokitePlaytestFeatures.ExceptionCapturing));
            Assert.IsTrue(config.IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics));

            ProtokitePlaytestForm form = config.Form;
            Assert.AreEqual("01M2MZFAFH3Z8EPV14T6XDSXEG", form.Id);
            Assert.AreEqual("01M2MZFADT7Y9WWQCKNS6VK3HK", form.TestId);
            Assert.AreEqual("01KVCSANMD9V0CRW85ADE1WJA1", form.GameId);
            Assert.AreEqual("How was this playtest?", form.Title);
            Assert.AreEqual("Tell the studio what broke, what confused you, or what you liked.", form.Description);
            Assert.IsTrue(form.IsPublished);
            StringAssert.StartsWith("2026-09-16T11:26:15.537365", form.CreatedAt, "The server writes no time zone, and none is added");
            StringAssert.StartsWith("2026-09-22T11:27:28.53897", form.UpdatedAt);
            Assert.AreEqual(7, form.Fields.Count);

            ProtokitePlaytestFormField rating = form.Fields[0];
            Assert.AreEqual("rating", rating.Id);
            Assert.AreEqual(ProtokitePlaytestFormFieldTypes.Rating, rating.Type);
            Assert.AreEqual("How was this session?", rating.Label);
            Assert.IsTrue(rating.Required);
            Assert.AreEqual("", rating.HelpText, "A null help text reads as none");
            CollectionAssert.IsEmpty(rating.Options);

            ProtokitePlaytestFormField category = form.Fields[1];
            Assert.AreEqual("category", category.Id, "In the order the studio arranged them");
            Assert.AreEqual(ProtokitePlaytestFormFieldTypes.Select, category.Type);
            CollectionAssert.AreEqual(new[] { "Bug", "Crash", "Feedback", "Other" }, category.Options);

            ProtokitePlaytestFormField steps = form.Fields[4];
            Assert.AreEqual("steps", steps.Id);
            Assert.AreEqual(ProtokitePlaytestFormFieldTypes.TextArea, steps.Type);
            Assert.IsFalse(steps.Required);
            Assert.IsFalse(string.IsNullOrEmpty(form.Fields[3].HelpText), "A help text that is there is read");
        }

        [Test]
        public void OnlyAFeatureSentAsTrueIsOn()
        {
            ProtokitePlaytestConfig config = Parse(ConfigAnswer("t", features:
                "{\"video_recording\":\"true\",\"heavy_analytics\":1,\"exception_capturing\":null,\"new_feature\":true}"));

            Assert.IsFalse(config.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording), "Text is not a boolean");
            Assert.IsFalse(config.IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics), "A number is not a boolean");
            Assert.IsFalse(config.IsFeatureEnabled(ProtokitePlaytestFeatures.ExceptionCapturing), "Null is not a boolean");
            Assert.IsTrue(config.IsFeatureEnabled("new_feature"), "A feature this package does not know is kept");
            Assert.IsFalse(config.IsFeatureEnabled("Video_Recording"), "Names are matched letter for letter");
            Assert.IsFalse(Parse(ConfigAnswer("t", features: "{}")).IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording), "A missing flag is off");
            Assert.IsFalse(Parse("{\"result\":{\"test_id\":\"t\"}}").IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording), "No features at all");
            Assert.IsFalse(config.IsFeatureEnabled(null));
        }

        [Test]
        public void ANullFormOrAFormWithoutAnIdIsNoForm()
        {
            Assert.IsNull(Parse(ConfigAnswer("t")).Form, "A null form");
            Assert.IsNull(Parse(ConfigAnswer("t", form: "{\"title\":\"x\",\"fields\":[]}")).Form, "A form without an id cannot take answers");
            Assert.IsNull(Parse("{\"result\":{\"test_id\":\"t\"}}").Form, "No form member");
        }

        [Test]
        public void AQuestionWithoutAnIdIsLeftOutAndAnUnknownKindIsKept()
        {
            ProtokitePlaytestConfig config = Parse(ConfigAnswer("t", form:
                "{\"id\":\"f\",\"fields\":[{\"type\":\"text\",\"label\":\"no id\"},{\"id\":\"q\",\"type\":\"slider\",\"label\":\"new kind\"},\"not an object\"]}"));

            Assert.AreEqual(1, config.Form.Fields.Count);
            Assert.AreEqual("q", config.Form.Fields[0].Id);
            Assert.AreEqual("slider", config.Form.Fields[0].Type, "A kind this package does not know stays as sent");
            Assert.IsTrue(config.Form.Fields[0].Required, "Required when the server does not say, as the server assumes");
        }

        [Test]
        public void AnAnswerWithoutATestIdIsNoConfig()
        {
            Assert.IsNull(Parse("{\"result\":{\"session_started_event\":\"session_started\"}}"));
            Assert.IsNull(Parse("{\"result\":{\"test_id\":\"\"}}"));
            Assert.IsNull(Parse("{\"result\":{\"test_id\":null}}"));
        }

        // What an answer means

        [TestCase(401, "ApiKeyRefused")]
        [TestCase(422, "ApiKeyRefused")]
        [TestCase(404, "PlaytestNotLinked")]
        [TestCase(403, "Unavailable")]
        [TestCase(500, "Unavailable")]
        [TestCase(503, "Unavailable")]
        public void AFailureIsJudgedByItsStatusAlone(int status, string expected)
        {
            FlockException failure = status == 401 || status == 403
                ? new FlockAuthException("x") { StatusCode = status }
                : status == 422 ? new FlockValidationException("x") { StatusCode = status } : (FlockException)new FlockNetworkException("x", status);
            Assert.AreEqual(expected, ProtokitePlaytest.ConfigStateFor(null, failure, "v").ToString());
        }

        [Test]
        public void AFailureThatNeverReachedProtokiteIsUnavailable()
        {
            Assert.AreEqual(ProtokitePlaytestConfigState.Unavailable, ProtokitePlaytest.ConfigStateFor(null, new FlockNetworkException("Request timeout"), "v"));
            Assert.AreEqual(ProtokitePlaytestConfigState.Unavailable, ProtokitePlaytest.ConfigStateFor(null, new FlockSerializationException("Malformed response body"), "v"));
            Assert.AreEqual(ProtokitePlaytestConfigState.Unavailable, ProtokitePlaytest.ConfigStateFor(null, new InvalidOperationException("x"), "v"));
        }

        [TestCase("test-gvid", "test-gvid", "Loaded")]
        [TestCase("TEST-GVID", "test-gvid", "ForAnotherVersion")]
        [TestCase("other", "test-gvid", "ForAnotherVersion")]
        [TestCase("newest", null, "ForAnotherVersion")]
        [TestCase(null, "test-gvid", "Loaded")]
        public void AConfigForAnotherVersionIsRefused(string answeredVersion, string sentVersion, string expected)
        {
            ProtokitePlaytestConfig config = Parse(ConfigAnswer("t", answeredVersion));
            Assert.AreEqual(expected, ProtokitePlaytest.ConfigStateFor(config, null, sentVersion).ToString());
        }

        // Fetching it, through the Flock SDK a game runs

        [Test]
        public void TheFetchCarriesTheGamesKeyAndVersionAndNoSignIn()
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(RealConfig))))
            {
                flock.LoginAs("player-1");
                ProtokitePlaytest.Refresh();

                FlockHttpRequest sent = flock.Transport.LastTo(ConfigRoute);
                Assert.AreEqual("http://protokite.test/game/sdk/playtest-config", sent.Url, "Joined onto the settings' URL, trailing slash and all");
                Assert.AreEqual("GET", sent.Method);
                Assert.AreEqual("test-key", sent.Headers["X-Flock-API-Key"]);
                Assert.AreEqual("test-gvid", sent.Headers["X-Game-Version-ID"]);
                Assert.IsFalse(sent.Headers.ContainsKey("Authorization"), "Protokite never sees the player's sign-in");

                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Assert.AreEqual("01M2MZFADT7Y9WWQCKNS6VK3HK", ProtokitePlaytest.Config.TestId);
                Assert.IsTrue(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));

                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(ConfigRoute), "Fetched once per Flock client, not once a frame");
            }
        }

        [TestCase(401, ProtokitePlaytestStatus.ProtokiteRefusedApiKey, "refused the Flock API key")]
        [TestCase(404, ProtokitePlaytestStatus.PlaytestNotLinked, "No Protokite playtest is linked")]
        [TestCase(403, ProtokitePlaytestStatus.PlaytestConfigUnavailable, "Could not fetch")]
        [TestCase(503, ProtokitePlaytestStatus.PlaytestConfigUnavailable, "Could not fetch")]
        public void EachAnswerSetsItsStatusAndLogsItOnce(int status, ProtokitePlaytestStatus expected, string logged)
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Coded(status, "any"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex(logged));
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();

                Assert.AreEqual(expected, ProtokitePlaytest.Status);
                Assert.IsNull(ProtokitePlaytest.Config);
                Assert.IsFalse(ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));
                LogAssert.NoUnexpectedReceived();
            }
        }

        [Test]
        public void AnAnswerWithNoConfigInItIsUnavailable()
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok("{\"result\":null}"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("Could not fetch"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestConfigUnavailable, ProtokitePlaytest.Status);
            }
        }

        [Test]
        public void ACancellationThePlaytestDidNotAskForIsUnavailableNotStuck()
        {
            using (FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockHttpClient.Configure(new CancelsOnItsOwn());
                LogAssert.Expect(LogType.Warning, new Regex("Could not fetch"));
                ProtokitePlaytest.Refresh();

                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestConfigUnavailable, ProtokitePlaytest.Status,
                    "A transport's own cancellation is a failure to reach Protokite, not the playtest stopping its fetch");
            }
        }

        [Test]
        public void ARefusalIsNotAskedAgainWhenTheNextFlockSessionStarts()
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()
                .On(ConfigRoute, FlockFakeTransport.Coded(401, "any"))
                .On("/analytics/sessions", FlockFakeTransport.Ok("{\"session_id\":\"srv-session-1\"}"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("refused the Flock API key"));
                ProtokitePlaytest.Refresh();
                StartAFlockSession(flock);

                Assert.AreEqual(1, flock.Transport.CountTo(ConfigRoute));
                Assert.AreEqual(ProtokitePlaytestStatus.ProtokiteRefusedApiKey, ProtokitePlaytest.Status);
            }
        }

        [Test]
        public void UnreachableIsFetchedAgainWhenTheNextFlockSessionStarts()
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()
                .OnSequence(ConfigRoute, FlockFakeTransport.Offline(), FlockFakeTransport.Ok(RealConfig))
                .On("/analytics/sessions", FlockFakeTransport.Ok("{\"session_id\":\"srv-session-1\"}"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("Could not fetch"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestConfigUnavailable, ProtokitePlaytest.Status);
                Assert.AreEqual(1, flock.Transport.CountTo(ConfigRoute), "Precondition: the Flock SDK's retries are off in this test client");

                StartAFlockSession(flock);

                Assert.AreEqual(2, flock.Transport.CountTo(ConfigRoute));
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
            }
        }

        [Test]
        public void TheStatusStopsReadingTheConfigTheMomentFlockShutsDown()
        {
            FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(RealConfig)));
            ProtokitePlaytest.Refresh();
            Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition");

            flock.Dispose();

            // No Refresh in between: a game reading the status straight after a shutdown must not see the old playtest.
            Assert.AreEqual(ProtokitePlaytestStatus.WaitingForFlock, ProtokitePlaytest.Status);
            Assert.IsNull(ProtokitePlaytest.Config);

            using (FlockTestClient next = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(ConfigAnswer("second")))))
            {
                Assert.AreEqual(ProtokitePlaytestStatus.FetchingPlaytestConfig, ProtokitePlaytest.Status, "A new Flock client has no config yet");
                ProtokitePlaytest.Refresh();
                Assert.AreEqual("second", ProtokitePlaytest.Config.TestId, "Fetched again under the new client");
            }
        }

        [UnityTest]
        public IEnumerator AnAnswerThatLandsAfterFlockRestartedIsIgnored()
        {
            FirstAnswerHeld transport = new FirstAnswerHeld(ConfigAnswer("second"));

            FlockTestClient first = FlockTestClient.Create(new FlockFakeTransport());
            FlockHttpClient.Configure(transport);
            ProtokitePlaytest.Refresh();
            Assert.AreEqual(ProtokitePlaytestStatus.FetchingPlaytestConfig, ProtokitePlaytest.Status, "Precondition: the first answer is held");
            first.Dispose();

            using (FlockTestClient second = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockHttpClient.Configure(transport);
                ProtokitePlaytest.Refresh();
                Assert.AreEqual("second", ProtokitePlaytest.Config?.TestId, "Precondition: the second client's answer came first");

                transport.ReleaseTheFirstAnswer(FlockFakeTransport.Status(404, "{}"));
                for (int frame = 0; frame < 10 && !transport.FirstAnswerLanded; frame++)
                    yield return null;
                yield return null;

                Assert.IsTrue(transport.FirstAnswerLanded, "Precondition: the held answer has landed");
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "The first client's 404 belongs to nobody");
                Assert.AreEqual("second", ProtokitePlaytest.Config.TestId);
            }
        }

        [UnityTest]
        public IEnumerator NothingMoreIsSentWhileTheFetchIsWaitingForItsAnswer()
        {
            FirstAnswerHeld transport = new FirstAnswerHeld(ConfigAnswer("second"));
            using (FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockHttpClient.Configure(transport);
                for (int frame = 0; frame < 3; frame++)
                    ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, transport.Requests, "Every frame while the answer is on its way sends nothing new");

                transport.ReleaseTheFirstAnswer(FlockFakeTransport.Ok(RealConfig));
                for (int frame = 0; frame < 10 && !transport.FirstAnswerLanded; frame++)
                    yield return null;
                yield return null;
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the held answer landed");
            }
        }

        [UnityTest]
        public IEnumerator ProtokiteIsAskedAgainWithTheFlockSdksRetrySettings()
        {
            FlockFakeTransport transport = new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Status(503, "{}"));
            using (CreateWithRetries(transport))
            {
                LogAssert.Expect(LogType.Warning, new Regex("Could not fetch"));
                ProtokitePlaytest.Refresh();
                yield return WaitForRealSeconds(1.5f);

                Assert.AreEqual(3, transport.CountTo(ConfigRoute), "The Flock SDK's retry settings are used: one try and two retries");
                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestConfigUnavailable, ProtokitePlaytest.Status);
            }
        }

        [UnityTest]
        public IEnumerator FlockShuttingDownStopsTheRetries()
        {
            FlockFakeTransport transport = new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Status(503, "{}"));
            FlockTestClient flock = CreateWithRetries(transport);
            ProtokitePlaytest.Refresh();
            Assert.AreEqual(1, transport.CountTo(ConfigRoute), "Precondition: the first try went out at once");

            flock.Dispose();
            FlockHttpClient.Configure(transport);
            ProtokitePlaytest.Refresh();
            yield return WaitForRealSeconds(1.5f);

            Assert.AreEqual(1, transport.CountTo(ConfigRoute), "Nothing more is sent with the key of a Flock client that has shut down");
        }

        [Test]
        public void NothingIsSentWhilePlaytestingIsOff()
        {
            _settings.Settings.PlaytestingEnabled = false;
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(RealConfig))))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, flock.Transport.CountTo(ConfigRoute));
                Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.Status);

                ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();
                Assert.AreEqual(0, Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>().Length, "No driver runs with playtesting off");
            }
        }

        [Test]
        public void TheDriverStartsBeforeTheFirstSceneLoads()
        {
            MethodInfo start = typeof(ProtokitePlaytestDriver).GetMethod(nameof(ProtokitePlaytestDriver.StartWhenPlaytestingIsOn),
                BindingFlags.Static | BindingFlags.NonPublic);
            RuntimeInitializeOnLoadMethodAttribute attribute = start.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
            Assert.IsNotNull(attribute, "Unity calls it at start-up");
            Assert.AreEqual(RuntimeInitializeLoadType.BeforeSceneLoad, attribute.loadType);
        }

        // Starts a Flock analytics session the way a game's sign-in does, so the Flock SDK raises its own event.
        private static void StartAFlockSession(FlockTestClient flock)
        {
            flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            string sessionId = flock.Run(() => flock.Client.Analytics.StartSessionAsync());
            Assert.IsNotNull(sessionId, "Precondition: the Flock session started");
        }

        private static FlockTestClient CreateWithRetries(FlockFakeTransport transport)
            => FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy
            {
                MaxRetries = 2, InitialDelay = TimeSpan.FromMilliseconds(100), UseJitter = false
            });

        private static IEnumerator WaitForRealSeconds(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
                yield return null;
        }

        // A transport that gives up with a cancellation nobody asked for, as a studio's own transport might on its own timeout.
        private sealed class CancelsOnItsOwn : IFlockHttpAdapter
        {
            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
                => throw new TaskCanceledException("the transport's own timeout");
        }

        // Holds the first request's answer until the test releases it, and answers every later one at once.
        private sealed class FirstAnswerHeld : IFlockHttpAdapter
        {
            private readonly string _laterAnswer;
            private readonly TaskCompletionSource<FlockHttpResponse> _first = new TaskCompletionSource<FlockHttpResponse>();
            private int _requests;

            public bool FirstAnswerLanded { get; private set; }

            public int Requests => _requests;

            public FirstAnswerHeld(string laterAnswer) => _laterAnswer = laterAnswer;

            public void ReleaseTheFirstAnswer(FlockHttpResponse answer) => _first.TrySetResult(answer);

            public async Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _requests) > 1)
                    return FlockFakeTransport.Ok(_laterAnswer);
                FlockHttpResponse answer = await _first.Task;
                FirstAnswerLanded = true;
                return answer;
            }
        }
    }
}
