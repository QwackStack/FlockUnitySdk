using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>Test videos asked for from the editor's window: kept on the machine with no session, never uploaded, and never in the playtest's way.</summary>
    public class ProtokitePlaytestTestVideoTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string StartRoute = "/game/sdk/playtest-session";
        private const string EndRoute = "/end";
        private const string LinkRoute = "/recording-upload";
        private const string FlockSessionRoute = "/analytics/sessions";
        private const double SixtyFps = 1.0 / 60.0;
        private const long Megabyte = 1024L * 1024;

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private FakeFrameSource _source;
        private FakeH264Encoder _encoder;
        private int _sourcesMade;

        private string Recordings => Path.Combine(_folder, "Recordings");

        private static string Config(bool video) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":"
            + (video ? "true" : "false") + "},\"form\":null}}";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            // Playtesting off: a test video needs no playtest.
            _settings = new ProtokitePlaytestSettingsForTests(playtestingEnabled: false);
            _folder = Path.Combine(Path.GetTempPath(), "protokite_test_video_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Recordings;
            _source = null;
            _encoder = null;
            _sourcesMade = 0;
            // A capture and an encoder of their own for each recording, as a game makes them.
            ProtokitePlaytest.VideoEncoderForTesting = () => _encoder = new FakeH264Encoder();
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) =>
            {
                _sourcesMade++;
                return _source = new FakeFrameSource();
            };
            _settings.Settings.VideoBitrateKbps = 1500;
            _settings.Settings.MaxRecordingSizeMb = 1536;
            _settings.Settings.MaxRecordingMinutes = 60f;
            _settings.Settings.RecordingsDiskBudgetMb = 4096;
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)), "The finishing pass ended before its folder is deleted");
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = null;
            ProtokitePlaytest.LongestWaitForEarlierRecordingsForTesting = null;
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            _settings.Dispose();
            try
            {
                if (Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        // Frames at 60 fps, each encoded before the next, as in a game.
        private void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                ProtokitePlaytest.UpdateVideo(SixtyFps);
                _source?.WaitForTheEncoderToCatchUp();
            }
        }

        private static void RecordTestVideo(double seconds)
        {
            Assert.IsTrue(ProtokitePlaytest.RecordTestVideo(seconds, out string whyNot), "Precondition: the test video is asked for: " + whyNot);
        }

        private static string RunFolderOf(string path) => Path.GetDirectoryName(path);

        // Until the test video's file is written and reported, a frame at a time.
        private void FramesUntilFinished()
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (ProtokitePlaytest.TestVideoState != ProtokitePlaytestTestVideoState.Finished && DateTime.UtcNow < until)
            {
                ProtokitePlaytest.UpdateVideo(SixtyFps);
                Thread.Sleep(1);
            }
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Finished, ProtokitePlaytest.TestVideoState, "Precondition: the test video's file was written");
        }

        private static FlockTestClient FlockWithConfig(bool video)
            => FlockTestClient.Create(new FlockFakeTransport()
                .On(LinkRoute, FlockFakeTransport.Ok("{\"result\":{\"upload_url\":\"http://storage.test/r\"},\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null}}"))
                .On(EndRoute, FlockFakeTransport.Status(204, ""))
                .On(StartRoute, FlockFakeTransport.Ok("{\"result\":{\"session_id\":\"pk-1\"}}"))
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(video)))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"01K5SRVSESSION000000000001\"}")),
                config => config.RetryPolicy = new RetryPolicy { MaxRetries = 0, InitialDelay = TimeSpan.Zero });

        [Test]
        public void ATestVideoIsRecordedWithNoPlaytestIntoTheTestVideosFolder()
        {
            Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.Status, "Precondition: playtesting is off and Flock is not running");
            RecordTestVideo(2);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.WaitingToStart, ProtokitePlaytest.TestVideoState, "It starts at the end of a frame");
            LogAssert.Expect(LogType.Log, new Regex(@"Recording a test video to .*TestVideos.*test-recording-.*\.mp4\.part, at 64x48 and 15 frames a second, as H\.264\. It stops after 2 seconds of play"));
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
            string partPath = ProtokitePlaytest.TestVideoPartPath;
            StringAssert.StartsWith(Path.Combine(Recordings, "TestVideos"), partPath, "In the TestVideos folder, which is never uploaded");
            Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "Not the playtest's recording");

            Frames(150);
            FramesUntilFinished();
            ProtokitePlaytestVideoRecordingSummary summary = ProtokitePlaytest.FinishedTestVideo;
            Assert.IsNull(summary.Error);
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.ReachedLengthLimit, summary.StopReason, "It stops at the length asked for");
            Assert.AreEqual(2.0, summary.VideoSeconds, 0.2);
            Assert.IsTrue(File.Exists(summary.FilePath));
            StringAssert.StartsWith("test-recording-", Path.GetFileName(summary.FilePath));
            Assert.AreEqual(Path.Combine(Recordings, "TestVideos"), Path.GetDirectoryName(RunFolderOf(summary.FilePath)));
            Assert.IsFalse(File.Exists(Path.Combine(RunFolderOf(summary.FilePath), "session.json")), "A test video has no session");
            using (ProtokitePlaytestRecordingRun run = ProtokitePlaytestRecordingRun.ClaimEnded(RunFolderOf(summary.FilePath), ProtokitePlaytestRecordingKind.TestVideo))
                Assert.IsNotNull(run, "Its run is let go once written: nothing more is saved into it");
        }

        [Test]
        public void ATestVideoReservesWhatItsOwnLengthNeedsNeverTheRoomLeft()
        {
            RecordTestVideo(20);
            Frames(1);
            ProtokitePlaytestVideoSettings expected = ProtokitePlaytestVideoSettings.From(_settings.Settings);
            expected.MaxSeconds = 20;
            long ownLength = expected.BytesToMakeRoomFor(ProtokitePlaytestMp4File.FrameHeaderBytes);
            Assert.Greater(ownLength, ProtokitePlaytestRecordingsFolder.SmallestRoomForARecording, "Precondition: twenty seconds need more than the least a recording starts with");
            string run = RunFolderOf(ProtokitePlaytest.TestVideoPartPath);
            Assert.AreEqual(ownLength.ToString(), File.ReadAllText(Path.Combine(run, "reserved-bytes.txt")),
                "Twenty seconds at the bitrate: not the 4 GB left in the budget, nor the hour the playtest may record");
            Assert.AreEqual(ownLength, ProtokitePlaytest.TestVideoForTesting.MaxBytes, "And it never grows past what others count it at");
        }

        [Test]
        public void AShortTestVideoReservesAtLeastWhatARecordingStartsWith()
        {
            RecordTestVideo(1);
            Frames(1);
            string run = RunFolderOf(ProtokitePlaytest.TestVideoPartPath);
            Assert.AreEqual(ProtokitePlaytestRecordingsFolder.SmallestRoomForARecording.ToString(), File.ReadAllText(Path.Combine(run, "reserved-bytes.txt")),
                "A second at the bitrate is less than the first frame alone can take");
        }

        [Test]
        public void RoomForATestVideoIsMadeFromOlderTestVideosNeverFromAWaitingUpload()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 3;
            string olderTestVideo = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.TestVideo, "20260101-000000-00000001",
                finishedVideo: new byte[Megabyte]);
            string upload = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000002",
                sessionId: "pk-1", finishedVideo: new byte[3 * Megabyte / 2]);
            RecordTestVideo(1);
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, ProtokitePlaytest.TestVideoProblem);
            Assert.IsFalse(Directory.Exists(olderTestVideo), "The older test video made room");
            Assert.IsTrue(File.Exists(ProtokitePlaytestPlantedRuns.VideoPath(upload, ProtokitePlaytestRecordingKind.Playtest)), "The waiting upload is kept");
        }

        [Test]
        public void ATestVideoThatWouldNeedAWaitingUploadsRoomIsNotRecorded()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 2;
            string upload = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000002",
                sessionId: "pk-1", finishedVideo: new byte[3 * Megabyte / 2]);
            RecordTestVideo(1);
            LogAssert.Expect(LogType.Warning, new Regex(@"No test video is recorded: the recordings in .* take 1\.5 MB of Recordings Disk Budget Mb \(2 MB\), and a test video makes room by deleting older test videos only, never a recording waiting to upload\. Raise Recordings Disk Budget Mb"));
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.NotRecorded, ProtokitePlaytest.TestVideoState);
            StringAssert.Contains("never a recording waiting to upload", ProtokitePlaytest.TestVideoProblem);
            Assert.IsTrue(File.Exists(ProtokitePlaytestPlantedRuns.VideoPath(upload, ProtokitePlaytestRecordingKind.Playtest)), "The waiting upload is kept");
            Assert.IsEmpty(ProtokitePlaytestPlantedRuns.Runs(Recordings, ProtokitePlaytestRecordingKind.TestVideo), "Its own run is deleted again");
            Assert.IsTrue(_encoder.Disposed, "The encoder is let go");
            Assert.IsTrue(_source.Disposed, "And the capture");
        }

        [UnityTest]
        public IEnumerator ATestVideoIsNeverUploadedAndNoSessionIsSavedBesideIt()
        {
            _settings.Settings.PlaytestingEnabled = true;
            using (FlockTestClient flock = FlockWithConfig(false))
            {
                ProtokitePlaytest.Refresh();
                RecordTestVideo(2);
                Frames(1);
                Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
                // The playtest's session starts while the test video records, and is still going when the video is written.
                flock.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                flock.Run(() => flock.Client.Analytics.StartSessionAsync());
                ProtokitePlaytest.Refresh();
                DateTime until = DateTime.UtcNow.AddSeconds(5);
                while (ProtokitePlaytest.PlaytestSessionId == null && DateTime.UtcNow < until)
                {
                    ProtokitePlaytest.Refresh();
                    yield return null;
                }
                Assert.AreEqual("pk-1", ProtokitePlaytest.PlaytestSessionId, "Precondition: the session started while the test video recorded");
                Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "Precondition: still recording");

                FramesUntilFinished();
                Assert.AreEqual(ProtokitePlaytestSessionState.Started, ProtokitePlaytest.SessionState, "Precondition: written while the session runs");
                string video = ProtokitePlaytest.FinishedTestVideo.FilePath;
                // Real time, frame by frame, as a game would run on: nothing is sent for it.
                until = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < until)
                {
                    ProtokitePlaytest.Refresh();
                    Frames(1);
                    yield return null;
                }
                Assert.AreEqual(0, flock.Transport.CountTo(LinkRoute), "No upload link is asked for a test video");
                Assert.IsTrue(File.Exists(video), "It is kept");
                Assert.IsFalse(File.Exists(Path.Combine(RunFolderOf(video), "session.json")), "And no session is saved beside it, so no later launch uploads it");
            }
        }

        [Test]
        public void ThePlaytestsOwnRecordingTakesOverFromATestVideo()
        {
            _settings.Settings.PlaytestingEnabled = true;
            RecordTestVideo(60);
            Frames(10);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "Precondition: recording before the playtest's config arrives");

            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the config turns video on");
                Frames(1);
                Assert.AreNotEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "The test video stops on the frame the playtest's could start");
                FramesUntilFinished();
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.PlaytestRecordingStarts, ProtokitePlaytest.FinishedTestVideo.StopReason);
                Assert.IsTrue(File.Exists(ProtokitePlaytest.FinishedTestVideo.FilePath), "What it recorded is kept");
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "The playtest records in the same launch, on the frame the test video was written");
                StringAssert.StartsWith(Path.Combine(Recordings, "Playtest"), ProtokitePlaytest.VideoRecordingForTesting.PartPath);
                Assert.AreEqual(2, _sourcesMade);
            }
        }

        [Test]
        public void ATestVideoIsRefusedWhileThePlaytestRecords()
        {
            _settings.Settings.PlaytestingEnabled = true;
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: the playtest records");
                Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(10, out string whyNot));
                StringAssert.Contains("this launch records video for its playtest", whyNot);
                Frames(5);
                Assert.AreEqual(ProtokitePlaytestTestVideoState.None, ProtokitePlaytest.TestVideoState);
                Assert.AreEqual(1, _sourcesMade, "Nothing else captures the screen");
            }
        }

        [Test]
        public void ATestVideoIsRefusedWhileThePlaytestsRecordingIsAboutToStart()
        {
            _settings.Settings.PlaytestingEnabled = true;
            ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.TestVideo, "20260101-000000-00000001", finishedVideo: new byte[10]);
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run => letItFinish.Wait(TimeSpan.FromSeconds(10));
            ProtokitePlaytest.LongestWaitForEarlierRecordingsForTesting = TimeSpan.FromSeconds(30);
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(1);
                    Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "Precondition: the playtest's recording waits for the earlier recordings");
                    Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(10, out string whyNot), "Its screen is spoken for");
                    StringAssert.Contains("this launch records video for its playtest", whyNot);
                }
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [Test]
        public void ATestVideoAskedForJustBeforeThePlaytestsRecordingGivesWay()
        {
            _settings.Settings.PlaytestingEnabled = true;
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.TestVideo, "20260101-000000-00000001", finishedVideo: new byte[10]);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run => letItFinish.Wait(TimeSpan.FromSeconds(10));
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                RecordTestVideo(10);
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    letItFinish.Set();
                    Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)));
                    LogAssert.Expect(LogType.Warning, new Regex(@"No test video is recorded: this launch's playtest records video"));
                    Frames(1);
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "The playtest's recording starts");
                    Assert.AreEqual(ProtokitePlaytestTestVideoState.NotRecorded, ProtokitePlaytest.TestVideoState, "And the test video waiting to start is dropped");
                    Assert.AreEqual(1, _sourcesMade);
                }
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [Test]
        public void OneTestVideoAtATime()
        {
            RecordTestVideo(1);
            Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(1, out string waiting), "Refused while one waits to start");
            StringAssert.Contains("already being recorded", waiting);
            Frames(1);
            Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(1, out string recording), "And while it records");
            StringAssert.Contains("already being recorded", recording);

            FramesUntilFinished();
            string first = ProtokitePlaytest.FinishedTestVideo.FilePath;
            RecordTestVideo(1);
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "Another may follow once the first is written");
            Assert.IsTrue(File.Exists(first), "The first is kept");
        }

        [TestCase(0.0)]
        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void ALengthThatIsNoLengthIsRefused(double seconds)
        {
            Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(seconds, out string whyNot));
            StringAssert.Contains("needs a length above 0 seconds", whyNot);
            Frames(3);
            Assert.AreEqual(0, _sourcesMade);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.None, ProtokitePlaytest.TestVideoState);
        }

        [Test]
        public void OutsidePlayModeATestVideoIsRefused()
        {
            // The screen is read only in Play Mode, and nothing moves a test video along outside it.
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            Assert.IsFalse(Application.isPlaying, "Precondition: an edit-mode test");
            Assert.IsFalse(ProtokitePlaytest.RecordTestVideo(5, out string whyNot));
            StringAssert.Contains("Play Mode", whyNot);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.None, ProtokitePlaytest.TestVideoState, "Nothing waits for a Play that would clear it");
        }

        [Test]
        public void ALengthPastTheLengthLimitIsCutToIt()
        {
            _settings.Settings.MaxRecordingMinutes = 0.1f;
            RecordTestVideo(600);
            LogAssert.Expect(LogType.Log, new Regex(@"It stops after 6 seconds of play \(600 were asked for, and Max Recording Minutes allows no more\)"));
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
        }

        [Test]
        public void ATestVideoIsRecordedWhateverThePlayerAnsweredThePlaytest()
        {
            _settings.Settings.PlaytestingEnabled = true;
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.Nothing), "Precondition: the player asked the playtest to collect nothing");
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.PlayerRefusedPlaytest, ProtokitePlaytest.Status);
                RecordTestVideo(5);
                Frames(1);
                Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState,
                    "The developer's own test video is never sent, so the playtest's question does not cover it");
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "While the playtest records nothing");
            }
        }

        [Test]
        public void TimeAwayFromTheGameIsLeftOutOfATestVideo()
        {
            // Longer than the time away, so five minutes counted as recorded would not simply end it at its length.
            RecordTestVideo(600);
            Frames(61);
            long before = _source.Asked[_source.Asked.Count - 1];
            ProtokitePlaytest.HandleGameLeftOrCameBack();
            ProtokitePlaytest.UpdateVideo(300.0);
            Frames(5);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "Still recording after coming back");
            long after = _source.Asked[_source.Asked.Count - 1];
            Assert.Greater(after, before, "Precondition: a frame was captured after coming back");
            Assert.Less(after, before + 200, "The five minutes away are not in the video");
        }

        [Test]
        public void ATestVideoStartsOnlyOnceEarlierLaunchesRecordingsAreGoneThrough()
        {
            ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.TestVideo, "20260101-000000-00000001", finishedVideo: new byte[10]);
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run => letItFinish.Wait(TimeSpan.FromSeconds(10));
            ProtokitePlaytest.LongestWaitForEarlierRecordingsForTesting = TimeSpan.FromSeconds(30);
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                RecordTestVideo(5);
                Frames(10);
                Assert.AreEqual(ProtokitePlaytestTestVideoState.WaitingToStart, ProtokitePlaytest.TestVideoState, "Making room counts runs the pass has not reached at their whole reservation");
                Assert.AreEqual(0, _sourcesMade);
                letItFinish.Set();
                Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)));
                Frames(1);
                Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [Test]
        public void QuittingStopsATestVideoAndFinishesItsFile()
        {
            RecordTestVideo(60);
            Frames(30);
            ProtokitePlaytest.HandleGameQuitting();
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Finished, ProtokitePlaytest.TestVideoState, "Finished before quitting returns");
            ProtokitePlaytestVideoRecordingSummary summary = ProtokitePlaytest.FinishedTestVideo;
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.GameQuitting, summary.StopReason);
            Assert.IsTrue(File.Exists(summary.FilePath));
            using (ProtokitePlaytestRecordingRun run = ProtokitePlaytestRecordingRun.ClaimEnded(RunFolderOf(summary.FilePath), ProtokitePlaytestRecordingKind.TestVideo))
                Assert.IsNotNull(run, "And its run is let go: the Editor stays open after Play Mode ends");
        }

        [Test]
        public void ATestVideoStillWaitingWhenTheGameClosesSaysItWasNotRecorded()
        {
            ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.TestVideo, "20260101-000000-00000001", finishedVideo: new byte[10]);
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run => letItFinish.Wait(TimeSpan.FromSeconds(10));
            ProtokitePlaytest.LongestWaitForEarlierRecordingsForTesting = TimeSpan.FromSeconds(30);
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                RecordTestVideo(5);
                Frames(2);
                Assert.AreEqual(ProtokitePlaytestTestVideoState.WaitingToStart, ProtokitePlaytest.TestVideoState, "Precondition: still waiting");
                LogAssert.Expect(LogType.Warning, new Regex(@"No test video is recorded: the game closed before it started\."));
                ProtokitePlaytest.HandleGameQuitting();
                Assert.AreEqual(ProtokitePlaytestTestVideoState.NotRecorded, ProtokitePlaytest.TestVideoState, "Not dropped without a word");
                Assert.AreEqual(0, _sourcesMade);
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [Test]
        public void ANewLaunchStopsAndForgetsATestVideo()
        {
            RecordTestVideo(60);
            Frames(30);
            ProtokitePlaytestVideoRecording last = ProtokitePlaytest.TestVideoForTesting;
            string run = RunFolderOf(ProtokitePlaytest.TestVideoPartPath);
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsTrue(last.HasFinishedWriting, "The last launch's test video is stopped and waited for");
            Assert.AreEqual(ProtokitePlaytestTestVideoState.None, ProtokitePlaytest.TestVideoState);
            using (ProtokitePlaytestRecordingRun claimed = ProtokitePlaytestRecordingRun.ClaimEnded(run, ProtokitePlaytestRecordingKind.TestVideo))
                Assert.IsNotNull(claimed, "Its run is let go");
        }

        [Test]
        public void ATestVideoThatCannotStartSaysWhyAndLeavesNothing()
        {
            ProtokitePlaytest.VideoEncoderForTesting = () => null;
            RecordTestVideo(5);
            LogAssert.Expect(LogType.Warning, new Regex(@"No test video is recorded: this build has no video encoder\."));
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.NotRecorded, ProtokitePlaytest.TestVideoState);
            Assert.AreEqual("this build has no video encoder.", ProtokitePlaytest.TestVideoProblem);
            Assert.IsEmpty(ProtokitePlaytestPlantedRuns.Runs(Recordings, ProtokitePlaytestRecordingKind.TestVideo));
        }
    }
}
