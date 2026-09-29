using System;
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
    /// <summary>When the playtest records, and what stops it, with frames and an encoder of the test's own.</summary>
    public class ProtokitePlaytestVideoTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const double SixtyFps = 1.0 / 60.0;

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private FakeFrameSource _source;
        private FakeVp8Encoder _encoder;
        private int _sourcesMade;
        private ProtokitePlaytestPixelFormat? _formatAskedFor;

        private static string Config(bool video) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":"
            + (video ? "true" : "false") + "},\"form\":null}}";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_video_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            _source = new FakeFrameSource();
            _encoder = new FakeVp8Encoder();
            _sourcesMade = 0;
            _formatAskedFor = null;
            ProtokitePlaytest.VideoEncoderForTesting = () => _encoder;
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) =>
            {
                _sourcesMade++;
                _formatAskedFor = format;
                return _source;
            };
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
            ProtokitePlaytestRecordingsFolder.BeforeNextMakingRoomForTesting = null;
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
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

        private static FlockTestClient FlockWithConfig(bool video)
            => FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Config(video))));

        // Frames at 60 fps; paced, the encoder has each frame before the next, as in a game, so none is dropped for falling behind.
        private static void Frames(int count, FakeFrameSource pacedBy = null)
        {
            for (int i = 0; i < count; i++)
            {
                ProtokitePlaytest.UpdateVideo(SixtyFps);
                pacedBy?.WaitForTheEncoderToCatchUp();
            }
        }

        private static void WaitUntilFinished()
        {
            Assert.IsTrue(ProtokitePlaytest.VideoRecordingForTesting.WaitUntilWritten(TimeSpan.FromSeconds(20)), "Precondition: the file is written");
            ProtokitePlaytest.UpdateVideo(SixtyFps);
            Assert.IsNotNull(ProtokitePlaytest.FinishedVideo, "The finished recording is reported on the next frame");
        }

        [Test]
        public void TheConfigTurningVideoOnStartsTheRecordingBeforeAnySignIn()
        {
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the config is loaded, and nobody has signed in");
                LogAssert.Expect(LogType.Log, new Regex(@"Recording video for the playtest to .*recording-.*\.part, at 64x48 and 15 frames a second \(Vp8\)\. It stops for good after 60 minutes of play, before the file passes 1536 MB"));
                Frames(1);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo);
                Assert.AreEqual(ProtokitePlaytestPixelFormat.I420, _formatAskedFor, "The capture is asked for the encoder's pixel layout");
                Frames(119);
                Assert.Greater(_source.Asked.Count, 25, "Frames are captured each end of frame");
                StringAssert.StartsWith(Path.Combine(_folder, "Recordings"), ProtokitePlaytest.VideoRecordingForTesting.PartPath, "In the recordings folder");
            }
        }

        [Test]
        public void TheCaptureIsAskedForTheEncodersPixelLayout()
        {
            ProtokitePlaytestPixelFormat another = (ProtokitePlaytestPixelFormat)7;
            _encoder.InputPixelFormat = another;
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                Assert.AreEqual(another, _formatAskedFor, "Whatever the encoder takes, the capture is told, never assumed");
            }
        }

        [Test]
        public void NothingIsRecordedUntilThePlayerAllowsTheScreen()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.WaitingForPlayerConsent, ProtokitePlaytest.Status, "Precondition: the question waits");
                Frames(60);
                Assert.AreEqual(0, _sourcesMade, "Not while the question waits");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                Frames(60);
                Assert.AreEqual(0, _sourcesMade, "Not for play data only");

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoOnly));
                Frames(1);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "The screen is allowed now");
            }
        }

        [Test]
        public void TakingTheScreenBackStopsTheRecordingAndDeletesItOnceWritten()
        {
            _settings.Settings.AskThePlayerForPlaytestConsent = true;
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData));
                Frames(30, _source);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: recording");
                string run = ProtokitePlaytest.RecordingRunForTesting.FolderPath;

                LogAssert.Expect(LogType.Log, new Regex("deleted instead of uploaded"));
                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly));
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "Stopped the moment the screen is taken back");
                WaitUntilFinished();

                Assert.IsFalse(Directory.Exists(run), "Deleted, not kept for a later launch to send");
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.PlayerTookTheScreenBack, ProtokitePlaytest.FinishedVideo.StopReason);
                Assert.IsNull(ProtokitePlaytest.RecordingRunForTesting);

                Assert.IsTrue(ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.VideoAndPlayData));
                Frames(30);
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "One recording a launch, whatever became of it");
            }
        }

        [Test]
        public void TheConfigLeavingVideoOffRecordsNothing()
        {
            using (FlockWithConfig(false))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Frames(60);
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo);
                Assert.AreEqual(0, _sourcesMade, "No capture is even set up");
            }
        }

        [Test]
        public void NothingIsRecordedBeforeTheConfigIsLoaded()
        {
            Frames(60);
            Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "No Flock, no config, no recording");
            Assert.AreEqual(0, _sourcesMade);
        }

        [Test]
        public void OneRecordingALaunch()
        {
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(30);
                Assert.IsTrue(ProtokitePlaytest.StopVideoRecording(), "Stopped by the game");
                Assert.IsFalse(ProtokitePlaytest.StopVideoRecording(), "Only once");
                WaitUntilFinished();
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.StoppedByGame, ProtokitePlaytest.FinishedVideo.StopReason);
                Assert.IsTrue(File.Exists(ProtokitePlaytest.FinishedVideo.FilePath), "Its file is kept");

                Frames(120);
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "No second recording this launch");
                Assert.AreEqual(1, _sourcesMade);
            }
        }

        [Test]
        public void AFlockRestartDoesNotEndTheRecordingButAConfigWithVideoOffDoes()
        {
            FlockTestClient first = FlockWithConfig(true);
            ProtokitePlaytest.Refresh();
            Frames(30);
            Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: recording");

            first.Dispose();
            ProtokitePlaytest.Refresh();
            Frames(30);
            Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Flock stopping, and the config with it, is no reason to end the launch's only recording");

            using (FlockTestClient second = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Config(true)))))
            {
                ProtokitePlaytest.Refresh();
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
                Frames(30);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Nor is the config coming back with video still on");
            }

            using (FlockWithConfig(false))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "A loaded config with video off stops it");
                WaitUntilFinished();
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.PlaytestStopped, ProtokitePlaytest.FinishedVideo.StopReason);
                Assert.AreEqual(1, _sourcesMade);
            }
        }

        [Test]
        public void QuittingStopsTheRecordingAndWaitsForItsFile()
        {
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(60, pacedBy: _source);
                ProtokitePlaytest.HandleGameQuitting();
                Assert.IsNotNull(ProtokitePlaytest.FinishedVideo, "The file was waited for");
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.GameQuitting, ProtokitePlaytest.FinishedVideo.StopReason);
                Assert.AreEqual(_source.Asked.Count, ProtokitePlaytest.FinishedVideo.FramesWritten);
                Assert.IsTrue(File.Exists(ProtokitePlaytest.FinishedVideo.FilePath));
            }
        }

        [Test]
        public void QuittingWaitsNoLongerThanTheQuitAllows()
        {
            ManualResetEventSlim holdEncoding = new ManualResetEventSlim(false);
            ProtokitePlaytest.VideoEncoderForTesting = () => new HeldEncoder(_encoder, holdEncoding);
            try
            {
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(30);
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: recording, with an encoder that holds every frame");

                    LogAssert.Expect(LogType.Warning, new Regex(@"could not be finished before the game closed; what was recorded stays in .*\.part"));
                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                    ProtokitePlaytest.HandleGameQuitting();
                    Assert.Less(clock.Elapsed.TotalSeconds, 4.0, "Bounded by the quit's three seconds");
                    Assert.Greater(clock.Elapsed.TotalSeconds, 2.5, "And the file is given them");
                    Assert.IsNull(ProtokitePlaytest.FinishedVideo);
                }
            }
            finally
            {
                holdEncoding.Set();
            }
        }

        [Test]
        public void ANewLaunchStartsFreshAfterTheLastOnesRecordingIsFinished()
        {
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(30);
                ProtokitePlaytestVideoRecording last = ProtokitePlaytest.VideoRecordingForTesting;
                ProtokitePlaytest.ResetForNewLaunch();
                Assert.IsTrue(last.HasFinishedWriting, "The last launch's recording is stopped and waited for");
                Assert.IsNull(ProtokitePlaytest.VideoRecordingForTesting);

                _source = new FakeFrameSource();
                _encoder = new FakeVp8Encoder();
                ProtokitePlaytest.Refresh();
                Frames(1);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "A new launch records again");
                Assert.AreEqual(2, _sourcesMade);
            }
        }

        [Test]
        public void TimeAwayFromTheGameIsLeftOut()
        {
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                Frames(60);
                long before = _source.Asked[_source.Asked.Count - 1];
                ProtokitePlaytest.HandleGameLeftOrCameBack();
                ProtokitePlaytest.UpdateVideo(300.0);
                Frames(5);
                long after = _source.Asked[_source.Asked.Count - 1];
                Assert.Greater(after, before, "Precondition: a frame was captured after coming back");
                Assert.Less(after, before + 200, "The five minutes away are not in the video");
            }
        }

        [Test]
        public void NoEncoderMeansNoVideoAndTheRestCarriesOn()
        {
            ProtokitePlaytest.VideoEncoderForTesting = () => null;
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(60);
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo);
                Assert.AreEqual(0, _sourcesMade, "No capture is set up without an encoder");
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "The playtest still runs");
            }
        }

        [Test]
        public void NoCaptureMeansNoVideoSaidOnceAndTheEncoderIsLetGo()
        {
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) =>
            {
                _sourcesMade++;
                return null;
            };
            int said = 0;
            Application.LogCallback count = (message, stack, type) =>
            {
                if (type == LogType.Warning && message.Contains("This launch records no playtest video: no frames can be captured"))
                    said++;
            };
            using (FlockWithConfig(true))
            {
                Application.logMessageReceived += count;
                try
                {
                    ProtokitePlaytest.Refresh();
                    Frames(120);
                }
                finally
                {
                    Application.logMessageReceived -= count;
                }
                Assert.AreEqual(1, said, "Said once, as a warning");
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo);
                Assert.AreEqual(1, _sourcesMade, "Tried once a launch");
                Assert.IsTrue(_encoder.Disposed, "The encoder is let go");
            }
        }

        [Test]
        public void TheScreenIsWhatAGameRecordsWhenNoTestStandsIn()
        {
            // The production path, in this editor: the screen's source is made and asked for this encoder's layout.
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.VideoEncoderForTesting = null;
            // Outside Play Mode the screen cannot be copied, so what that logs is not this test's concern.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(1);
                    bool recording = ProtokitePlaytest.IsRecordingVideo;
                    bool screenHasSize = Screen.width > 0 && Screen.height > 0;
                    Assert.AreEqual(screenHasSize, recording, $"Records exactly when this editor's screen ({Screen.width}x{Screen.height}) has a size");
                    ProtokitePlaytest.ResetForNewLaunch();
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        // Where the recording lives on disk, and the disk budget

        private string Recordings => Path.Combine(_folder, "Recordings");

        private const long Megabyte = 1024L * 1024;

        [Test]
        public void TheRecordingIsWrittenInARunFolderHeldUntilTheLaunchEnds()
        {
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                ProtokitePlaytestRecordingRun run = ProtokitePlaytest.RecordingRunForTesting;
                Assert.IsNotNull(run, "Precondition: recording");
                string runFolder = run.FolderPath;
                string name = Path.GetFileName(runFolder);
                Assert.AreEqual(Path.GetFullPath(Path.Combine(Recordings, "Playtest")), Path.GetDirectoryName(runFolder), "A playtest recording's run");
                Assert.AreEqual(Path.Combine(runFolder, "recording-" + name + ".webm.part"), ProtokitePlaytest.VideoRecordingForTesting.PartPath);
                Assert.AreEqual(ProtokitePlaytest.VideoRecordingForTesting.MaxBytes.ToString(), File.ReadAllText(Path.Combine(runFolder, "reserved-bytes.txt")),
                    "Other launches count it at the most it may grow to");

                Frames(30);
                Assert.IsTrue(ProtokitePlaytest.StopVideoRecording());
                WaitUntilFinished();
                Assert.AreEqual(Path.Combine(runFolder, "recording-" + name + ".webm"), ProtokitePlaytest.FinishedVideo.FilePath);
                Assert.IsNull(ProtokitePlaytestRecordingRun.ClaimEnded(runFolder, ProtokitePlaytestRecordingKind.Playtest),
                    "Still held once written: its Protokite session may yet start and be saved beside it");

                ProtokitePlaytest.HandleGameQuitting();
                using (ProtokitePlaytestRecordingRun claimed = ProtokitePlaytestRecordingRun.ClaimEnded(runFolder, ProtokitePlaytestRecordingKind.Playtest))
                    Assert.IsNotNull(claimed, "Let go when the launch ends, so a game started after it (or after Play Mode) can keep or finish it");
            }
        }

        [Test]
        public void ARunWhoseFileIsStillBeingWrittenAtQuitIsLetGoOnlyByTheNextLaunch()
        {
            ManualResetEventSlim holdEncoding = new ManualResetEventSlim(false);
            ProtokitePlaytest.VideoEncoderForTesting = () => new HeldEncoder(_encoder, holdEncoding);
            try
            {
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(30);
                    string runFolder = ProtokitePlaytest.RecordingRunForTesting.FolderPath;
                    LogAssert.Expect(LogType.Warning, new Regex(@"could not be finished before the game closed"));
                    ProtokitePlaytest.HandleGameQuitting();
                    Assert.IsNull(ProtokitePlaytestRecordingRun.ClaimEnded(runFolder, ProtokitePlaytestRecordingKind.Playtest),
                        "Its file is still being written, so no other launch may finish it yet");

                    holdEncoding.Set();
                    ProtokitePlaytest.ResetForNewLaunch();
                    using (ProtokitePlaytestRecordingRun claimed = ProtokitePlaytestRecordingRun.ClaimEnded(runFolder, ProtokitePlaytestRecordingKind.Playtest))
                        Assert.IsNotNull(claimed, "The next launch waits for it, then lets go");
                }
            }
            finally
            {
                holdEncoding.Set();
            }
        }

        [Test]
        public void ARecordingMakesRoomOnlyOnceEarlierLaunchesRecordingsAreGoneThrough()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 4;
            _settings.Settings.MaxRecordingMinutes = 0.1f;
            // Waiting to upload, reserved at 3 MB and holding 1 KB: counted at 3 MB while the pass holds it, at 1 KB after.
            string upload = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000001", 3 * Megabyte,
                "pk-1", finishedVideo: new byte[1024]);
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run => letItFinish.Wait(TimeSpan.FromSeconds(5));
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(30);
                    Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "No room is made while the pass still works on what earlier launches left");

                    letItFinish.Set();
                    Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)));
                    Frames(1);
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Once it is done, recording starts");
                    long uploadBytes = ProtokitePlaytestRecordingRun.BytesOnDisk(upload);
                    Assert.AreEqual(4 * Megabyte - uploadBytes, ProtokitePlaytest.VideoRecordingForTesting.MaxBytes, "The ended upload counts at what it holds");
                }
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [Test]
        public void ARecordingWaitsForEarlierLaunchesRecordingsNoLongerThanItsBound()
        {
            ProtokitePlaytest.LongestWaitForEarlierRecordingsForTesting = TimeSpan.FromMilliseconds(500);
            ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000001", 1024, "pk-1", finishedVideo: new byte[1024]);
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run => letItFinish.Wait(TimeSpan.FromSeconds(5));
            try
            {
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(1);
                    Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "Precondition: waiting for the pass");
                    Thread.Sleep(700);
                    Frames(1);
                    Assert.IsFalse(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.Zero), "Precondition: the pass is still held");
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "A pass that takes too long does not cost the launch its recording");
                }
            }
            finally
            {
                letItFinish.Set();
            }
        }

        [Test]
        public void ARecordingThatCannotStartLeavesNoRunBehind()
        {
            _source = new FakeFrameSource(1, 1);
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                LogAssert.Expect(LogType.Warning, new Regex(@"This launch records no playtest video: a 1x1 video cannot be recorded"));
                Frames(1);
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo);
                Assert.IsEmpty(ProtokitePlaytestPlantedRuns.Runs(Recordings, ProtokitePlaytestRecordingKind.Playtest), "Its run is deleted with it");
            }
        }

        [Test]
        public void TheRecordingMayTakeOnlyTheRoomTheBudgetHasLeft()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 3;
            _settings.Settings.MaxRecordingMinutes = 0.1f;
            string otherGame = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000001", Megabyte);
            using (ProtokitePlaytestPlantedRuns.HoldLock(otherGame))
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                LogAssert.Expect(LogType.Log, new Regex(@"before the file passes 2 MB \(Max Recording Size Mb is 1536 MB, but Recordings Disk Budget Mb has only this much left\)"));
                Frames(1);
                Assert.AreEqual(2 * Megabyte, ProtokitePlaytest.VideoRecordingForTesting.MaxBytes, "The 3 MB budget less the 1 MB another game still running reserved");
                Assert.AreEqual((2 * Megabyte).ToString(), File.ReadAllText(Path.Combine(ProtokitePlaytest.RecordingRunForTesting.FolderPath, "reserved-bytes.txt")));
                Assert.IsTrue(Directory.Exists(otherGame));
            }
        }

        [Test]
        public void NoRoomLeftInTheBudgetRecordsNothingAndNamesTheSettingToRaise()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 2;
            _settings.Settings.MaxRecordingMinutes = 0.1f;
            string otherGame = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000001", 3 * Megabyte / 2);
            Regex noRoom = new Regex(@"This launch records no playtest video: the recordings in .* take 1\.5 MB of Recordings Disk Budget Mb \(2 MB\).*" +
                                     @"Raise Recordings Disk Budget Mb in Protokite > Playtest > Settings");
            int said = 0;
            Application.LogCallback count = (message, stack, type) =>
            {
                if (type == LogType.Warning && noRoom.IsMatch(message))
                    said++;
            };
            using (ProtokitePlaytestPlantedRuns.HoldLock(otherGame))
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Application.logMessageReceived += count;
                try
                {
                    Frames(60);
                }
                finally
                {
                    Application.logMessageReceived -= count;
                }
                Assert.AreEqual(1, said, "Said once, as a warning naming the setting");
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "Half a megabyte is too little to start with");
                Assert.AreEqual(1, ProtokitePlaytestPlantedRuns.Runs(Recordings, ProtokitePlaytestRecordingKind.Playtest).Length, "Its own run is deleted again");
                Assert.IsTrue(_encoder.Disposed, "The encoder is let go");
                Assert.IsTrue(_source.Disposed, "And the capture");
            }
        }

        [Test]
        public void AShortRecordingNeverDeletesARecordingWaitingToUpload()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 10;
            _settings.Settings.MaxRecordingMinutes = 0.1f;
            string upload = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000001",
                sessionId: "pk-1", finishedVideo: new byte[7 * Megabyte]);
            using (FlockWithConfig(true))
            {
                ProtokitePlaytest.Refresh();
                Frames(1);
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo);
                Assert.IsTrue(File.Exists(ProtokitePlaytestPlantedRuns.VideoPath(upload, ProtokitePlaytestRecordingKind.Playtest)),
                    "Six seconds at its bitrate fit beside it; the 1.5 GB size limit is no reason to delete it");
            }
        }

        [Test]
        public void TwoGamesStartingToRecordTogetherNeverTakeEachOthersRoom()
        {
            _settings.Settings.RecordingsDiskBudgetMb = 4;
            _settings.Settings.MaxRecordingMinutes = 0.1f;
            ProtokitePlaytestVideoSettings other = ProtokitePlaytestVideoSettings.From(_settings.Settings);
            long wanted = other.BytesToMakeRoomFor(ProtokitePlaytestWebmFile.FrameHeaderBytes);
            long otherMaxBytes = 0;
            ProtokitePlaytestRecordingRun otherRun = null;
            // Another game starts just as this one makes room, and does as this one does: its run and reservation, then room.
            ProtokitePlaytestRecordingsFolder.BeforeNextMakingRoomForTesting = () =>
            {
                otherRun = ProtokitePlaytestRecordingRun.Start(Recordings, ProtokitePlaytestRecordingKind.Playtest, wanted, out _);
                ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(Recordings, otherRun, other.DiskBudgetBytes, wanted, other.MaxBytes);
                otherMaxBytes = Math.Min(other.MaxBytes, room.BytesLeft);
                otherRun.SaveReservedBytes(otherMaxBytes, out _);
            };
            try
            {
                using (FlockWithConfig(true))
                {
                    ProtokitePlaytest.Refresh();
                    Frames(1);
                    Assert.IsNotNull(otherRun, "Precondition: the other game started while this one made room");
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "This game records");
                    long mine = ProtokitePlaytest.VideoRecordingForTesting.MaxBytes;
                    Assert.GreaterOrEqual(otherMaxBytes, wanted, "The other game has the room it wanted");
                    Assert.GreaterOrEqual(mine, wanted, "And so does this one");
                    Assert.LessOrEqual(mine + otherMaxBytes, 4 * Megabyte, "Together they fit the budget");
                }
            }
            finally
            {
                otherRun?.Dispose();
            }
        }

        [Test]
        public void WhatEarlierLaunchesLeftIsGoneThroughOffTheMainThreadWithoutHoldingUpTheLaunch()
        {
            string noSession = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.Playtest, "20260101-000000-00000001",
                finishedVideo: new byte[100]);
            ManualResetEventSlim letItFinish = new ManualResetEventSlim(false);
            int finishingThread = -1;
            ProtokitePlaytest.BeforeFinishingEachEarlierRecordingForTesting = run =>
            {
                finishingThread = Thread.CurrentThread.ManagedThreadId;
                letItFinish.Wait(TimeSpan.FromSeconds(5));
            };
            try
            {
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                ProtokitePlaytest.StartFinishingEarlierRecordings();
                Assert.Less(clock.Elapsed.TotalSeconds, 1.0, "Started, not waited for");
                Assert.IsFalse(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromMilliseconds(200)), "Still held by the test");
                letItFinish.Set();
                Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)));
                Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, finishingThread, "On a thread of its own");
                Assert.IsFalse(Directory.Exists(noSession), "And it did its work");
            }
            finally
            {
                letItFinish.Set();
            }
        }

        /// <summary>An encoder whose frames wait for the test before they are encoded.</summary>
        private sealed class HeldEncoder : IProtokitePlaytestVideoEncoder
        {
            private readonly IProtokitePlaytestVideoEncoder _inner;
            private readonly ManualResetEventSlim _hold;

            public HeldEncoder(IProtokitePlaytestVideoEncoder inner, ManualResetEventSlim hold)
            {
                _inner = inner;
                _hold = hold;
            }

            public ProtokitePlaytestPixelFormat InputPixelFormat => _inner.InputPixelFormat;
            public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error) => _inner.Configure(settings, out error);

            public bool Encode(byte[] i420, long timestampMs, long durationMs, bool forceKeyframe, System.Collections.Generic.List<ProtokitePlaytestEncodedFrame> output, out string error)
            {
                _hold.Wait(TimeSpan.FromSeconds(20));
                return _inner.Encode(i420, timestampMs, durationMs, forceKeyframe, output, out error);
            }

            public bool Finish(System.Collections.Generic.List<ProtokitePlaytestEncodedFrame> output, out string error) => _inner.Finish(output, out error);
            public void Dispose() => _inner.Dispose();
        }
    }
}
