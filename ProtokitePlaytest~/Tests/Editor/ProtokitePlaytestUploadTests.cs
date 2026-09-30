using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Config;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>Recordings reaching their Protokite session: this launch's once finished, and earlier launches' when a later one starts.</summary>
    public class ProtokitePlaytestUploadTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string EndRoute = "/end";
        private const string FlockSessionRoute = "/analytics/sessions";
        private const string ServerSessionId = "01K5SRVSESSION000000000001";
        // Registered before the session start's route, which is part of every link's address: the fake answers the first route that matches.
        private const string LinkRoute = "/playtest-session/pk-1/recording-upload";
        private const string FirstLink = "http://storage.test/recordings/first?X-Amz-Signature=1";
        private const string SecondLink = "http://storage.test/recordings/second?X-Amz-Signature=2";
        private const string ExpiredLink = "<Error><Code>AccessDenied</Code><Message>Request has expired</Message></Error>";
        private const string SignatureRefused = "<Error><Code>SignatureDoesNotMatch</Code><Message>The request signature we calculated does not match</Message></Error>";

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private FakeUploader _uploader;

        private string Recordings => Path.Combine(_folder, "Recordings");

        private static string Config(bool video) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":"
            + (video ? "true" : "false") + "},\"form\":null}}";

        private static string Link(string url) =>
            "{\"result\":{\"upload_url\":\"" + url + "\",\"bucket\":\"protokite-playtest-recordings\",\"key\":\"recordings/t/pk-1/r.webm\"},"
            + "\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null}}";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_upload_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Recordings;
            ProtokitePlaytest.VideoEncoderForTesting = () => new FakeVp8Encoder();
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => new FakeFrameSource();
            _uploader = new FakeUploader();
            FlockHttpClient.UseFileUploader(_uploader);
        }

        [TearDown]
        public void TearDown()
        {
            _uploader.ReleaseEverything();
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = null;
            ProtokitePlaytest.RecordingFileForTesting = null;
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            FlockHttpClient.UseFileUploader(null);
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            _settings.Dispose();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static FlockFakeTransport Transport(bool video = true, Action<FlockFakeTransport> linkRoutes = null)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            if (linkRoutes != null)
                linkRoutes(transport);
            else
                transport.On(LinkRoute, FlockFakeTransport.Ok(Link(FirstLink)));
            return transport
                .On(EndRoute, FlockFakeTransport.Status(204, ""))
                .On(StartRoute, FlockFakeTransport.Ok("{\"result\":{\"session_id\":\"pk-1\"}}"))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(video)))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}"));
        }

        // A retry policy that never waits and never retries, so a refusal is seen once.
        private static FlockTestClient StartFlock(FlockFakeTransport transport)
            => FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy { MaxRetries = 0, InitialDelay = TimeSpan.Zero });

        private static void Frames(int count)
        {
            for (int i = 0; i < count; i++)
                ProtokitePlaytest.UpdateVideo(1.0 / 60.0);
        }

        private static void StartAFlockSession(FlockTestClient flock)
        {
            flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            flock.Run(() => flock.Client.Analytics.StartSessionAsync());
            ProtokitePlaytest.Refresh();
        }

        private static IEnumerator Settled(Func<bool> done, float seconds, string what)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(done(), what + $" within {seconds} s");
        }

        // A check that something did not happen waits real time, not frames: a batchmode editor runs many frames within one timer tick.
        private static IEnumerator ForAWhile(float seconds = 1f, Action eachFrame = null)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                eachFrame?.Invoke();
                yield return null;
            }
        }

        private static IEnumerator TheSessionStarts(FlockTestClient flock)
        {
            StartAFlockSession(flock);
            yield return Settled(() => ProtokitePlaytest.SessionState != ProtokitePlaytestSessionState.Starting, 5f, "The session start settled");
            Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId, "Precondition: the session started");
        }

        // Records a few frames and stops, so the file is finished; the upload then starts on the next frame, as a game's would.
        private static IEnumerator RecordAndStop()
        {
            Frames(30);
            Assert.IsTrue(ProtokitePlaytest.StopVideoRecording(), "Precondition: recording");
            yield return Settled(() => ProtokitePlaytest.VideoRecordingForTesting == null || ProtokitePlaytest.VideoRecordingForTesting.HasFinishedWriting, 20f, "The file was written");
            Frames(1);
            Assert.IsNotNull(ProtokitePlaytest.FinishedVideo?.FilePath, "Precondition: a finished file");
        }

        private static IEnumerator ThisLaunchsUpload()
        {
            yield return Settled(() => ProtokitePlaytest.ThisLaunchsUpload != null && ProtokitePlaytest.ThisLaunchsUpload.IsCompleted, 10f,
                "This launch's upload ended");
        }

        // This launch's recording

        [UnityTest]
        public IEnumerator ThisLaunchsRecordingIsUploadedOnceFinishedAndItsFolderDeleted()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                Assert.AreEqual(0, flock.Transport.CountTo(LinkRoute), "Nothing is asked for while the recording is still being written");

                LogAssert.Expect(LogType.Log, new Regex("Uploading this launch's playtest recording to Protokite session pk-1"));
                LogAssert.Expect(LogType.Log, new Regex("Playtest recording uploaded to Protokite session pk-1"));
                yield return RecordAndStop();
                string video = ProtokitePlaytest.FinishedVideo.FilePath;
                yield return ThisLaunchsUpload();

                Assert.IsTrue(ProtokitePlaytest.ThisLaunchsUpload.Result.Uploaded);
                FlockHttpRequest asked = flock.Transport.LastTo(LinkRoute);
                Assert.AreEqual("http://protokite.test/game/sdk/playtest-session/pk-1/recording-upload", asked.Url);
                Assert.AreEqual("POST", asked.Method);
                Assert.AreEqual("video/webm", (string)JObject.Parse(asked.JsonBody)["content_type"]);
                Assert.AreEqual(1, JObject.Parse(asked.JsonBody).Count, "Webcam and voice are left to the server's false");
                Assert.AreEqual("test-key", asked.Headers["X-Flock-API-Key"]);
                Assert.AreEqual("test-gvid", asked.Headers["X-Game-Version-ID"]);

                FakeUploader.Sent sent = _uploader.Single();
                Assert.AreEqual(FirstLink, sent.Url, "To the link Protokite gave");
                Assert.AreEqual(video, sent.FilePath);
                Assert.AreEqual("video/webm", sent.ContentType, "The type the link was signed for");
                Assert.Greater(sent.FileBytes, 0, "The finished file was there to send");
                Assert.IsFalse(Directory.Exists(runFolder), "Uploaded, so no longer kept on disk");
            }
        }

        [UnityTest]
        public IEnumerator ThePlayerAskingStopsTheRecordingAndUploadsItStraightAway()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(30);
                Assert.IsFalse(ProtokitePlaytest.CanSendTheRecording, "No session yet, so it could not be uploaded at all");
                Assert.IsFalse(ProtokitePlaytest.StopRecordingAndSendIt());
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Asking when it cannot go stops nothing");

                yield return TheSessionStarts(flock);
                Assert.IsTrue(ProtokitePlaytest.CanSendTheRecording);
                LogAssert.Expect(LogType.Log, new Regex("The player asked for their recording to be sent"));
                LogAssert.Expect(LogType.Log, new Regex("It stopped because the player asked for it to be sent"));
                Assert.IsTrue(ProtokitePlaytest.StopRecordingAndSendIt());
                Assert.IsFalse(ProtokitePlaytest.CanSendTheRecording, "Once asked, nothing more is recorded to send");
                yield return Settled(() => ProtokitePlaytest.VideoRecordingForTesting == null || ProtokitePlaytest.VideoRecordingForTesting.HasFinishedWriting, 20f, "The file was written");
                Frames(1);
                yield return ThisLaunchsUpload();
                Assert.IsTrue(ProtokitePlaytest.ThisLaunchsUpload.Result.Uploaded);
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.PlayerAskedToSendIt, ProtokitePlaytest.FinishedVideo.StopReason);
            }
        }

        [UnityTest]
        public IEnumerator ALinkIsNotAnUploadAFailedFileUploadKeepsTheRecording()
        {
            _uploader.Answers.Enqueue(FakeUploader.Status(500, "<Error><Code>InternalError</Code></Error>"));
            _uploader.Answers.Enqueue(FakeUploader.Status(500, "<Error><Code>InternalError</Code></Error>"));
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                LogAssert.Expect(LogType.Warning, new Regex(@"The playtest recording was not uploaded, so it is kept for a later launch to upload: the storage answered HTTP 500 \(InternalError\)"));
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();

                ProtokitePlaytestRecordingUploadOutcome outcome = ProtokitePlaytest.ThisLaunchsUpload.Result;
                Assert.IsFalse(outcome.Uploaded, "A link was given, which Protokite counts; the file never arrived, which is what counts here");
                Assert.AreEqual(1, Directory.GetFiles(runFolder, "*.webm").Length, "The video is kept");
                Assert.IsTrue(File.Exists(Path.Combine(runFolder, "session.json")), "With the session a later launch uploads it to");
                Assert.AreEqual(2, _uploader.Count, "Tried once more");
            }
        }

        [UnityTest]
        public IEnumerator ALinkThatExpiredIsTriedOnceMoreWithAFreshLink()
        {
            _uploader.Answers.Enqueue(FakeUploader.Status(403, ExpiredLink));
            FlockHttpResponse[] links = { FlockFakeTransport.Ok(Link(FirstLink)), FlockFakeTransport.Ok(Link(SecondLink)) };
            using (FlockTestClient flock = StartFlock(Transport(linkRoutes: transport => transport.OnSequence(LinkRoute, links))))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                LogAssert.Expect(LogType.Log, new Regex(@"the storage answered HTTP 403 \(AccessDenied\)\. Asking for a fresh link and trying once more"));
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();

                Assert.IsTrue(ProtokitePlaytest.ThisLaunchsUpload.Result.Uploaded);
                Assert.AreEqual(2, flock.Transport.CountTo(LinkRoute), "A fresh link for the second try");
                Assert.AreEqual(new[] { FirstLink, SecondLink }, _uploader.Urls(), "The second try used the fresh link");
            }
        }

        [UnityTest]
        public IEnumerator NoMoreThanOneMoreTry()
        {
            for (int i = 0; i < 5; i++)
                _uploader.Answers.Enqueue(FakeUploader.Status(403, ExpiredLink));
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                LogAssert.Expect(LogType.Warning, new Regex("kept for a later launch to upload"));
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();
                Assert.AreEqual(2, _uploader.Count);
                Assert.AreEqual(2, flock.Transport.CountTo(LinkRoute));
            }
        }

        [UnityTest]
        public IEnumerator ALinkTheStorageRefusesForItsSignatureIsNotTriedAgain()
        {
            _uploader.Answers.Enqueue(FakeUploader.Status(403, SignatureRefused));
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                LogAssert.Expect(LogType.Warning, new Regex(@"HTTP 403 \(SignatureDoesNotMatch\)"));
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();
                Assert.AreEqual(1, _uploader.Count, "A fresh link would be signed the same way");
                Assert.AreEqual(1, flock.Transport.CountTo(LinkRoute));
            }
        }

        [UnityTest]
        public IEnumerator NoFileIsSentWithoutALinkAndARefusalIsNotBlamedOnTheApiKey()
        {
            using (FlockTestClient flock = StartFlock(Transport(linkRoutes: transport => transport.On(LinkRoute, FlockFakeTransport.Status(403, "{\"detail\":\"Not your playtest session\"}")))))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                LogAssert.Expect(LogType.Warning, new Regex(@"kept for a later launch to upload: Protokite says its session belongs to another game or playtest .* \(HTTP 403\)"));
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();
                Assert.AreEqual(0, _uploader.Count, "No link, no upload");
                StringAssert.DoesNotContain("refused the Flock API key", ProtokitePlaytest.ThisLaunchsUpload.Result.WhyNot);
                // A definite refusal is kept like any other, for a later launch to ask again, until a launch that records needs its room.
                Assert.IsTrue(File.Exists(ProtokitePlaytest.FinishedVideo.FilePath), "The recording is kept");
                Assert.IsTrue(File.Exists(Path.Combine(runFolder, "session.json")), "With its session");
            }
        }

        [UnityTest]
        public IEnumerator AnEarlierRecordingProtokiteNoLongerKnowsIsKeptForALaterLaunch()
        {
            string run = PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(Transport(false, transport => transport
                .On("/playtest-session/pk-9/recording-upload", FlockFakeTransport.Status(404, "{\"detail\":\"Playtest session not found\"}")))))
            {
                LogAssert.Expect(LogType.Log, new Regex(@"was not uploaded, so it is kept: Protokite has no such session.* \(HTTP 404\)"));
                yield return EarlierUploads();

                Assert.AreEqual((0, 1), ProtokitePlaytest.EarlierUploadsForTesting.Result);
                Assert.AreEqual(0, _uploader.Count, "No link, no upload");
                Assert.IsNotNull(ProtokitePlaytestRecordingRun.FinishedVideoPath(run), "The recording is kept");
                Assert.IsTrue(File.Exists(Path.Combine(run, "session.json")), "With its session, to be asked for again");
            }
        }

        [UnityTest]
        public IEnumerator ALinkWithNoUsableAddressSendsNoFile()
        {
            using (FlockTestClient flock = StartFlock(Transport(linkRoutes: transport => transport.On(LinkRoute, FlockFakeTransport.Ok(Link(" "))))))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                LogAssert.Expect(LogType.Warning, new Regex("names no usable upload_url"));
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();
                Assert.AreEqual(0, _uploader.Count);
            }
        }

        [UnityTest]
        public IEnumerator TheLinkAndTheUploadCarryTheRecordingFilesOwnContentType()
        {
            ProtokitePlaytest.RecordingFileForTesting = () => new AnotherKindOfRecordingFile();
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();

                Assert.AreEqual("video/mp4", (string)JObject.Parse(flock.Transport.LastTo(LinkRoute).JsonBody)["content_type"], "Asked for as its own type");
                Assert.AreEqual("video/mp4", _uploader.Single().ContentType, "And sent as it");
                StringAssert.EndsWith(".mp4", _uploader.Single().FilePath);
            }
        }

        [UnityTest]
        public IEnumerator ARecordingFinishedBeforeItsSessionGoesWhenTheSessionStarts()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return RecordAndStop();
                yield return ForAWhile();
                Assert.AreEqual(0, flock.Transport.CountTo(LinkRoute), "No session yet, so nowhere to upload it");

                yield return TheSessionStarts(flock);
                yield return ThisLaunchsUpload();
                Assert.IsTrue(ProtokitePlaytest.ThisLaunchsUpload.Result.Uploaded, "Sent the moment its session started");
            }
        }

        [UnityTest]
        public IEnumerator NothingIsUploadedWhenTheGameQuits()
        {
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                Frames(30);
                ProtokitePlaytest.HandleGameQuitting();
                Assert.IsNotNull(ProtokitePlaytest.FinishedVideo?.FilePath, "Precondition: quitting finished the file");
                yield return ForAWhile();
                Assert.IsNull(ProtokitePlaytest.ThisLaunchsUpload, "No upload even began: the game is closing, and the next launch sends it");
                Assert.AreEqual(0, flock.Transport.CountTo(LinkRoute));
                Assert.IsTrue(File.Exists(Path.Combine(runFolder, "session.json")));
            }
        }

        [UnityTest]
        public IEnumerator ThisLaunchsRecordingGoesWithTheKeyAndVersionItsSessionStartedWith()
        {
            FlockTestClient first = StartFlock(Transport());
            ProtokitePlaytest.Refresh();
            Frames(1);
            yield return TheSessionStarts(first);
            first.Dispose();

            // Flock restarts with another build's version before the recording ends; the session is still the first's.
            FlockFakeTransport restarted = Transport();
            // The setter is core's own; a build with another version is what the test needs, so it is set as a baked config would be.
            using (FlockTestClient.Create(restarted, config => typeof(FlockInitConfig).GetProperty(nameof(FlockInitConfig.GameVersionId)).SetValue(config, "another-gvid")))
            {
                ProtokitePlaytest.Refresh();
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();
                Assert.AreEqual("test-gvid", restarted.LastTo(LinkRoute).Headers["X-Game-Version-ID"], "The version the session started with, which finds its playtest");
                Assert.AreEqual("test-key", restarted.LastTo(LinkRoute).Headers["X-Flock-API-Key"]);
            }
        }

        [UnityTest]
        public IEnumerator AnUploadStillGoingWhenTheGameQuitsStopsAndTheRecordingIsKept()
        {
            _uploader.HoldEach = true;
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                yield return RecordAndStop();
                yield return Settled(() => _uploader.Count == 1, 10f, "The file upload began");

                LogAssert.Expect(LogType.Warning, new Regex("kept for a later launch to upload: the launch ended before the upload finished"));
                ProtokitePlaytest.HandleGameQuitting();
                yield return ThisLaunchsUpload();
                Assert.IsTrue(_uploader.WasCancelled, "The upload was told to stop");
                Assert.IsFalse(ProtokitePlaytest.ThisLaunchsUpload.Result.Uploaded);
                Assert.IsTrue(File.Exists(Path.Combine(runFolder, "session.json")), "Kept, with its session, for the next launch");
                Assert.AreEqual(1, Directory.GetFiles(runFolder, "*.webm").Length);
            }
        }

        [UnityTest]
        [UnityPlatform(RuntimePlatform.WindowsEditor)] // A file another program holds open cannot be deleted on Windows alone.
        public IEnumerator AnUploadedRecordingIsNeverSentAgainThoughNotAllOfItCouldBeDeleted()
        {
            _uploader.KeepFileOpen = true;
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                yield return TheSessionStarts(flock);
                string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                yield return RecordAndStop();
                yield return ThisLaunchsUpload();
                Assert.IsTrue(ProtokitePlaytest.ThisLaunchsUpload.Result.Uploaded);
                Assert.IsTrue(Directory.Exists(runFolder), "Precondition: the video could not be deleted");
                Assert.IsFalse(File.Exists(Path.Combine(runFolder, "session.json")), "Its session is gone, so nothing uploads it again");

                _uploader.ReleaseEverything();
                ProtokitePlaytest.ResetForNewLaunch();
                ProtokitePlaytestEarlierRecordings next = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(Recordings);
                Assert.AreEqual(1, next.RecordingsDeletedWithNoSession, "The next launch deletes what is left rather than upload it");
            }
        }

        // What earlier launches left

        // The player's answer

        [UnityTest]
        public IEnumerator NothingAnEarlierLaunchLeftGoesWhileTheQuestionWaitsAndItGoesOnceAnswered()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status, "Precondition: the question waits");
                yield return ForAWhile(1f, ProtokitePlaytest.Refresh);
                Assert.IsNull(ProtokitePlaytest.EarlierUploadsForTesting, "A player about to ask for nothing has nothing sent while they read");
                Assert.AreEqual(0, flock.Transport.CountTo("/pk-9/recording-upload"));

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                yield return EarlierUploads();
                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result, "An earlier launch recorded it with that launch's permission");
            }
        }

        [UnityTest]
        public IEnumerator AnAnswerOfNothingHoldsEarlierRecordingsBackEvenWithPlaytestingOffUntilAChangeOfMind()
        {
            _settings.Settings.PlaytestingEnabled = false;
            Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(ProtokitePlaytestConsentChoice.Nothing));
            string run = PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                LogAssert.Expect(LogType.Log, new Regex("Nothing an earlier launch recorded is being sent: the player has asked this playtest to collect nothing"));
                yield return ForAWhile(1f, ProtokitePlaytest.Refresh);
                Assert.IsNull(ProtokitePlaytest.EarlierUploadsForTesting, "The answer outlives a build that stops asking, or playtests at all");
                Assert.IsTrue(Directory.Exists(run), "Kept, not given up on");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoOnly));
                yield return EarlierUploads();
                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result, "Sent in the same launch the player changed their mind");
            }
        }

        [UnityTest]
        public IEnumerator ABuildWithPlaytestingOffAndNoAnswerStillSendsWhatEarlierLaunchesLeft()
        {
            _settings.Settings.PlaytestingEnabled = false;
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (StartFlock(EarlierTransport()))
            {
                yield return EarlierUploads();
                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result, "No question is ever put here, so waiting for one would strand them");
            }
        }

        [UnityTest]
        public IEnumerator ABuildThatAsksButWhosePlaytestCannotLoadStillSendsWhatEarlierLaunchesLeft()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            // Built here: the shared transport would answer the config route with a config.
            FlockFakeTransport notLinked = new FlockFakeTransport()
                .On("/playtest-session/pk-9/recording-upload", FlockFakeTransport.Ok(Link(FirstLink)))
                .On(ConfigRoute, FlockFakeTransport.Status(404, "{\"detail\":\"No playtest for this game version\"}"));
            using (StartFlock(notLinked))
            {
                yield return EarlierUploads();
                Assert.AreEqual(ProtokitePlaytestStatus.PlaytestNotLinked, ProtokitePlaytest.Status, "Precondition: no question is ever put");
                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result, "Held only while a question may still come");
            }
        }

        [UnityTest]
        public IEnumerator TakingTheScreenBackRemovesTheSessionAtOnceAndDeletesTheRecordingInsteadOfUploadingIt()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            ProtokitePlaytestConsentFile answer = new ProtokitePlaytestConsentFile(_settings.ConsentFilePath);
            Assert.IsTrue(answer.Save(ProtokitePlaytestConsentChoice.VideoAndPlayData));
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                Frames(10);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: recording");
                yield return TheSessionStarts(flock);
                string run = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                Assert.IsTrue(File.Exists(Path.Combine(run, "session.json")), "Precondition: the session is saved beside the recording");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                Assert.IsFalse(File.Exists(Path.Combine(run, "session.json")), "Gone before the file is even written, so no later launch can send it");

                yield return Settled(() => ProtokitePlaytest.VideoRecordingForTesting == null || ProtokitePlaytest.VideoRecordingForTesting.HasFinishedWriting, 20f, "The file was written");
                Frames(1);
                yield return ForAWhile();
                Assert.IsFalse(Directory.Exists(run), "Deleted instead of uploaded");
                Assert.AreEqual(0, flock.Transport.CountTo(LinkRoute), "No link is asked for");
                Assert.AreEqual(0, _uploader.Count);
            }
        }

        [UnityTest]
        public IEnumerator ASessionThatStartsAfterTheScreenIsTakenBackIsNeverSavedBesideTheRecording()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(ProtokitePlaytestConsentChoice.VideoOnly));
            CloseHeldRecordingFile held = new CloseHeldRecordingFile();
            ProtokitePlaytest.RecordingFileForTesting = () => held;
            try
            {
                using (FlockTestClient flock = StartFlock(Transport()))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(10);
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: recording, before any session");
                    string run = ProtokitePlaytest.RecordingRunForTesting.FolderPath;

                    // The file cannot finish yet, so the session starts in the moment between taking the screen back and the file being written.
                    Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                    yield return TheSessionStarts(flock);
                    Assert.IsFalse(File.Exists(Path.Combine(run, "session.json")), "Never given the session, so a game quitting now leaves nothing a later launch would send");

                    held.Release();
                    yield return Settled(() => ProtokitePlaytest.VideoRecordingForTesting == null || ProtokitePlaytest.VideoRecordingForTesting.HasFinishedWriting, 20f, "The file was written");
                    Frames(1);
                    Assert.IsFalse(Directory.Exists(run), "Deleted once written");
                }
            }
            finally
            {
                held.Release();
            }
        }

        [UnityTest]
        public IEnumerator AFinishedRecordingWaitingForItsSessionIsDeletedWhenTheScreenIsTakenBack()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            Assert.IsTrue(new ProtokitePlaytestConsentFile(_settings.ConsentFilePath).Save(ProtokitePlaytestConsentChoice.VideoOnly));
            using (FlockTestClient flock = StartFlock(Transport()))
            {
                ProtokitePlaytest.Refresh();
                yield return RecordAndStop();
                string run = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                Assert.IsTrue(Directory.Exists(run), "Precondition: kept, waiting for a session to upload to");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.Nothing));
                Assert.IsFalse(Directory.Exists(run), "Deleted at once");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData));
                yield return TheSessionStarts(flock);
                yield return ForAWhile();
                Assert.AreEqual(0, flock.Transport.CountTo(LinkRoute), "Nothing is left to upload");
            }
        }

        private string PlantWaitingRecording(string name, string sessionId, string gameVersionId, string apiUrl = "http://protokite.test")
        {
            string run = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, name, 1000,
                finishedVideo: ProtokitePlaytestPlantedRuns.FinishedVideo(_folder, 3));
            JObject session = new JObject { ["protokite_session_id"] = sessionId, ["protokite_api_url"] = apiUrl };
            if (gameVersionId != null)
                session["flock_game_version_id"] = gameVersionId;
            File.WriteAllText(Path.Combine(run, "session.json"), session.ToString());
            return run;
        }

        private static IEnumerator TheEarlierRecordingsAreGoneThrough()
        {
            ProtokitePlaytest.StartFinishingEarlierRecordings();
            yield return Settled(() => ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.Zero), 10f, "The finishing pass ended");
        }

        private static IEnumerator EarlierUploads()
        {
            yield return Settled(() =>
            {
                ProtokitePlaytest.Refresh();
                return ProtokitePlaytest.EarlierUploadsForTesting != null && ProtokitePlaytest.EarlierUploadsForTesting.IsCompleted;
            }, 10f, "Earlier launches' uploads ended");
        }

        private static FlockFakeTransport EarlierTransport() => Transport(false, transport => transport
            .On("/playtest-session/pk-9/recording-upload", FlockFakeTransport.Ok(Link(FirstLink)))
            .On("/playtest-session/pk-8/recording-upload", FlockFakeTransport.Ok(Link(SecondLink))));

        [UnityTest]
        public IEnumerator AnEarlierLaunchsRecordingGoesWithTheSessionsGameVersionAndThisLaunchsKey()
        {
            string run = PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                LogAssert.Expect(LogType.Log, new Regex("Recordings earlier launches left: 1 uploaded, 0 kept for a later launch"));
                yield return EarlierUploads();

                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result);
                FlockHttpRequest asked = flock.Transport.LastTo("/pk-9/recording-upload");
                Assert.AreEqual("http://protokite.test/game/sdk/playtest-session/pk-9/recording-upload", asked.Url);
                Assert.AreEqual("session-gvid", asked.Headers["X-Game-Version-ID"], "The version its session started with, which is how Protokite finds that session's playtest");
                Assert.AreEqual("test-key", asked.Headers["X-Flock-API-Key"], "This launch's key: the session never saves one");
                Assert.AreEqual("video/webm", _uploader.Single().ContentType);
                Assert.IsFalse(Directory.Exists(run), "Uploaded, so no longer kept");
            }
        }

        [UnityTest]
        public IEnumerator ASessionThatStartedWithNoGameVersionIsAskedForWithNone()
        {
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", null);
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return EarlierUploads();
                Assert.IsFalse(flock.Transport.LastTo("/pk-9/recording-upload").Headers.ContainsKey("X-Game-Version-ID"),
                    "Not this launch's version: Protokite finds the playtest the way it did when the session started");
            }
        }

        [UnityTest]
        public IEnumerator AnEarlierRecordingGoesToTheProtokiteItsSessionStartedOn()
        {
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid", "http://another-protokite.test/");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return EarlierUploads();
                StringAssert.StartsWith("http://another-protokite.test/game/sdk/playtest-session/pk-9", flock.Transport.LastTo("/pk-9/recording-upload").Url);
            }
        }

        [UnityTest]
        public IEnumerator EarlierRecordingsWaitForTheFinishingPass()
        {
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = folder => letItFinish.Wait(TimeSpan.FromSeconds(5));
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                using (FlockTestClient flock = StartFlock(EarlierTransport()))
                {
                    yield return ForAWhile(1f, ProtokitePlaytest.Refresh);
                    Assert.AreEqual(0, flock.Transport.CountTo("/recording-upload"), "Nothing goes while the pass may still be finishing a file");
                    letItFinish.Set();
                    yield return Settled(() => ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.Zero), 10f, "The pass ended");
                    yield return EarlierUploads();
                    Assert.AreEqual(1, flock.Transport.CountTo("/recording-upload"));
                }
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [UnityTest]
        public IEnumerator ARecordingAnotherLaunchHoldsIsLeftToIt()
        {
            string held = PlantWaitingRecording("20260101-000000-00000001", "pk-8", "session-gvid");
            string free = PlantWaitingRecording("20260101-000000-00000002", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (ProtokitePlaytestPlantedRuns.HoldLock(held))
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return EarlierUploads();
                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result);
                Assert.AreEqual(0, flock.Transport.CountTo("/pk-8/recording-upload"), "Another launch is sending it, or still recording it");
                Assert.IsTrue(Directory.Exists(held));
                Assert.IsFalse(Directory.Exists(free));
            }
        }

        [UnityTest]
        public IEnumerator EarlierRecordingsGoOneAtATimeOldestFirst()
        {
            PlantWaitingRecording("20260101-000000-00000001", "pk-8", "session-gvid");
            PlantWaitingRecording("20260101-000000-00000002", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            _uploader.HoldEach = true;
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return Settled(() =>
                {
                    ProtokitePlaytest.Refresh();
                    return _uploader.Count == 1;
                }, 10f, "The first upload began");
                yield return ForAWhile(1f, ProtokitePlaytest.Refresh);
                Assert.AreEqual(1, flock.Transport.CountTo("/recording-upload"), "The second waits for the first");
                Assert.AreEqual(SecondLink, _uploader.Urls()[0], "The oldest first (pk-8's link)");

                _uploader.HoldEach = false;
                _uploader.ReleaseEverything();
                yield return EarlierUploads();
                Assert.AreEqual((2, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result);
            }
        }

        [UnityTest]
        public IEnumerator AStrayFileBesideAnEarlierRecordingIsNotTakenForIt()
        {
            string run = PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            // What a file browser leaves in a folder someone opened; it sorts before the recording.
            File.WriteAllBytes(Path.Combine(run, ".DS_Store"), new byte[10]);
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return EarlierUploads();
                Assert.AreEqual((1, 0), ProtokitePlaytest.EarlierUploadsForTesting.Result);
                StringAssert.EndsWith(".webm", _uploader.Single().FilePath, "The recording, not the stray file");
            }
        }

        [UnityTest]
        public IEnumerator AnEarlierRecordingBeingUploadedCountsAtItsSizeWhenThisLaunchsRecordingMakesRoom()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 4096;
            _settings.Settings.MaxRecordingSizeMb = 1536;
            // It once reserved the whole budget, and its upload holds it while this launch's recording starts.
            string uploading = PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            File.WriteAllText(Path.Combine(uploading, "reserved-bytes.txt"), (4096L * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
            string waiting = PlantWaitingRecording("20260101-000000-00000002", "pk-8", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            _uploader.HoldEach = true;
            using (FlockTestClient flock = StartFlock(Transport(true, transport => transport
                .On("/playtest-session/pk-9/recording-upload", FlockFakeTransport.Ok(Link(FirstLink)))
                .On("/playtest-session/pk-8/recording-upload", FlockFakeTransport.Ok(Link(SecondLink))))))
            {
                yield return Settled(() =>
                {
                    ProtokitePlaytest.Refresh();
                    return _uploader.Count == 1 && ProtokitePlaytest.Status == ProtokitePlaytestStatus.Ready;
                }, 10f, "The oldest one's upload began and the config loaded");
                Frames(1);

                Assert.IsNotNull(ProtokitePlaytest.RecordingRunForTesting, "This launch records: the recording being uploaded takes a few KB, not the 4096 MB it once reserved");
                Assert.AreEqual(1536L * 1024 * 1024, ProtokitePlaytestRecordingRun.ReadReservedBytes(ProtokitePlaytest.RecordingRunForTesting.FolderPath),
                    "Nor is its size limit cut");
                Assert.IsTrue(Directory.Exists(waiting), "Nothing waiting was deleted for room that was free");

                _uploader.HoldEach = false;
                _uploader.ReleaseEverything();
                yield return EarlierUploads();
            }
        }

        [UnityTest]
        public IEnumerator EarlierUploadsStopWhenTheGameQuitsAndKeepTheRecording()
        {
            string run = PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            _uploader.HoldEach = true;
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return Settled(() =>
                {
                    ProtokitePlaytest.Refresh();
                    return _uploader.Count == 1;
                }, 10f, "The upload began");
                LogAssert.Expect(LogType.Log, new Regex("was not uploaded, so it is kept: the launch ended before the upload finished"));
                ProtokitePlaytest.HandleGameQuitting();
                yield return EarlierUploads();
                Assert.AreEqual((0, 1), ProtokitePlaytest.EarlierUploadsForTesting.Result);
                Assert.IsTrue(File.Exists(Path.Combine(run, "session.json")));
            }
        }

        [UnityTest]
        public IEnumerator NothingEarlierLaunchesLeftStartsOnceTheGameHasQuit()
        {
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            yield return TheEarlierRecordingsAreGoneThrough();
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                ProtokitePlaytest.HandleGameQuitting();
                yield return ForAWhile(1f, ProtokitePlaytest.Refresh);
                Assert.IsNull(ProtokitePlaytest.EarlierUploadsForTesting, "The launch is over, so the next one sends them");
                Assert.AreEqual(0, flock.Transport.CountTo("/recording-upload"));
            }
        }

        [UnityTest]
        public IEnumerator NothingEarlierLaunchesLeftGoesUntilTheirRecordingsHaveBeenGoneThrough()
        {
            // No finishing pass this launch (as on WebGL, where none runs): nothing is sent.
            PlantWaitingRecording("20260101-000000-00000001", "pk-9", "session-gvid");
            using (FlockTestClient flock = StartFlock(EarlierTransport()))
            {
                yield return ForAWhile(1f, ProtokitePlaytest.Refresh);
                Assert.IsNull(ProtokitePlaytest.EarlierUploadsForTesting);
                Assert.AreEqual(0, flock.Transport.CountTo("/recording-upload"));
            }
        }

        /// <summary>Takes each file upload the playtest makes, answers as the test says (200 unless told otherwise), and can hold one until released.</summary>
        private sealed class FakeUploader : IFlockFileUploader
        {
            internal sealed class Sent
            {
                public string Url;
                public string FilePath;
                public string ContentType;
                public long FileBytes;
            }

            private readonly List<Sent> _sent = new List<Sent>();
            private readonly List<TaskCompletionSource<FlockFileUploadOutcome>> _held = new List<TaskCompletionSource<FlockFileUploadOutcome>>();
            private FileStream _keptOpen;

            public readonly Queue<FlockFileUploadOutcome> Answers = new Queue<FlockFileUploadOutcome>();
            public volatile bool HoldEach;
            public bool KeepFileOpen;
            public volatile bool WasCancelled;

            public int Count
            {
                get { lock (_sent) return _sent.Count; }
            }

            public static FlockFileUploadOutcome Status(int status, string body)
                => new FlockFileUploadOutcome { Result = FlockHttpResult.Success, StatusCode = status, Body = body };

            public Sent Single()
            {
                lock (_sent)
                {
                    Assert.AreEqual(1, _sent.Count, "One file upload");
                    return _sent[0];
                }
            }

            public string[] Urls()
            {
                lock (_sent)
                    return _sent.ConvertAll(s => s.Url).ToArray();
            }

            public Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType, CancellationToken cancellationToken)
            {
                long bytes = File.Exists(filePath) ? new FileInfo(filePath).Length : -1;
                lock (_sent)
                    _sent.Add(new Sent { Url = url, FilePath = filePath, ContentType = contentType, FileBytes = bytes });
                if (KeepFileOpen && _keptOpen == null)
                    _keptOpen = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                FlockFileUploadOutcome answer = Answers.Count > 0
                    ? Answers.Dequeue()
                    : new FlockFileUploadOutcome { Result = FlockHttpResult.Success, StatusCode = 200, BytesSent = bytes };
                if (!HoldEach)
                    return Task.FromResult(answer);

                TaskCompletionSource<FlockFileUploadOutcome> held = new TaskCompletionSource<FlockFileUploadOutcome>();
                cancellationToken.Register(() =>
                {
                    WasCancelled = true;
                    held.TrySetCanceled();
                });
                lock (_held)
                    _held.Add(held);
                return held.Task;
            }

            public void ReleaseEverything()
            {
                lock (_held)
                {
                    foreach (TaskCompletionSource<FlockFileUploadOutcome> held in _held)
                        held.TrySetResult(new FlockFileUploadOutcome { Result = FlockHttpResult.Success, StatusCode = 200 });
                    _held.Clear();
                }
                _keptOpen?.Dispose();
                _keptOpen = null;
            }
        }

        /// <summary>A recording written as WebM that says it is another kind, so the type on the wire can be told from the default.</summary>
        // A WebM file whose close waits until the test lets it: the recording's writing thread holds there, as a slow disk would.
        private sealed class CloseHeldRecordingFile : IProtokitePlaytestRecordingFile
        {
            private readonly ProtokitePlaytestWebmFile _inner = new ProtokitePlaytestWebmFile();
            private readonly ManualResetEventSlim _released = new ManualResetEventSlim(false);

            public void Release() => _released.Set();

            public string ContentType => _inner.ContentType;
            public string FileExtension => _inner.FileExtension;
            public int BytesAddedToEachFrame => _inner.BytesAddedToEachFrame;
            public long BytesWritten => _inner.BytesWritten;
            public int FramesWritten => _inner.FramesWritten;
            public long LastTimestampMs => _inner.LastTimestampMs;
            public bool Open(string path, ProtokitePlaytestVideoCodec codec, int width, int height, out string error) => _inner.Open(path, codec, width, height, out error);
            public bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error) => _inner.WriteFrame(frame, out error);

            public bool Close(out string error)
            {
                _released.Wait(TimeSpan.FromSeconds(30));
                return _inner.Close(out error);
            }

            public ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error)
                => _inner.FinishInterruptedRecording(unfinishedPath, finishedPath, out framesKept, out error);

            public void Dispose() => _inner.Dispose();
        }

        private sealed class AnotherKindOfRecordingFile : IProtokitePlaytestRecordingFile
        {
            private readonly ProtokitePlaytestWebmFile _inner = new ProtokitePlaytestWebmFile();

            public string ContentType => "video/mp4";
            public string FileExtension => ".mp4";
            public int BytesAddedToEachFrame => _inner.BytesAddedToEachFrame;
            public long BytesWritten => _inner.BytesWritten;
            public int FramesWritten => _inner.FramesWritten;
            public long LastTimestampMs => _inner.LastTimestampMs;
            public bool Open(string path, ProtokitePlaytestVideoCodec codec, int width, int height, out string error) => _inner.Open(path, codec, width, height, out error);
            public bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error) => _inner.WriteFrame(frame, out error);
            public bool Close(out string error) => _inner.Close(out error);

            public ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error)
                => _inner.FinishInterruptedRecording(unfinishedPath, finishedPath, out framesKept, out error);

            public void Dispose() => _inner.Dispose();
        }
    }
}
