using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>Filled-in forms reaching Protokite: kept on disk first, sent by one sending at a time, and deleted only once taken or refused for good.</summary>
    public class ProtokitePlaytestFormSendingTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string FormRoute = "/game/sdk/feedback-form";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string EndRoute = "/end";
        private const string FlockSessionRoute = "/analytics/sessions";
        private const string ServerSessionId = "01K5SRVSESSION000000000001";

        private const string FormJson =
            "{\"id\":\"form-1\",\"test_id\":\"t\",\"game_id\":\"g\",\"title\":\"How was it?\",\"description\":null,\"is_published\":true,\"fields\":["
            + "{\"id\":\"rating\",\"type\":\"rating\",\"label\":\"How was this session?\",\"required\":true,\"help_text\":null,\"options\":[]},"
            + "{\"id\":\"category\",\"type\":\"select\",\"label\":\"What is this about?\",\"required\":true,\"help_text\":null,\"options\":[\"Bug\",\"Crash\"]},"
            + "{\"id\":\"steps\",\"type\":\"textarea\",\"label\":\"Steps to reproduce\",\"required\":false,\"help_text\":null,\"options\":[]}],"
            + "\"created_at\":\"2026-09-29T10:00:00\",\"updated_at\":\"2026-09-29T10:00:00\"}";

        private static string Config(bool form = true) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{},\"form\":"
            + (form ? FormJson : "null") + "}}";

        private static string Stored(string id) =>
            "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":{\"id\":\"" + id + "\",\"form_id\":\"form-1\",\"test_id\":\"t\","
            + "\"session_id\":null,\"steam_id\":null,\"device_id\":\"d\",\"answers\":{},\"created_at\":\"2026-09-29T10:00:00\"}}";

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private string _formsBefore;
        private bool _reachable;
        private DateTime _now;

        private string Forms => Path.Combine(_folder, "FeedbackForms");

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_forms_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            _formsBefore = ProtokitePlaytest.FeedbackFormsFolderForTesting;
            Assert.IsNotNull(_formsBefore, "Precondition: the run keeps waiting forms out of the project's own folder");
            ProtokitePlaytest.FeedbackFormsFolderForTesting = Forms;
            _reachable = true;
            _now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
            ProtokitePlaytest.ReachabilityForTesting = () => _reachable;
            ProtokitePlaytest.ClockForTesting = () => _now;
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.FeedbackFormsFolderForTesting = _formsBefore;
            ProtokitePlaytest.ReachabilityForTesting = null;
            ProtokitePlaytest.ClockForTesting = null;
            ProtokitePlaytest.SetSteamId(null);
            _settings.Dispose();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static FlockFakeTransport Transport(FlockHttpResponse form = null, bool withForm = true)
            => new FlockFakeTransport()
                .On(FormRoute, form ?? FlockFakeTransport.Ok(Stored("resp-1")))
                .On(EndRoute, FlockFakeTransport.Status(204, ""))
                .On(StartRoute, FlockFakeTransport.Ok("{\"result\":{\"session_id\":\"pk-1\"}}"))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(withForm)))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"));

        private static FlockTestClient StartFlock(FlockFakeTransport transport, int retries = 0)
            => FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy { MaxRetries = retries, InitialDelay = TimeSpan.Zero, UseJitter = false });

        // The config loads on the first Refresh; the second sees it.
        private static void Loaded()
        {
            ProtokitePlaytest.Refresh();
            ProtokitePlaytest.Refresh();
            Assert.IsNotNull(ProtokitePlaytest.FeedbackForm, "Precondition: the playtest and its form loaded");
        }

        private static ProtokitePlaytestFormAnswers GoodAnswers()
        {
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetRating("rating", 4);
            answers.SetChosenOption("category", "Bug");
            answers.SetText("steps", "  jump twice ");
            return answers;
        }

        // A form an earlier launch kept, under a name the test chooses so the order is the test's own.
        private string Plant(string name, string sessionId = "", string url = ProtokitePlaytestSettingsForTests.ProtokiteApiUrl, string version = "test-gvid", string title = "planted")
        {
            Directory.CreateDirectory(Forms);
            string path = Path.Combine(Forms, name + ProtokitePlaytestKeptForms.Extension);
            File.WriteAllText(path, new ProtokitePlaytestFormSubmission
            {
                PlaytestSessionId = sessionId,
                DeviceId = "earlier-device",
                ProtokiteApiUrl = url,
                FlockGameVersionId = version,
                Answers = new JObject { ["rating"] = 3, ["category"] = "Crash", ["steps"] = title }
            }.ToSavedJson());
            return path;
        }

        private string[] Waiting() => Directory.Exists(Forms) ? Directory.GetFiles(Forms, "*.json") : new string[0];

        private static JObject Body(FlockHttpRequest request) => JObject.Parse(request.JsonBody);

        private static IEnumerator Settled(Func<bool> done, float seconds, string what)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(done(), what + $" within {seconds} s");
        }

        // Sending this launch's form

        [Test]
        public void AFormIsKeptThenSentWithTheGamesKeyAndVersionAndDeletedOnceTaken()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                Loaded();
                LogAssert.Expect(LogType.Log, new Regex("The feedback form is kept at .* no Protokite session had started, so it names none"));
                Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                Assert.AreEqual(1, Waiting().Length, "Kept on disk before anything is sent");
                Assert.AreEqual(0, flock.Transport.CountTo(FormRoute), "Sent by the pass, not by the call");

                LogAssert.Expect(LogType.Log, new Regex("A feedback form was sent to Protokite \\(stored as resp-1\\)"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(FormRoute));
                FlockHttpRequest sent = flock.Transport.LastTo(FormRoute);
                Assert.AreEqual("http://protokite.test/game/sdk/feedback-form", sent.Url);
                Assert.AreEqual("POST", sent.Method);
                Assert.AreEqual("test-key", sent.Headers["X-Flock-API-Key"]);
                Assert.AreEqual("test-gvid", sent.Headers["X-Game-Version-ID"]);
                JObject body = Body(sent);
                Assert.IsNull(body["session_id"], "No session had started, and an empty one would be stored as one");
                Assert.IsNull(body["steam_id"]);
                Assert.AreEqual(File.ReadAllText(ProtokitePlaytest.DeviceIdFilePathForTesting).Trim(), (string)body["device_id"], "This install's device id, as the session would name it");
                Assert.AreEqual(4, (int)body["answers"]["rating"]);
                Assert.AreEqual("Bug", (string)body["answers"]["category"]);
                Assert.AreEqual("jump twice", (string)body["answers"]["steps"]);
                Assert.AreEqual(0, Waiting().Length, "Taken, so no longer kept");
            }
        }

        // In a WebGL player the browser keeps only what was copied: a form taken but still in the last copy is sent again next visit.
        [Test]
        public void AFormTakenByProtokiteIsGoneFromTheLastCopyOfTheSavedFiles()
        {
            List<string> waitingAtEachCopy = new List<string>();
            ProtokitePlaytestSavedFiles.CopyToBrowserStorageForTesting = changedPath =>
            {
                if (!changedPath.StartsWith(_folder, StringComparison.Ordinal))
                    return;
                lock (waitingAtEachCopy)
                    waitingAtEachCopy.Add(string.Join(",", Array.ConvertAll(Waiting(), Path.GetFileName)));
            };
            try
            {
                using (FlockTestClient flock = StartFlock(Transport()))
                {
                    Loaded();
                    Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                    string kept = Path.GetFileName(Waiting()[0]);
                    Assert.AreEqual(kept, waitingAtEachCopy[waitingAtEachCopy.Count - 1], "Kept, and copied once kept");

                    ProtokitePlaytest.Refresh();
                    Assert.AreEqual(1, flock.Transport.CountTo(FormRoute), "Precondition: sent");
                    Assert.AreEqual("", waitingAtEachCopy[waitingAtEachCopy.Count - 1], "Deleted once taken, and the deletion copied");
                }
            }
            finally
            {
                ProtokitePlaytestSavedFiles.CopyToBrowserStorageForTesting = null;
            }
        }

        [Test]
        public void ASteamIdSetByTheGameNamesThePlayerInsteadOfTheDeviceId()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                Assert.IsTrue(ProtokitePlaytest.SetSteamId("76561190000000001"));
                Loaded();
                Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                ProtokitePlaytest.Refresh();
                JObject body = Body(flock.Transport.LastTo(FormRoute));
                Assert.AreEqual("76561190000000001", (string)body["steam_id"]);
                Assert.IsNull(body["device_id"]);
            }
        }

        [UnityTest]
        public IEnumerator AFormFilledInOnceTheSessionStartedNamesItsSession()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                Loaded();
                flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());
                ProtokitePlaytest.Refresh();
                yield return Settled(() => ProtokitePlaytest.PlaytestSessionId != null, 5f, "The session started");

                Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                ProtokitePlaytest.Refresh();
                FlockHttpRequest sent = flock.Transport.LastTo(FormRoute);
                Assert.AreEqual("pk-1", (string)Body(sent)["session_id"]);
                Assert.AreEqual("test-gvid", sent.Headers["X-Game-Version-ID"], "The session's own version");
            }
        }

        [Test]
        public void AnswersProtokiteWouldRefuseAreNotKeptAndEveryProblemIsNamed()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                Loaded();
                ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
                answers.SetChosenOption("category", "bug");
                LogAssert.Expect(LogType.Warning, new Regex("not sent, as Protokite would refuse it: 'rating': This one is needed\\. 'category': Choose one of the options given\\."));
                Assert.IsFalse(ProtokitePlaytest.SendFeedbackForm(answers));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, Waiting().Length);
                Assert.AreEqual(0, flock.Transport.CountTo(FormRoute));
            }
        }

        [Test]
        public void APlaytestWithNoFormTakesNoAnswers()
        {
            using (FlockTestClient flock = StartFlock(Transport(withForm: false)))
            {
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the playtest loaded");
                Assert.IsNull(ProtokitePlaytest.FeedbackForm);
                Assert.IsFalse(ProtokitePlaytest.CanOpenFeedbackForm);
                LogAssert.Expect(LogType.Warning, new Regex("not sent: this build's playtest is not loaded or publishes no feedback form"));
                Assert.IsFalse(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                Assert.AreEqual(0, Waiting().Length);
            }
        }

        [Test]
        public void AFormNobodyCanBeNamedForIsRefusedAndNotKept()
        {
            // A folder where the device id file should be: no id can be read or saved.
            Directory.CreateDirectory(ProtokitePlaytest.DeviceIdFilePathForTesting);
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                Loaded();
                LogAssert.Expect(LogType.Warning, new Regex("not sent: nobody can be named as the one who filled it in"));
                Assert.IsFalse(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                Assert.AreEqual(0, Waiting().Length);
            }
        }

        // The player's own message, whatever the consent answer (owner, 2026-09-29)

        [Test]
        public void TheFormIsOfferedAndSentWhateverThePlayersConsentAnswer()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status);
                Assert.IsTrue(ProtokitePlaytest.CanOpenFeedbackForm, "Before any answer");

                ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.Nothing);
                Assert.AreEqual(ProtokitePlaytestStatus.PlayerRefusedPlaytest, ProtokitePlaytest.Status);
                Assert.IsTrue(ProtokitePlaytest.CanOpenFeedbackForm, "After asking for nothing to be collected");
                Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(FormRoute));
                Assert.IsNull(Body(flock.Transport.LastTo(FormRoute))["session_id"], "No session is made for it: the answer holds for everything the playtest collects");
            }
        }

        [Test]
        public void NoFormIsOfferedWhereNoPlaytestIsLoaded()
        {
            _settings.Settings.PlaytestingEnabled = false;
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.Status);
                Assert.IsFalse(ProtokitePlaytest.CanOpenFeedbackForm);
            }
            _settings.Settings.PlaytestingEnabled = true;
            using (FlockTestClient flock = StartFlock(new FlockFakeTransport()
                       .On(ConfigRoute, FlockFakeTransport.Status(404, "{\"detail\":\"No playtest is linked to this Flock SDK version\"}"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("No Protokite playtest is linked"));
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestNotLinked, ProtokitePlaytest.Status);
                Assert.IsFalse(ProtokitePlaytest.CanOpenFeedbackForm);
            }
        }

        [UnityTest]
        public IEnumerator NoFormIsOfferedOnceThePlaytestHasClosed()
        {
            FlockFakeTransport transport = Transport();
            transport.On(StartRoute, FlockFakeTransport.Status(400, "{\"detail\":\"This playtest is no longer collecting sessions\"}"));
            using (FlockTestClient flock = StartFlock(transport))
            {
                Loaded();
                flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());
                LogAssert.Expect(LogType.Warning, new Regex("This playtest has closed"));
                ProtokitePlaytest.Refresh();
                yield return Settled(() => ProtokitePlaytest.Status == ProtokitePlaytestStatus.PlaytestNoLongerCollecting, 5f, "Protokite said the playtest closed");
                Assert.IsNull(ProtokitePlaytest.FeedbackForm, "Its config is still loaded, and the playtest takes nothing more");
                Assert.IsFalse(ProtokitePlaytest.CanOpenFeedbackForm);
            }
        }

        // What decides a form is taken

        [TestCase(200, "<html><body>Sign in to the Wi-Fi</body></html>", "could not be sent now, so the waiting forms are kept", TestName = "APortalsPageIsNotTaken")]
        [TestCase(200, "{\"error\":{\"code\":null},\"result\":null}", "Protokite could not take a feedback form now", TestName = "AnAnswerNamingNoStoredIdIsNotTaken")]
        [TestCase(500, "{\"detail\":\"boom\"}", "Protokite could not take a feedback form now \\(HTTP 500\\)", TestName = "AServerErrorIsNotTaken")]
        [TestCase(503, "{\"detail\":\"Could not reach Flock to verify SDK key\"}", "could not be sent now \\(HTTP 503\\), so the waiting forms are kept", TestName = "AnUnverifiableKeyIsNotTaken")]
        [TestCase(403, "<html>Forbidden</html>", "could not be sent now \\(HTTP 403\\), so the waiting forms are kept", TestName = "ARefusalOnTheWayIsNotTakenAndNamesNoSetting")]
        public void AFormNotTakenIsKeptForALaterTry(int status, string body, string said)
        {
            Plant("20260101-000000-000-a");
            using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Status(status, body))))
            {
                LogAssert.Expect(LogType.Log, new Regex(said));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(FormRoute), "Precondition: it was sent");
                Assert.AreEqual(1, Waiting().Length);
            }
        }

        [TestCase(422, "{\"detail\":\"Missing required answer 'steps'\"}", "the question 'steps'")]
        [TestCase(404, "{\"detail\":\"No published feedback form\"}", "HTTP 404")]
        [TestCase(404, "{\"detail\":\"Playtest session not found\"}", "Playtest session not found")]
        public void AFormProtokiteRefusesForGoodIsDeletedAndTheRefusalNamed(int status, string body, string named)
        {
            Plant("20260101-000000-000-a");
            using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Status(status, body))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("was refused by Protokite (?=.*" + Regex.Escape(named) + ").*It is deleted\\."));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, Waiting().Length, "Sending it every launch would only be refused again");
            }
        }

        [Test]
        public void AKeptFormThatCouldNeverBeSentIsDeletedWithoutARequest()
        {
            Plant("20260101-000000-000-a", url: "not a url");
            Directory.CreateDirectory(Forms);
            File.WriteAllText(Path.Combine(Forms, "20260101-000000-000-b.json"), "{\"device_id\":\"d\"}");
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                LogAssert.Expect(LogType.Warning, new Regex("could never be sent: its Protokite API URL is not an http or https address\\. It is deleted\\."));
                LogAssert.Expect(LogType.Warning, new Regex("could never be sent: it names nowhere to send it\\. It is deleted\\."));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, Waiting().Length);
                Assert.AreEqual(0, flock.Transport.CountTo(FormRoute));
            }
        }

        // One sending at a time, and when it stops

        [Test]
        public void AFailureThatMayPassStopsThePassSoTheOthersWaitUntouched()
        {
            Plant("20260101-000000-000-a", title: "first");
            Plant("20260101-000000-000-b", title: "second");
            using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Offline())))
            {
                LogAssert.Expect(LogType.Log, new Regex("could not be sent now"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(FormRoute), "An offline game does not try every form in turn");
                Assert.AreEqual("first", (string)Body(flock.Transport.LastTo(FormRoute))["answers"]["steps"], "Oldest first");
                Assert.AreEqual(2, Waiting().Length);
            }
        }

        [Test]
        public void AFormProtokiteAnswersWithAnErrorDoesNotHoldBackTheFormsBehindIt()
        {
            Plant("20260101-000000-000-a", title: "first");
            Plant("20260101-000000-000-b", title: "second");
            FlockFakeTransport transport = Transport();
            transport.OnSequence(FormRoute, FlockFakeTransport.Status(500, "{\"detail\":\"boom\"}"), FlockFakeTransport.Ok(Stored("resp-2")));
            using (FlockTestClient flock = StartFlock(transport))
            {
                LogAssert.Expect(LogType.Log, new Regex("Protokite could not take a feedback form now \\(HTTP 500\\).*the forms behind it are tried now"));
                ProtokitePlaytest.Refresh();
                List<FlockHttpRequest> sent = transport.AllTo(FormRoute);
                Assert.AreEqual(2, sent.Count, "Protokite answered the first, so the network works: the second is tried");
                Assert.AreEqual("second", (string)Body(sent[1])["answers"]["steps"]);
                string[] left = Waiting();
                Assert.AreEqual(1, left.Length);
                StringAssert.Contains("-a.json", left[0], "The first stays for a later try");
            }
        }

        [Test]
        public void AFormWhoseFileCannotBeDeletedSaysSo()
        {
            string path = Plant("20260101-000000-000-a");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Status(422, "{\"detail\":\"Missing required answer 'steps'\"}"))))
                {
                    LogAssert.Expect(LogType.Warning, new Regex("was refused by Protokite.*It could not be deleted, so it is tried again later"));
                    ProtokitePlaytest.Refresh();
                    Assert.IsTrue(File.Exists(path));
                }
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        [Test]
        public void TheNetworkIsAskedAboutOnlyWhileFormsWaitAfterAFailure()
        {
            int asked = 0;
            ProtokitePlaytest.ReachabilityForTesting = () =>
            {
                asked++;
                return _reachable;
            };
            FlockFakeTransport transport = Transport();
            using (FlockTestClient flock = StartFlock(transport))
            {
                for (int frame = 0; frame < 5; frame++)
                    ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, asked, "Nothing waits, so nothing needs the network's state");
            }
            ProtokitePlaytest.ResetForNewLaunch();
            Plant("20260101-000000-000-a");
            FlockFakeTransport offline = Transport();
            offline.GoOffline();
            using (FlockTestClient flock = StartFlock(offline))
            {
                LogAssert.Expect(LogType.Log, new Regex("could not be sent now"));
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.Greater(asked, 0, "A failed send left a form waiting: the network coming back is watched for");
            }
        }

        [Test]
        public void TheWaitingFormsGoWhenTheNetworkComesBackAndNotBefore()
        {
            Plant("20260101-000000-000-a");
            FlockFakeTransport transport = Transport();
            transport.GoOffline();
            using (FlockTestClient flock = StartFlock(transport))
            {
                LogAssert.Expect(LogType.Log, new Regex("could not be sent now"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, Waiting().Length, "Precondition: kept");
                transport.GoOnline();
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, transport.CountTo(FormRoute), "Nothing says the network came back, and the retry is not due");

                _reachable = false;
                ProtokitePlaytest.Refresh();
                _reachable = true;
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(2, transport.CountTo(FormRoute));
                Assert.AreEqual(0, Waiting().Length);
            }
        }

        [Test]
        public void TheWaitingFormsAreTriedAgainOnceTheRetryIsDue()
        {
            Plant("20260101-000000-000-a");
            FlockFakeTransport transport = Transport();
            transport.GoOffline();
            using (FlockTestClient flock = StartFlock(transport))
            {
                LogAssert.Expect(LogType.Log, new Regex("could not be sent now"));
                ProtokitePlaytest.Refresh();
                transport.GoOnline();
                _now += ProtokitePlaytest.FormRetryInterval - TimeSpan.FromSeconds(1);
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, transport.CountTo(FormRoute), "Not yet due");
                _now += TimeSpan.FromSeconds(1);
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(2, transport.CountTo(FormRoute));
                Assert.AreEqual(0, Waiting().Length);
            }
        }

        [Test]
        public void ARefusedApiKeyKeepsTheFormsForALaterLaunchWithoutRetryingThisOne()
        {
            Plant("20260101-000000-000-a");
            using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Status(401, "{\"detail\":\"Invalid API Key\"}"))))
            {
                LogAssert.Expect(LogType.Warning, new Regex("refused this build's Flock API key when sending a feedback form \\(HTTP 401\\).*Flock > Settings"));
                ProtokitePlaytest.Refresh();
                _now += ProtokitePlaytest.FormRetryInterval + TimeSpan.FromMinutes(1);
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(FormRoute), "This launch's key will not change");
                Assert.AreEqual(1, Waiting().Length);
            }
        }

        [Test]
        public void OnlyAFormNamingASessionIsSentAgainAfterAFailureThatMayHaveReachedProtokite()
        {
            Plant("20260101-000000-000-a", title: "no session");
            using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Status(500, "{\"detail\":\"boom\"}")), retries: 2))
            {
                LogAssert.Expect(LogType.Log, new Regex("Protokite could not take a feedback form now \\(HTTP 500\\)"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, flock.Transport.CountTo(FormRoute), "Every send of it adds a row, so it is not retried within the send");
            }
            ProtokitePlaytest.ResetForNewLaunch();
            Directory.Delete(Forms, true);
            Plant("20260101-000000-000-b", sessionId: "pk-9", title: "with a session");
            using (FlockTestClient flock = StartFlock(Transport(FlockFakeTransport.Status(500, "{\"detail\":\"boom\"}")), retries: 2))
            {
                LogAssert.Expect(LogType.Log, new Regex("Protokite could not take a feedback form now \\(HTTP 500\\)"));
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(3, flock.Transport.CountTo(FormRoute), "Protokite replaces the answer it holds for the session, so it is retried like a read");
            }
        }

        [Test]
        public void AKeptFormGoesWithThisLaunchsKeyButItsOwnVersionAndAddress()
        {
            Plant("20260101-000000-000-a", url: "http://elsewhere.test/", version: "old-gvid");
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                FlockHttpRequest sent = flock.Transport.LastTo(FormRoute);
                Assert.AreEqual("http://elsewhere.test/game/sdk/feedback-form", sent.Url);
                Assert.AreEqual("test-key", sent.Headers["X-Flock-API-Key"], "The launch's own key: a kept form holds none");
                Assert.AreEqual("old-gvid", sent.Headers["X-Game-Version-ID"], "Protokite finds the form's playtest from the version it was filled in under");
                Assert.AreEqual("earlier-device", (string)Body(sent)["device_id"]);
            }
        }

        [UnityTest]
        public IEnumerator AFormKeptWhileAPassIsSendingIsSentOnceByTheNextPass()
        {
            Plant("20260101-000000-000-a", title: "earlier");
            FlockFakeTransport transport = Transport();
            transport.GateNext(FormRoute);
            using (FlockTestClient flock = StartFlock(transport))
            {
                Loaded();
                Assert.AreEqual(1, transport.CountTo(FormRoute), "Precondition: the earlier form is on its way, held");
                Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                for (int frame = 0; frame < 5; frame++)
                {
                    ProtokitePlaytest.Refresh();
                    yield return null;
                }
                Assert.AreEqual(1, transport.CountTo(FormRoute), "One sending at a time");

                transport.ReleaseGate();
                yield return Settled(() =>
                {
                    ProtokitePlaytest.Refresh();
                    return Waiting().Length == 0;
                }, 5f, "Both forms were sent");
                List<FlockHttpRequest> sent = transport.AllTo(FormRoute);
                Assert.AreEqual(2, sent.Count, "Each form once");
                Assert.AreEqual("earlier", (string)Body(sent[0])["answers"]["steps"]);
                Assert.AreEqual("jump twice", (string)Body(sent[1])["answers"]["steps"]);
            }
        }

        [Test]
        public void WaitingFormsGoWithPlaytestingOffOnceFlockRuns()
        {
            _settings.Settings.PlaytestingEnabled = false;
            Plant("20260101-000000-000-a");
            FlockFakeTransport transport = Transport();
            ProtokitePlaytest.Refresh();
            Assert.AreEqual(0, transport.CountTo(FormRoute), "Nothing to send them with until Flock runs");
            using (FlockTestClient flock = StartFlock(transport))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(1, transport.CountTo(FormRoute), "A build with playtesting off never strands a report a playtest build kept");
                Assert.AreEqual(0, Waiting().Length);
            }
        }

        [Test]
        public void NothingIsSentOnceTheGameIsQuitting()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                Loaded();
                ProtokitePlaytest.HandleGameQuitting();
                // A form kept after quitting began makes a sending due; the launch's cancelled token stops it before any request.
                Assert.IsTrue(ProtokitePlaytest.SendFeedbackForm(GoodAnswers()));
                ProtokitePlaytest.Refresh();
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, flock.Transport.CountTo(FormRoute));
                Assert.AreEqual(1, Waiting().Length, "Kept for the next launch");
            }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AFormAnotherLaunchIsSendingIsLeftToIt()
        {
            // Windows only: elsewhere Mono keeps the claim within one process.
            string path = Plant("20260101-000000-000-a");
            using (FileStream otherLaunch = ProtokitePlaytestKeptForms.Claim(path))
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(0, flock.Transport.CountTo(FormRoute));
                Assert.IsTrue(File.Exists(path));
            }
        }
    }
}
