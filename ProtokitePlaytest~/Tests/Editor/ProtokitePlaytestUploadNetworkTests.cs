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
    /// <summary>The question about upload networks: what each answer allows, the file it is kept in, when a phone asks it, and what the session start carries.</summary>
    public class ProtokitePlaytestUploadNetworkTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string EndRoute = "/end";
        private const string FlockSessionRoute = "/analytics/sessions";
        private const string ServerSessionId = "01K5SRVSESSION000000000001";
        private const string NetworkQuestionCannotBeDrawn = "the question about which networks recordings may upload on cannot be drawn here";

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private readonly List<string> _warnings = new List<string>();

        private static string Config(bool video) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":"
            + (video ? "true" : "false") + "},\"form\":null}}";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests(askThePlayerForPlaytestConsent: true);
            _folder = Path.Combine(Path.GetTempPath(), "protokite_upload_network_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            ProtokitePlaytest.VideoEncoderForTesting = () => new FakeH264Encoder();
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => new FakeFrameSource();
            _warnings.Clear();
            Application.logMessageReceived += HearWarnings;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= HearWarnings;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = false;
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = null;
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            string uploadNetworkFile = _settings.UploadNetworkFilePath;
            if (File.Exists(uploadNetworkFile))
                File.SetAttributes(uploadNetworkFile, FileAttributes.Normal);
            _settings.Dispose();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private void HearWarnings(string message, string stack, LogType type)
        {
            if (type == LogType.Warning)
                _warnings.Add(message);
        }

        private int WarningsSaying(string words) => _warnings.FindAll(warning => warning.Contains(words)).Count;

        private static FlockTestClient StartFlock(bool video = true)
        {
            FlockFakeTransport transport = new FlockFakeTransport()
                .On(EndRoute, FlockFakeTransport.Status(204, ""))
                .On(StartRoute, FlockFakeTransport.Ok("{\"result\":{\"session_id\":\"pk-1\"}}"))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(video)))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"));
            FlockTestClient flock = FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy { MaxRetries = 0, InitialDelay = TimeSpan.Zero });
            flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            flock.Run(() => flock.Client.Analytics.StartSessionAsync());
            return flock;
        }

        private void OnAPhoneThatRecords()
        {
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = true;
            _settings.Settings.RecordVideoOnAndroid = true;
        }

        private void PlayerLets(ProtokitePlaytestConsentChoice consent)
            => Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(consent));

        private static IEnumerator Settled(Func<bool> done, float seconds, string what)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
            {
                ProtokitePlaytest.Refresh();
                yield return null;
            }
            Assert.IsTrue(done(), what + $" within {seconds} s");
        }

        // A check that something did not happen waits real time, not frames: a batchmode editor runs many frames within one timer tick.
        private static IEnumerator ForAWhile(float seconds = 1f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                ProtokitePlaytest.Refresh();
                yield return null;
            }
        }

        private static IEnumerator TheSessionStarts(FlockTestClient flock)
        {
            yield return Settled(() => ProtokitePlaytest.PlaytestSessionId != null, 5f, "The session started");
            Assert.AreEqual(1, flock.Transport.CountTo(StartRoute));
        }

        private static string SentUploadNetwork(FlockTestClient flock)
            => (string)JObject.Parse(flock.Transport.LastTo(StartRoute).JsonBody)["extra_debug"]["playtest_upload_network"];

        // The rules

        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, NetworkReachability.ReachableViaLocalAreaNetwork, true)]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, NetworkReachability.ReachableViaCarrierDataNetwork, false)]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, NetworkReachability.NotReachable, false)]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, NetworkReachability.ReachableViaLocalAreaNetwork, true)]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, NetworkReachability.ReachableViaCarrierDataNetwork, true)]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, NetworkReachability.NotReachable, true)]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.NotAnswered, NetworkReachability.ReachableViaCarrierDataNetwork, true)]
        public void EachAnswerAllowsItsOwnNetworks(ProtokitePlaytestUploadNetworkChoice choice, NetworkReachability network, bool allowed)
        {
            // An answer that allows mobile data still tries with no network, and fails as any upload does.
            Assert.AreEqual(allowed, ProtokitePlaytestUploadNetwork.AllowsUploadOn(choice, () => network));
        }

        [Test]
        public void TheNetworkIsReadOnlyForAnAnswerThatCanHoldUploadsBack()
        {
            int read = 0;
            Func<NetworkReachability> network = () =>
            {
                read++;
                return NetworkReachability.ReachableViaCarrierDataNetwork;
            };
            Assert.IsTrue(ProtokitePlaytestUploadNetwork.AllowsUploadOn(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, network));
            Assert.IsTrue(ProtokitePlaytestUploadNetwork.AllowsUploadOn(ProtokitePlaytestUploadNetworkChoice.NotAnswered, network));
            Assert.AreEqual(0, read);
            Assert.IsFalse(ProtokitePlaytestUploadNetwork.AllowsUploadOn(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, network));
            Assert.AreEqual(1, read, "Control: Wi-Fi only reads it");
        }

        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, "wifi_only")]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, "wifi_and_mobile_data")]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.NotAnswered, "not_asked")]
        public void EachAnswerIsSpeltTheWayEverySessionSendsIt(ProtokitePlaytestUploadNetworkChoice choice, string wire)
        {
            Assert.AreEqual(wire, ProtokitePlaytestUploadNetwork.ToWire(choice));
            Assert.AreEqual(choice, ProtokitePlaytestUploadNetwork.FromWire(wire));
        }

        [TestCase("WIFI_ONLY")]
        [TestCase("Wifi_Only")]
        [TestCase(" wifi_only")]
        [TestCase("wifi")]
        [TestCase("")]
        [TestCase(null)]
        public void AnyOtherSpellingIsNotAnAnswer(string wire)
            => Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.NotAnswered, ProtokitePlaytestUploadNetwork.FromWire(wire));

        [TestCase(1500, 11)]
        [TestCase(100, 1)]
        [TestCase(20000, 150)]
        public void TheQuestionSaysAboutHowMuchMobileDataAMinuteTakes(int bitrateKbps, int megabytesAMinute)
        {
            Assert.AreEqual(megabytesAMinute, ProtokitePlaytestUploadNetwork.MegabytesAMinute(bitrateKbps));
            StringAssert.Contains($"about {megabytesAMinute} MB of your mobile data for each minute", ProtokitePlaytestUploadNetworkQuestionView.WiFiAndMobileDataExplanation(megabytesAMinute));
        }

        // The file

        [Test]
        public void AnAnswerIsSavedBesideTheConsentAnswerReadBackAndForgotten()
        {
            ProtokitePlaytestUploadNetworkFile file = new ProtokitePlaytestUploadNetworkFile(_settings.UploadNetworkFilePath);
            PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
            Assert.AreEqual(Path.GetDirectoryName(_settings.ConsentFilePath), Path.GetDirectoryName(file.Path), "Beside the consent answer");

            Assert.IsTrue(file.Save(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
            StringAssert.Contains("\"playtest_upload_network\":\"wifi_only\"", File.ReadAllText(file.Path));
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, file.Read());

            Assert.IsTrue(file.Save(ProtokitePlaytestUploadNetworkChoice.NotAnswered), "Not answered is never saved: the file goes");
            Assert.IsFalse(File.Exists(file.Path));
            Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoOnly, new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Read(), "The consent answer beside it is left alone");
        }

        [TestCase("{\"playtest_upload_network\":\"not_asked\"}")]
        [TestCase("{\"playtest_upload_network\":true}")]
        [TestCase("{\"playtest_consent\":\"wifi_only\"}")]
        [TestCase("not json")]
        public void AFileHoldingAnythingElseReadsAsNotAnswered(string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settings.UploadNetworkFilePath));
            File.WriteAllText(_settings.UploadNetworkFilePath, contents);
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.NotAnswered, new ProtokitePlaytestUploadNetworkFile(_settings.UploadNetworkFilePath).Read());
        }

        [Test]
        public void ANewLaunchReadsTheSavedAnswer()
        {
            Assert.IsTrue(ProtokitePlaytest.SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData));
            ProtokitePlaytest.ResetForNewLaunch();
            File.WriteAllText(_settings.UploadNetworkFilePath, "{\"playtest_upload_network\":\"wifi_only\"}");
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, ProtokitePlaytest.PlayersUploadNetworkAnswer, "Read from the file, not remembered from the launch before");
        }

        // A read-only file stops a replace on Windows only; macOS and Linux let it go ahead.
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AnAnswerThatCannotBeSavedHoldsForTheLaunchAndTheNextLaunchAsksAgain()
        {
            Assert.IsTrue(ProtokitePlaytest.SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
            File.SetAttributes(_settings.UploadNetworkFilePath, FileAttributes.ReadOnly);
            LogAssert.Expect(LogType.Warning, new Regex(@"could not be saved to .*playtest_upload_network\.json; it holds for this launch, and the next launch asks again"));

            Assert.IsFalse(ProtokitePlaytest.SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData));
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, ProtokitePlaytest.PlayersUploadNetworkAnswer, "What the player chose holds for the launch");
            Assert.IsFalse(File.Exists(_settings.UploadNetworkFilePath), "Not the answer before, which the player replaced");
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.NotAnswered, ProtokitePlaytest.PlayersUploadNetworkAnswer);
        }

        // When a phone asks, and what the session start carries

        [UnityTest]
        public IEnumerator OnAPhoneTheSessionWaitsForTheAnswerWhileTheScreenIsRecordedAndCarriesIt()
        {
            OnAPhoneThatRecords();
            PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
            using (FlockTestClient flock = StartFlock())
            {
                LogAssert.Expect(LogType.Log, new Regex("The Protokite session starts once the player says which networks recordings may upload on"));
                yield return ForAWhile();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: loaded and allowed to record the screen");
                Assert.AreEqual(0, flock.Transport.CountTo(StartRoute), "The session waits, so it carries the answer");
                ProtokitePlaytest.UpdateVideo(1.0 / 60.0);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "The answer holds back uploads, never the recording");
                Assert.AreEqual(1, WarningsSaying(NetworkQuestionCannotBeDrawn), "Said once that the question cannot be drawn here, as the consent question does");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
                yield return TheSessionStarts(flock);
                Assert.AreEqual("wifi_only", SentUploadNetwork(flock));
                StringAssert.Contains("\"playtest_upload_network\":\"wifi_only\"", File.ReadAllText(_settings.UploadNetworkFilePath));
            }
        }

        [UnityTest]
        public IEnumerator OnWindowsTheQuestionIsNeverAskedAndTheSessionSaysSo()
        {
            _settings.Settings.RecordVideoOnAndroid = true;
            PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock));
                Assert.AreEqual(0, WarningsSaying(NetworkQuestionCannotBeDrawn));
            }
        }

        [UnityTest]
        public IEnumerator AnAnswerThatDoesNotLetTheScreenBeRecordedIsNeverFollowedByIt()
        {
            OnAPhoneThatRecords();
            PlayerLets(ProtokitePlaytestConsentChoice.PlayDataOnly);
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock));
                Assert.AreEqual(0, WarningsSaying(NetworkQuestionCannotBeDrawn));
            }
        }

        [UnityTest]
        public IEnumerator APlaytestThatRecordsNoVideoIsNeverFollowedByIt()
        {
            OnAPhoneThatRecords();
            PlayerLets(ProtokitePlaytestConsentChoice.VideoAndPlayData);
            using (FlockTestClient flock = StartFlock(video: false))
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock));
                Assert.AreEqual(0, WarningsSaying(NetworkQuestionCannotBeDrawn));
            }
        }

        [UnityTest]
        public IEnumerator APhoneWhoseVideoTheStudioTurnedOffIsNeverAsked()
        {
            OnAPhoneThatRecords();
            _settings.Settings.RecordVideoOnAndroid = false;
            PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock));
            }
        }

        [UnityTest]
        public IEnumerator APhoneThatHasSaidItRecordsNoVideoIsNeverAsked()
        {
            OnAPhoneThatRecords();
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(null, "this phone offers no H.264 encoder.");
            ProtokitePlaytestVideoEncoders.StartLookingForEncoders()?.Join(TimeSpan.FromSeconds(10));
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FinishedLookingForEncoders(), "Precondition: the phone has answered");
            PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock), "Nothing will be recorded to upload");
                Assert.AreEqual(0, WarningsSaying(NetworkQuestionCannotBeDrawn));
            }
        }

        [UnityTest]
        public IEnumerator APhoneStillLookingForItsEncodersIsAsked()
        {
            // Control for the test above: an answer not yet given is no reason to skip the question.
            OnAPhoneThatRecords();
            ManualResetEventSlim answer = new ManualResetEventSlim();
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
            {
                answer.Wait(TimeSpan.FromSeconds(10));
                return new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(null, "this phone offers no H.264 encoder.");
            };
            ProtokitePlaytestVideoEncoders.StartLookingForEncoders();
            try
            {
                PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
                using (FlockTestClient flock = StartFlock())
                {
                    yield return ForAWhile(0.5f);
                    Assert.AreEqual(0, flock.Transport.CountTo(StartRoute), "Asked, so the session waits");
                    Assert.AreEqual(1, WarningsSaying(NetworkQuestionCannotBeDrawn));
                }
            }
            finally
            {
                answer.Set();
            }
        }

        [UnityTest]
        public IEnumerator AndroidVideoTurnedOnMidLaunchAsksNothingThisLaunch()
        {
            OnAPhoneThatRecords();
            _settings.Settings.RecordVideoOnAndroid = false;
            ProtokitePlaytest.StartFinishingEarlierRecordings();
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)), "Precondition: the launch read its settings");
            _settings.Settings.RecordVideoOnAndroid = true;
            PlayerLets(ProtokitePlaytestConsentChoice.VideoOnly);
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock), "Off at launch holds for the launch, as it does for the recording");
            }
        }

        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, "Wi-Fi only")]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, "Wi-Fi and mobile data")]
        [TestCase(ProtokitePlaytestUploadNetworkChoice.NotAnswered, "Not answered")]
        public void TheSetupWindowNamesTheAnswerInPlainWords(ProtokitePlaytestUploadNetworkChoice choice, string shown)
            => Assert.AreEqual(shown, Protokite.Playtest.Editor.ProtokitePlaytestWindow.UploadNetworkAnswerAsRead(choice));

        [UnityTest]
        public IEnumerator ABuildThatStopsAskingSendsAndKeepsTheAnswerAlreadyGiven()
        {
            OnAPhoneThatRecords();
            _settings.Settings.AskThePlayerForPlaytestConsent = false;
            Assert.IsTrue(new ProtokitePlaytestUploadNetworkFile(_settings.UploadNetworkFilePath).Save(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("wifi_only", SentUploadNetwork(flock), "The setting decides whether the question is put, never whether an answer counts");
            }
        }

        [UnityTest]
        public IEnumerator ABuildThatAsksNobodySendsNotAsked()
        {
            OnAPhoneThatRecords();
            _settings.Settings.AskThePlayerForPlaytestConsent = false;
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);
                Assert.AreEqual("not_asked", SentUploadNetwork(flock));
            }
        }

        [UnityTest]
        public IEnumerator AskingAgainPutsTheQuestionAboutNetworksAfterAnAnswerThatLetsTheScreenBeRecorded()
        {
            OnAPhoneThatRecords();
            PlayerLets(ProtokitePlaytestConsentChoice.PlayDataOnly);
            Assert.IsTrue(new ProtokitePlaytestUploadNetworkFile(_settings.UploadNetworkFilePath).Save(ProtokitePlaytestUploadNetworkChoice.WiFiOnly));
            using (FlockTestClient flock = StartFlock())
            {
                yield return TheSessionStarts(flock);

                // Control: an answer given in the game's own menu, with nobody asking again, puts no question about networks.
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData));
                yield return ForAWhile(0.2f);
                Assert.AreEqual(0, WarningsSaying(NetworkQuestionCannotBeDrawn), "Already answered, and nobody asked again");

                ProtokitePlaytest.AskForPlaytestConsent();
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoOnly));
                yield return ForAWhile(0.2f);
                Assert.AreEqual(1, WarningsSaying(NetworkQuestionCannotBeDrawn), "Asked again, the player is asked about networks too");
                Assert.AreEqual(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, ProtokitePlaytest.PlayersUploadNetworkAnswer, "The answer before holds until they choose");
            }
        }
    }
}
