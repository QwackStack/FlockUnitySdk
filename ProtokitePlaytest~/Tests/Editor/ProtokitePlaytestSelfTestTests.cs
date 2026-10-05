using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace Protokite.Playtest.Tests
{
    /// <summary>The live self-test, run against a fake Protokite that refuses each request for the reason the real one does: it reads the key, version, body and session.</summary>
    public class ProtokitePlaytestSelfTestTests
    {
        private const string FlockSessionRoute = "/analytics/sessions";
        private const string ServerSessionId = "01K5SRVSESSION000000000001";
        private const string ClosedVersion = "01KXCLOSED0000000000000000";
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string FormRoute = "/game/sdk/feedback-form";

        private const string Form =
            "{\"id\":\"f\",\"test_id\":\"t\",\"game_id\":\"g\",\"title\":\"How was this playtest?\",\"is_published\":true,\"fields\":[" +
            "{\"id\":\"rating\",\"type\":\"rating\",\"label\":\"How was this session?\",\"required\":true,\"options\":[]}," +
            "{\"id\":\"category\",\"type\":\"select\",\"label\":\"What is this about?\",\"required\":true,\"options\":[\"Bug\",\"Crash\",\"Feedback\"]}," +
            "{\"id\":\"title\",\"type\":\"text\",\"label\":\"Short title\",\"required\":true,\"options\":[]}," +
            "{\"id\":\"agree\",\"type\":\"checkbox\",\"label\":\"May we follow up?\",\"required\":false,\"options\":[]}]}";

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private StorageForTests _storage;

        private string Recordings => Path.Combine(_folder, "Recordings");
        private string KeptForms => Path.Combine(_folder, "FeedbackForms");

        private static string Config(bool video, bool heavyAnalytics, bool form, string playtestVersion = "test-gvid")
            => "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"" + playtestVersion + "\",\"features\":{" +
               $"\"video_recording\":{(video ? "true" : "false")},\"heavy_analytics\":{(heavyAnalytics ? "true" : "false")},\"exception_capturing\":true}},\"form\":" +
               (form ? Form : "null") + "}}";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_self_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Recordings;
            ProtokitePlaytest.FeedbackFormsFolderForTesting = KeptForms;
            ProtokitePlaytest.VideoEncoderForTesting = () => new FakeH264Encoder();
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => new FakeFrameSource();
            ProtokitePlaytest.SelfTestWaitSecondsForTesting = 5.0;
            _storage = new StorageForTests();
            FlockHttpClient.UseFileUploader(_storage);
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            _storage.LetGo();
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.FeedbackFormsFolderForTesting = null;
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.SelfTestWaitSecondsForTesting = null;
            ProtokitePlaytest.SelfTestRunsInThisBuildForTesting = null;
            ProtokitePlaytest.NetworkForTesting = null;
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = false;
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            FlockHttpClient.UseFileUploader(null);
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            _settings.Dispose();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static FlockTestClient StartFlock(FakeProtokite protokite, int flockRetries = 0)
        {
            FlockFakeTransport transport = new FlockFakeTransport()
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"))
                .Default(protokite.Answer);
            protokite.Transport = transport;
            return FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy { MaxRetries = flockRetries, InitialDelay = TimeSpan.Zero });
        }

        // What a game's frames do for the playtest while a test waits.
        private static void AFrame()
        {
            ProtokitePlaytest.Refresh();
            ProtokitePlaytest.UpdateVideo(1.0 / 60.0);
            ProtokitePlaytest.UpdateHeavyAnalytics(1.0 / 60.0);
        }

        private static IEnumerator Frames(Func<bool> until, float seconds, string what)
        {
            DateTime giveUp = DateTime.UtcNow.AddSeconds(seconds);
            while (!until() && DateTime.UtcNow < giveUp)
            {
                AFrame();
                yield return null;
            }
            Assert.IsTrue(until(), what + $" within {seconds} s");
        }

        // The config loaded and the launch's session started, the way a game's sign-in starts it.
        private static IEnumerator AReadyPlaytestWithASession(FlockTestClient flock)
        {
            yield return Frames(() => ProtokitePlaytest.Status == ProtokitePlaytestStatus.Ready, 5f, "The playtest is ready");
            flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            flock.Run(() => flock.Client.Analytics.StartSessionAsync());
            yield return Frames(() => ProtokitePlaytest.PlaytestSessionId != null, 5f, "The launch's session started");
        }

        private static IEnumerator TheRunEnds(Task<ProtokitePlaytestSelfTestReport> run, float seconds = 30f)
            => Frames(() => run.IsCompleted, seconds, "The self-test finished");

        private static ProtokitePlaytestSelfTestStep StepNamed(ProtokitePlaytestSelfTestReport report, string startsWith)
            => report.Steps.Single(step => step.Name.StartsWith(startsWith, StringComparison.Ordinal));

        private static void ExpectTheRaisedExceptions()
        {
            LogAssert.Expect(LogType.Exception, new Regex("Raised on purpose by the Protokite Playtest self-test"));
            LogAssert.Expect(LogType.Exception, new Regex("Raised on purpose by the Protokite Playtest self-test"));
        }

        // The whole run

        [UnityTest]
        public IEnumerator EveryStepPassesAgainstAPlaytestThatTurnsEverythingOn()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: true, form: true));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                AFrame();
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: the launch's recording runs");
                for (int frame = 0; frame < 30; frame++)
                    AFrame();
                int flockSessionsBefore = flock.Transport.CountTo(FlockSessionRoute);

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync(ClosedVersion);
                Assert.IsTrue(ProtokitePlaytestSelfTest.IsRunning);
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestReport report = run.Result;

                string failures = string.Join("\n", report.Steps.Where(s => s.Outcome != ProtokitePlaytestSelfTestOutcome.Passed).Select(s => s.Name + ": " + s.Detail));
                Assert.AreEqual(16, report.Passed, failures);
                Assert.AreEqual(0, report.Failed);
                Assert.AreEqual(0, report.Skipped);
                Assert.IsTrue(report.AllRunStepsPassed);
                Assert.IsFalse(ProtokitePlaytestSelfTest.IsRunning);

                // What each probe sent, read back: a probe refused for the wrong reason would pass on a fake that answers by address.
                List<FlockHttpRequest> configs = flock.Transport.AllTo(ConfigRoute);
                FlockHttpRequest wrongKey = configs[configs.Count - 3];
                Assert.AreEqual(ProtokitePlaytest.SelfTestWrongApiKey, wrongKey.Headers["X-Flock-API-Key"]);
                Assert.AreEqual("test-gvid", wrongKey.Headers["X-Game-Version-ID"], "Only the key is swapped");
                FlockHttpRequest noKey = configs[configs.Count - 2];
                Assert.IsFalse(noKey.Headers.Keys.Any(k => string.Equals(k, "X-Flock-API-Key", StringComparison.OrdinalIgnoreCase)), "No key at all");
                FlockHttpRequest unlinked = configs[configs.Count - 1];
                Assert.AreEqual("test-key", unlinked.Headers["X-Flock-API-Key"]);
                Assert.AreEqual(ProtokitePlaytest.SelfTestIdNothingHas, unlinked.Headers["X-Game-Version-ID"]);

                List<FlockHttpRequest> starts = protokite.SessionStarts;
                Assert.AreEqual(3, starts.Count, "The launch's own start and the two probes");
                JObject noPlayer = JObject.Parse(starts[1].JsonBody);
                Assert.IsNull(noPlayer["steam_id"]);
                Assert.IsNull(noPlayer["device_id"]);
                Assert.AreEqual(report.RunId, (string)noPlayer["extra_debug"][ProtokitePlaytest.SelfTestRunProperty]);
                Assert.AreEqual(ClosedVersion, starts[2].Headers["X-Game-Version-ID"]);
                Assert.AreEqual(ProtokitePlaytest.SelfTestDeviceIdPrefix + report.RunId, (string)JObject.Parse(starts[2].JsonBody)["device_id"],
                    "A device of the run's own, never the player's, under the closed playtest's version");

                List<FlockHttpRequest> forms = flock.Transport.AllTo(FormRoute);
                Assert.AreEqual(4, forms.Count);
                Assert.IsNull(JObject.Parse(forms[0].JsonBody)["answers"]["rating"], "The needed answer left out");
                Assert.AreEqual("BUG", (string)JObject.Parse(forms[1].JsonBody)["answers"]["category"], "An option not on the list, by letter case");
                Assert.AreEqual(ProtokitePlaytest.SelfTestIdNothingHas, (string)JObject.Parse(forms[2].JsonBody)["session_id"]);
                JObject taken = JObject.Parse(forms[3].JsonBody);
                Assert.AreEqual("pk-1", (string)taken["session_id"]);
                Assert.AreEqual("Bug", (string)taken["answers"]["category"]);
                StringAssert.Contains(report.RunId, (string)taken["answers"]["title"]);
                Assert.AreEqual(1, protokite.FormsStored.Count, "Only the good form was stored");
                // What Protokite answered, which only the self-test's own client hears: the game's sender keeps a form and never says.
                StringAssert.Contains("stored it as form-1", StepNamed(report, "A filled-in form is taken").Detail);

                StringAssert.Contains("/playtest-session/" + ProtokitePlaytest.SelfTestIdNothingHas + "/recording-upload", protokite.LinkRequests[0].Url);
                Assert.AreEqual(1, _storage.Uploads, "The launch's recording was uploaded once");
                Assert.IsNull(ProtokitePlaytest.RecordingRunForTesting, "and is no longer kept on disk");
                CollectionAssert.AreEqual(new[] { ProtokitePlaytest.SelfTestIdNothingHas }, protokite.EndsAsked, "The self-test leaves the launch's session for quitting to end");

                // The game's own state, after every refusal it provoked.
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId);
                Assert.AreEqual(flockSessionsBefore, flock.Transport.CountTo(FlockSessionRoute));
                Assert.IsFalse(Directory.Exists(KeptForms) && Directory.GetFiles(KeptForms).Length > 0, "The good form went through the self-test's own client, not the game's kept forms");
            }
        }

        [UnityTest]
        public IEnumerator WithAPlaytestIdEveryProbeAsksForThePlaytestsVersion()
        {
            string playtestVersion = ProtokitePlaytestSetupChecksTests.PlaytestVersionId;
            _settings.Settings.PlaytestId = ProtokitePlaytestSetupChecksTests.TestId;
            _settings.Settings.KeepResolvedPlaytestVersion(ProtokitePlaytestSetupChecksTests.TestId, playtestVersion);
            // This Protokite takes only the playtest's version: a probe sent with the Flock SDK's own is refused for it.
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: true, form: true, playtestVersion), playtestVersion);
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                for (int frame = 0; frame < 30; frame++)
                    AFrame();

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync(ClosedVersion);
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestReport report = run.Result;
                string failures = string.Join("\n", report.Steps.Where(s => s.Outcome != ProtokitePlaytestSelfTestOutcome.Passed).Select(s => s.Name + ": " + s.Detail));
                Assert.AreEqual(16, report.Passed, failures);

                List<FlockHttpRequest> configs = flock.Transport.AllTo(ConfigRoute);
                Assert.AreEqual(playtestVersion, configs[configs.Count - 3].Headers["X-Game-Version-ID"], "The wrong-key probe swaps the key alone");
                Assert.AreEqual(playtestVersion, protokite.SessionStarts[0].Headers["X-Game-Version-ID"], "The launch's own session");
                Assert.AreEqual(playtestVersion, protokite.SessionStarts[1].Headers["X-Game-Version-ID"], "The probe naming no player");
                Assert.AreEqual("test-gvid", flock.Transport.LastTo(FlockSessionRoute).Headers["X-Game-Version-ID"], "The Flock SDK keeps its own");
            }
        }

        [UnityTest]
        public IEnumerator AnAcceptedCounterCaseFailsAndASessionItMadeIsEnded()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: true));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                protokite.AcceptEverything = true;

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync(ClosedVersion);
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestReport report = run.Result;

                foreach (string refusal in new[] { "A wrong API key", "A missing API key", "A version no playtest", "A session start naming", "A session start for a closed",
                             "A form missing", "A form choosing", "A form naming", "An upload link", "An end for" })
                {
                    ProtokitePlaytestSelfTestStep step = StepNamed(report, refusal);
                    Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Failed, step.Outcome, refusal);
                    StringAssert.Contains("it was accepted", step.Detail, refusal);
                }
                Assert.AreEqual(10, report.Failed);
                Assert.IsFalse(report.AllRunStepsPassed);
                // Both sessions a probe was wrongly given are ended; the launch's own is left for quitting.
                CollectionAssert.AreEquivalent(new[] { "pk-2", "pk-3", ProtokitePlaytest.SelfTestIdNothingHas }, protokite.EndsAsked);
                StringAssert.Contains("pk-2, was ended", StepNamed(report, "A session start naming").Detail);
            }
        }

        [UnityTest]
        public IEnumerator EachCounterCaseIsSentOnceWhateverFlocksRetries()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: true));
            using (FlockTestClient flock = StartFlock(protokite, flockRetries: 3))
            {
                yield return AReadyPlaytestWithASession(flock);
                int configsBefore = flock.Transport.CountTo(ConfigRoute);
                int startsBefore = protokite.SessionStarts.Count;
                protokite.AnswerEveryRequestWith = 500;

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync(ClosedVersion);
                yield return TheRunEnds(run);

                Assert.AreEqual(3, flock.Transport.CountTo(ConfigRoute) - configsBefore, "Three config probes, each sent once");
                Assert.AreEqual(2, protokite.SessionStarts.Count - startsBefore, "Two start probes, each sent once");
                Assert.AreEqual(4, flock.Transport.CountTo(FormRoute), "Four forms, each sent once");
                Assert.AreEqual(1, protokite.LinkRequests.Count);
                Assert.AreEqual(1, protokite.EndsAsked.Count);
                StringAssert.Contains("answered HTTP 500, where 401 was expected", StepNamed(run.Result, "A wrong API key").Detail);
            }
        }

        // Skips, and runs that never start

        [UnityTest]
        public IEnumerator WhileThePlayerHasNotAnsweredTheSessionStepsSayWhy()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: true));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return Frames(() => ProtokitePlaytest.Status == ProtokitePlaytestStatus.WaitingForPlayerConsent, 5f, "The question waits for an answer");
                flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync(ClosedVersion);
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestReport report = run.Result;

                Assert.IsFalse(File.Exists(ProtokitePlaytest.DeviceIdFilePathForTesting), "No probe made the game's device id, with no session to need one");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Passed, StepNamed(report, "A session start for a closed").Outcome, "It needs no session");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Passed, StepNamed(report, "The playtest config loads").Outcome, "The config did load");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Passed, StepNamed(report, "A wrong API key").Outcome, "A config probe needs no session");
                ProtokitePlaytestSelfTestStep session = StepNamed(report, "This launch's Protokite session starts");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, session.Outcome);
                StringAssert.Contains("has not said what the playtest may collect", session.Detail);
                StringAssert.Contains("has not said what the playtest may collect", StepNamed(report, "A filled-in form").Detail, "Not 'the config did not load'");
                Assert.AreEqual(2, protokite.SessionStarts.Count, "Only the two probes; the game started none");
            }
        }

        [Test]
        public void AReleaseBuildDoesNotRunItAndSendsNothing()
        {
            ProtokitePlaytest.SelfTestRunsInThisBuildForTesting = false;
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                LogAssert.Expect(LogType.Warning, new Regex("did not run: it runs in the editor and in development builds only"));
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                Assert.IsTrue(run.IsCompleted);
                StringAssert.Contains("development builds only", run.Result.NotRunBecause);
                Assert.AreEqual(0, run.Result.Steps.Count);
                Assert.IsFalse(run.Result.AllRunStepsPassed);
                Assert.AreEqual(0, flock.Transport.Requests.Count);
            }
        }

        [Test]
        public void WithoutFlockItDoesNotRun()
        {
            LogAssert.Expect(LogType.Warning, new Regex("did not run: the Flock SDK is not running"));
            Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
            Assert.IsTrue(run.IsCompleted);
            StringAssert.Contains("Flock SDK is not running", run.Result.NotRunBecause);
        }

        [UnityTest]
        public IEnumerator ASecondRunWhileOneRunsIsRefused()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                // The first run's first probe is held, so it is still running: against a fake that answers at once it would not be.
                flock.Transport.GateNext(ConfigRoute);
                // In the order they are logged: the refusal at once, the raised exceptions later in the first run.
                LogAssert.Expect(LogType.Warning, new Regex("did not run: a self-test is already running"));
                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> first = ProtokitePlaytestSelfTest.RunAsync();
                Assert.IsFalse(first.IsCompleted, "Precondition: the first run is under way");
                Task<ProtokitePlaytestSelfTestReport> second = ProtokitePlaytestSelfTest.RunAsync();
                Assert.IsTrue(second.IsCompleted);
                StringAssert.Contains("already running", second.Result.NotRunBecause);
                flock.Transport.ReleaseGate();
                yield return TheRunEnds(first);
                Assert.IsNull(first.Result.NotRunBecause);
            }
        }

        [UnityTest]
        public IEnumerator WithNoClosedPlaytestNamedThatStepIsSkippedAndSaysHowToRunIt()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep closed = StepNamed(run.Result, "A session start for a closed");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, closed.Outcome);
                StringAssert.Contains("ProtokitePlaytestSelfTest.RunAsync", closed.Detail);
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, StepNamed(run.Result, "A filled-in form").Outcome, "The playtest publishes no form");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, StepNamed(run.Result, "This launch's recording").Outcome, "Nor video");
            }
        }

        [UnityTest]
        public IEnumerator ThePlaytestEventWaitsForHeavyAnalyticsToStartMeasuring()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: true, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                // Ready, with a session, and no frame of heavy analytics yet: measuring starts on the next one.
                ProtokitePlaytest.Refresh();
                flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());
                DateTime giveUp = DateTime.UtcNow.AddSeconds(5);
                while (ProtokitePlaytest.PlaytestSessionId == null && DateTime.UtcNow < giveUp)
                {
                    ProtokitePlaytest.Refresh();
                    yield return null;
                }
                Assert.IsNotNull(ProtokitePlaytest.PlaytestSessionId, "Precondition: the session started");

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep playtestEvent = StepNamed(run.Result, "A playtest event is recorded");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Passed, playtestEvent.Outcome, playtestEvent.Detail);
            }
        }

        // Endings mid-run

        [UnityTest]
        public IEnumerator AFlockShutdownMidRunFailsOnceAndSkipsTheRest()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                // The first probe is held, and Flock shuts down while it is out.
                flock.Transport.GateNext(ConfigRoute);
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return Frames(() => protokite.ConfigAnswers > 1 || flock.Transport.CountTo(ConfigRoute) > 1, 5f, "The first probe went out");
                FlockClient.Shutdown();
                flock.Transport.ReleaseGate();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestReport report = run.Result;

                // The probe already out finishes as Protokite answered it; the next step is the one that finds Flock gone.
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Passed, StepNamed(report, "A wrong API key").Outcome);
                Assert.AreEqual(2, report.Passed, "The config had loaded, and the probe out was refused as it should be");
                Assert.AreEqual(1, report.Failed, "One failure says Flock shut down");
                StringAssert.Contains("Flock SDK shut down during the self-test", StepNamed(report, "A missing API key").Detail);
                Assert.AreEqual(13, report.Skipped, "and every later step is skipped for it");
                foreach (ProtokitePlaytestSelfTestStep step in report.Steps.Where(s => s.Outcome == ProtokitePlaytestSelfTestOutcome.Skipped))
                    StringAssert.Contains("Flock SDK shut down", step.Detail, step.Name);
            }
        }

        [UnityTest]
        public IEnumerator QuittingBeforeAnyStepEndsSaysSoAtWarning()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: false, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                // The config fetch is held, so the first step is still waiting when the game closes.
                flock.Transport.GateNext(ConfigRoute);
                ProtokitePlaytest.Refresh();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                LogAssert.Expect(LogType.Warning, new Regex(@"Self-test \w+ stopped, as the game is closing, after: 0 passed, 0 failed, 0 skipped\."));
                ProtokitePlaytest.HandleGameQuitting();
                flock.Transport.ReleaseGate();
                yield return TheRunEnds(run);
                Assert.AreEqual(0, run.Result.Steps.Count, "Nothing is added once the end is said");
                Assert.IsFalse(ProtokitePlaytestSelfTest.IsRunning);
            }
        }

        // The recording

        [UnityTest]
        public IEnumerator TheUploadStepFailsWhenTheStorageRefusesIt()
        {
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: false, form: false));
            _storage.Answer = 500;
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                AFrame();
                ExpectTheRaisedExceptions();
                LogAssert.Expect(LogType.Warning, new Regex("was not uploaded"));
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep upload = StepNamed(run.Result, "This launch's recording");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Failed, upload.Outcome, "A link is not an upload: only the storage's 2xx counts");
                StringAssert.Contains("HTTP 500", upload.Detail);
            }
        }

        [UnityTest]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public IEnumerator TheUploadStepFailsWhenTheRecordingIsStillOnDisk()
        {
            // Windows only: an open file holds its folder there, and macOS and Linux delete it anyway.
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: false, form: false));
            _storage.KeepTheFileOpen = true;
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                AFrame();
                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep upload = StepNamed(run.Result, "This launch's recording");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Failed, upload.Outcome);
                StringAssert.Contains("could not all be deleted", upload.Detail);
            }
        }

        [UnityTest]
        public IEnumerator AnEncoderThatCannotStartFailsTheUploadStepWithItsReason()
        {
            ProtokitePlaytest.VideoEncoderForTesting = () => new FakeH264Encoder { RefuseToStart = "the graphics card's encoder is busy" };
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                AFrame();
                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep upload = StepNamed(run.Result, "This launch's recording");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Failed, upload.Outcome);
                StringAssert.Contains("kept no file to upload", upload.Detail);
                StringAssert.Contains("the graphics card's encoder is busy", upload.Detail, "The recording's own reason, not a wait running out");
            }
        }

        [UnityTest]
        public IEnumerator ABuildWithNoEncoderSkipsTheUploadAndSaysWhy()
        {
            ProtokitePlaytest.VideoEncoderForTesting = () => null;
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                AFrame();
                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep upload = StepNamed(run.Result, "This launch's recording");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, upload.Outcome);
                StringAssert.Contains("records no video", upload.Detail);
            }
        }

        [UnityTest]
        public IEnumerator APlayerWhoChoseWiFiOnlyOffWiFiSkipsTheUploadAndSaysWhy()
        {
            Assert.IsTrue(new ProtokitePlaytestUploadNetworkFile(_settings.UploadNetworkFilePath).Save(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
            ProtokitePlaytest.NetworkForTesting = () => NetworkReachability.ReachableViaCarrierDataNetwork;
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return AReadyPlaytestWithASession(flock);
                AFrame();
                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync();
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep upload = StepNamed(run.Result, "This launch's recording");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, upload.Outcome, upload.Detail);
                StringAssert.Contains("chose Wi-Fi only and the device is not on Wi-Fi", upload.Detail);
                Assert.AreEqual(1, protokite.LinkRequests.Count, "Only the probe for a session that does not exist: the recording waits on the device");
                Assert.AreEqual(0, _storage.Uploads, "Nothing was sent to the storage");
            }
        }

        [UnityTest]
        public IEnumerator WhileThePhonesQuestionAboutNetworksWaitsTheSessionStepsSayWhy()
        {
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = true;
            _settings.Settings.RecordVideoOnAndroid = true;
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(ProtokitePlaytestConsentChoice.VideoOnly));
            FakeProtokite protokite = new FakeProtokite(Config(video: true, heavyAnalytics: false, form: false));
            using (FlockTestClient flock = StartFlock(protokite))
            {
                yield return Frames(() => ProtokitePlaytest.Status == ProtokitePlaytestStatus.Ready, 5f, "The playtest is ready");
                flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());

                ExpectTheRaisedExceptions();
                Task<ProtokitePlaytestSelfTestReport> run = ProtokitePlaytestSelfTest.RunAsync(ClosedVersion);
                yield return TheRunEnds(run);
                ProtokitePlaytestSelfTestStep session = StepNamed(run.Result, "This launch's Protokite session starts");
                Assert.AreEqual(ProtokitePlaytestSelfTestOutcome.Skipped, session.Outcome, session.Detail);
                StringAssert.Contains("has not said which networks recordings may upload on", session.Detail);
                Assert.AreEqual(2, protokite.SessionStarts.Count, "Only the two probes; the game's own waits for the answer");
            }
        }

        // The judge

        [Test]
        public void OnlyTheExpectedStatusIsTheRefusal()
        {
            StringAssert.Contains("it was accepted", ProtokitePlaytest.WhyNotTheRefusal(null, 401));
            StringAssert.Contains("it was accepted, with an answer that could not be read",
                ProtokitePlaytest.WhyNotTheRefusal(new FlockSerializationException("The playtest session start answer names no usable session_id"), 422));
            StringAssert.Contains("no answer came from Protokite", ProtokitePlaytest.WhyNotTheRefusal(new FlockNetworkException("Network request failed"), 401));
            StringAssert.Contains("answered HTTP 404, where 401 was expected", ProtokitePlaytest.WhyNotTheRefusal(Refusal(404, "No playtest"), 401));
            StringAssert.Contains("answered HTTP 500, where 404 was expected", ProtokitePlaytest.WhyNotTheRefusal(Refusal(500, "boom"), 404));
            Assert.IsNull(ProtokitePlaytest.WhyNotTheRefusal(Refusal(401, "Invalid API Key"), 401));
        }

        [Test]
        public void AFormRefusalMustNameTheQuestionLetterForLetter()
        {
            Assert.IsNull(ProtokitePlaytest.WhyNotTheRefusal(Refusal(422, "Missing required answer 'rating'"), 422, "rating"));
            StringAssert.Contains("for question 'category', where 'rating'", ProtokitePlaytest.WhyNotTheRefusal(Refusal(422, "Invalid option for 'category'"), 422, "rating"));
            StringAssert.Contains("for question 'Rating', where 'rating'", ProtokitePlaytest.WhyNotTheRefusal(Refusal(422, "Missing required answer 'Rating'"), 422, "rating"));
            StringAssert.Contains("named no question", ProtokitePlaytest.WhyNotTheRefusal(Refusal(422, "Provide steam_id or device_id"), 422, "rating"));
        }

        [Test]
        public void AnOptionNotOnTheListDiffersFromEveryOption()
        {
            Assert.AreEqual("BUG", ProtokitePlaytest.AnOptionNotOnTheList(new[] { "Bug", "Crash" }));
            Assert.AreEqual("bug", ProtokitePlaytest.AnOptionNotOnTheList(new[] { "BUG", "Crash" }));
            Assert.AreEqual("1 (not on the list)", ProtokitePlaytest.AnOptionNotOnTheList(new[] { "1" }));
            Assert.AreEqual("Bug (not on the list)", ProtokitePlaytest.AnOptionNotOnTheList(new[] { "Bug", "BUG", "bug" }));
        }

        [Test]
        public void ASwappedHeaderReplacesTheOneThereWhateverItsLetterCase()
        {
            Dictionary<string, string> headers = new Dictionary<string, string> { ["x-flock-api-key"] = "real", ["X-Game-Version-ID"] = "v" };
            Dictionary<string, string> swapped = ProtokitePlaytest.WithHeader(headers, "X-Flock-API-Key", "wrong");
            Assert.AreEqual(2, swapped.Count, "One key header, not two");
            Assert.AreEqual("wrong", swapped["X-Flock-API-Key"]);
            Dictionary<string, string> removed = ProtokitePlaytest.WithHeader(headers, "X-Flock-API-Key", null);
            CollectionAssert.AreEqual(new[] { "X-Game-Version-ID" }, removed.Keys);
            Assert.AreEqual("real", headers["x-flock-api-key"], "The headers handed in are left as they were");
        }

        [Test]
        public void TheSelfTestsPublicSurfaceIsWhatItsDocsName()
        {
            string[] members = typeof(ProtokitePlaytestSelfTest).GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(member => !(member is MethodInfo method) || !method.IsSpecialName)
                .Select(member => member.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "IsRunning", "RunAsync" }, members);
        }

        private static FlockException Refusal(int status, string said) => new FlockValidationException("Validation failed") { StatusCode = status, ServerMessage = said };

        /// <summary>Protokite's SDK routes as the real one answers them: the key, the version, the body and the session decide each answer.</summary>
        private sealed class FakeProtokite
        {
            private const string RealKey = "test-key";
            private readonly string _playtestVersion;
            private readonly string _config;
            private readonly JObject _form;
            private readonly List<string> _sessions = new List<string>();

            public FlockFakeTransport Transport;
            public bool AcceptEverything;
            public int AnswerEveryRequestWith;
            public int ConfigAnswers;
            public readonly List<FlockHttpRequest> SessionStarts = new List<FlockHttpRequest>();
            public readonly List<FlockHttpRequest> LinkRequests = new List<FlockHttpRequest>();
            public readonly List<string> EndsAsked = new List<string>();
            public readonly List<JObject> FormsStored = new List<JObject>();

            public FakeProtokite(string config, string playtestVersion = "test-gvid")
            {
                _playtestVersion = playtestVersion;
                _config = config;
                _form = JObject.Parse(config)["result"]["form"] as JObject;
            }

            public FlockHttpResponse Answer(FlockHttpRequest request)
            {
                string path = new Uri(request.Url).AbsolutePath;
                if (!path.StartsWith("/game/sdk/", StringComparison.Ordinal))
                    return FlockFakeTransport.Ok("{}");
                if (path.EndsWith("/playtest-session", StringComparison.Ordinal))
                    SessionStarts.Add(request);
                if (path.EndsWith("/recording-upload", StringComparison.Ordinal))
                    LinkRequests.Add(request);
                if (path.EndsWith("/end", StringComparison.Ordinal))
                    EndsAsked.Add(path.Split('/')[4]);
                if (AnswerEveryRequestWith != 0)
                    return FlockFakeTransport.Status(AnswerEveryRequestWith, "{\"detail\":\"Internal Server Error\"}");
                if (AcceptEverything)
                    return Accepted(path, request);

                request.Headers.TryGetValue("X-Flock-API-Key", out string key);
                if (key == null)
                    return Refuse(422, "[{\"type\":\"missing\",\"loc\":[\"header\",\"X-Flock-API-Key\"],\"msg\":\"Field required\"}]");
                if (key != RealKey)
                    return Refuse(401, "\"Invalid API Key\"");
                request.Headers.TryGetValue("X-Game-Version-ID", out string version);
                bool closed = version == ClosedVersion;
                if (version != null && version != _playtestVersion && !closed)
                    return Refuse(404, "\"No playtest is linked to this Flock SDK version\"");

                JObject body = string.IsNullOrEmpty(request.JsonBody) ? new JObject() : JObject.Parse(request.JsonBody);
                if (path.EndsWith("/playtest-config", StringComparison.Ordinal))
                {
                    ConfigAnswers++;
                    return FlockFakeTransport.Ok(_config);
                }
                if (path.EndsWith("/playtest-session", StringComparison.Ordinal))
                {
                    if (closed)
                        return Refuse(400, "\"This playtest is no longer collecting SDK sessions\"");
                    if (body["steam_id"] == null && body["device_id"] == null)
                        return Refuse(422, "\"Provide steam_id or device_id so the session can be tied to a Flock player\"");
                    return Accepted(path, request);
                }
                string[] parts = path.Split('/');
                if (path.EndsWith("/recording-upload", StringComparison.Ordinal))
                    return _sessions.Contains(parts[4]) ? Accepted(path, request) : Refuse(404, "\"Playtest session not found\"");
                if (path.EndsWith("/end", StringComparison.Ordinal))
                    return _sessions.Contains(parts[4]) ? FlockFakeTransport.Status(204, "") : Refuse(404, "\"Playtest session not found\"");
                if (path.EndsWith("/feedback-form", StringComparison.Ordinal))
                    return AnswerAForm(body, request);
                return Refuse(404, "\"Not Found\"");
            }

            private FlockHttpResponse AnswerAForm(JObject body, FlockHttpRequest request)
            {
                if (string.IsNullOrEmpty((string)body["steam_id"]) && string.IsNullOrEmpty((string)body["device_id"]))
                    return Refuse(422, "\"Provide steam_id or device_id so the response can be tied to a Flock player\"");
                if (_form == null)
                    return Refuse(404, "\"No published feedback form\"");
                string session = (string)body["session_id"];
                if (!string.IsNullOrEmpty(session) && !_sessions.Contains(session))
                    return Refuse(404, "\"Playtest session not found\"");
                JObject answers = body["answers"] as JObject ?? new JObject();
                foreach (JObject field in _form["fields"])
                {
                    string id = (string)field["id"];
                    if ((bool)field["required"] && answers[id] == null)
                        return Refuse(422, $"\"Missing required answer '{id}'\"");
                    if ((string)field["type"] == "select" && answers[id] != null && !field["options"].Any(o => (string)o == (string)answers[id]))
                        return Refuse(422, $"\"Invalid option for '{id}'\"");
                }
                return Accepted("/feedback-form", request);
            }

            private FlockHttpResponse Accepted(string path, FlockHttpRequest request)
            {
                if (path.EndsWith("/playtest-config", StringComparison.Ordinal))
                    return FlockFakeTransport.Ok(_config);
                if (path.EndsWith("/playtest-session", StringComparison.Ordinal))
                {
                    string id = "pk-" + (_sessions.Count + 1);
                    _sessions.Add(id);
                    return FlockFakeTransport.Ok("{\"result\":{\"session_id\":\"" + id + "\"}}");
                }
                if (path.EndsWith("/recording-upload", StringComparison.Ordinal))
                    return FlockFakeTransport.Ok("{\"result\":{\"upload_url\":\"http://storage.test/r?X-Amz-Signature=1\",\"bucket\":\"b\",\"key\":\"k\"}}");
                if (path.EndsWith("/end", StringComparison.Ordinal))
                    return FlockFakeTransport.Status(204, "");
                FormsStored.Add(JObject.Parse(request.JsonBody));
                return FlockFakeTransport.Ok("{\"result\":{\"id\":\"form-" + FormsStored.Count + "\"}}");
            }

            private static FlockHttpResponse Refuse(int status, string detail) => FlockFakeTransport.Status(status, "{\"detail\":" + detail + "}");
        }

        /// <summary>Takes the recording's upload: 200 unless told otherwise, and can keep the file open so it cannot be deleted.</summary>
        private sealed class StorageForTests : IFlockFileUploader
        {
            private FileStream _keptOpen;
            public int Answer = 200;
            public bool KeepTheFileOpen;
            public int Uploads;

            public Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Uploads);
                if (KeepTheFileOpen && _keptOpen == null)
                    _keptOpen = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long bytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
                return Task.FromResult(new FlockFileUploadOutcome { Result = FlockHttpResult.Success, StatusCode = Answer, BytesSent = bytes, Body = Answer == 200 ? "" : "<Error><Code>InternalError</Code></Error>" });
            }

            public void LetGo()
            {
                _keptOpen?.Dispose();
                _keptOpen = null;
            }
        }
    }
}
