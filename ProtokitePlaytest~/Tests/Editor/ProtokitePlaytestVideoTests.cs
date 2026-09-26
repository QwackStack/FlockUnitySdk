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

        private static void Frames(int count)
        {
            for (int i = 0; i < count; i++)
                ProtokitePlaytest.UpdateVideo(SixtyFps);
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
                Frames(60);
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
