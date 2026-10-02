using System;
using System.Collections;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    // The path a game takes: the driver Unity starts, running once a frame, with nothing calling the playtest by hand.
    public class ProtokitePlaytestDriverTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string Answer =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"driven\",\"flock_game_version_id\":\"test-gvid\",\"features\":{},\"form\":null}}";

        private ProtokitePlaytestSettings _settings;
        private bool _wasEnabled;
        private string _oldUrl;
        private string _recordings;
        private ProtokitePlaytestConsentForPlayModeTests _consent;

        // What Unity's own start-up left, read once before any test tidies it away.
        private static bool _playtestingWasOnAtStartUp;
        private static int _driversStartedByUnity = -1;

        // As Play Mode starts, after the driver (BeforeSceneLoad) and before any test: another fixture may tidy the driver away first.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void ReadWhatStartUpLeft()
        {
            if (_driversStartedByUnity >= 0)
                return;
            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            _playtestingWasOnAtStartUp = settings != null && settings.PlaytestingEnabled;
            _driversStartedByUnity = Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>().Length;
        }

        [Test]
        public void UnityStartsOneDriverWhateverThePlaytestingSetting()
        {
            // With playtesting off it still uploads what earlier launches kept, so it runs either way.
            Assert.AreEqual(1, _driversStartedByUnity,
                $"Playtesting was {(_playtestingWasOnAtStartUp ? "on" : "off")} in the project's settings when Play Mode started");
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // Entering Play Mode ran the project's own start-up: its Flock client and a driver, whatever the playtesting setting.
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            yield return null;

            _settings = ProtokitePlaytestSettings.Load();
            if (_settings == null)
                Assert.Ignore("This project has no playtest settings; create them with Protokite > Playtest > Settings.");
            _wasEnabled = _settings.PlaytestingEnabled;
            _oldUrl = _settings.ProtokiteApiUrl;
            _settings.PlaytestingEnabled = true;
            _settings.ProtokiteApiUrl = "http://protokite.test";
            _consent = new ProtokitePlaytestConsentForPlayModeTests(_settings);
            ProtokitePlaytest.ResetForNewLaunch();
            // A session started here must never read or write the game's own device id, and the driver goes through recordings
            // earlier launches left, which must never be the game's own.
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "protokite_driver_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = System.IO.Path.Combine(folder, "device_id.txt");
            _recordings = System.IO.Path.Combine(folder, "Recordings");
            ProtokitePlaytest.RecordingsFolderForTesting = _recordings;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)), "The finishing pass ended before its folder is deleted");
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            if (ProtokitePlaytest.DeviceIdFilePathForTesting != null)
            {
                string folder = System.IO.Path.GetDirectoryName(ProtokitePlaytest.DeviceIdFilePathForTesting);
                ProtokitePlaytest.DeviceIdFilePathForTesting = null;
                if (System.IO.Directory.Exists(folder))
                    System.IO.Directory.Delete(folder, true);
            }
            if (_settings != null)
            {
                _settings.PlaytestingEnabled = _wasEnabled;
                _settings.ProtokiteApiUrl = _oldUrl;
            }
            _consent?.Dispose();
            _consent = null;
        }

        [Test]
        public void TheDriverEndsTheSessionWhenTheGameQuits()
        {
            ProtokitePlaytestDriver.StartWithTheGame();
            ProtokitePlaytestDriver.StartWithTheGame();

            // Unity keeps the quitting handlers in a private field; read it to see the hook is there, once.
            System.Reflection.FieldInfo field = typeof(Application).GetField("quitting",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Application.quitting's handlers were found where this Unity keeps them");
            Delegate handlers = (Delegate)field.GetValue(null);
            int hooked = 0;
            foreach (Delegate handler in handlers?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                if (handler.Method.Name == nameof(ProtokitePlaytest.HandleGameQuitting) && handler.Method.DeclaringType == typeof(ProtokitePlaytest))
                    hooked++;
            }
            Assert.AreEqual(1, hooked, "The session end runs when the game quits, once however often the driver starts");
        }

        [UnityTest]
        public IEnumerator TheDriverFetchesOncePerFlockClientAndFollowsARestart()
        {
            ProtokitePlaytestDriver.StartWithTheGame();
            ProtokitePlaytestDriver.StartWithTheGame();
            Assert.AreEqual(1, Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>().Length, "One driver, however often it is started");

            FlockFakeTransport transport = new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Answer));
            FlockTestClient flock = FlockTestClient.Create(transport);
            for (int frame = 0; frame < 5; frame++)
                yield return null;

            Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
            Assert.AreEqual("driven", ProtokitePlaytest.Config.TestId);
            Assert.AreEqual(1, transport.CountTo(ConfigRoute), "Once, not once a frame");

            flock.Dispose();
            yield return null;
            Assert.AreEqual(ProtokitePlaytestStatus.WaitingForFlock, ProtokitePlaytest.Status);

            using (FlockTestClient.Create(transport))
            {
                for (int frame = 0; frame < 5; frame++)
                    yield return null;
                Assert.AreEqual(2, transport.CountTo(ConfigRoute), "A new Flock client is noticed without any event, and asked for again");
                Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status);
            }
        }

        private const string VideoAnswer =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"driven\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":true},\"form\":null}}";

        [UnityTest]
        public IEnumerator TheDriverAloneRecordsWhenTheConfigTurnsVideoOn()
        {
            if (Application.isBatchMode)
                Assert.Ignore("A batchmode editor never reaches the end of a frame, where the driver records; run the PlayMode tests in a windowed editor.");
            CountingFrameSource source = new CountingFrameSource();
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => source;
            try
            {
                ProtokitePlaytestDriver.StartWithTheGame();
                using (FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(VideoAnswer))))
                {
                    // The graphics card's encoder takes 0.1 to 2.2 s to start (measured); frames are captured once it has.
                    yield return FlockTestWait.Until(() => ProtokitePlaytest.VideoRecordingForTesting != null && ProtokitePlaytest.VideoRecordingForTesting.EncoderHasStarted,
                        "the encoder started, from the driver alone");
                    yield return new WaitForSecondsRealtime(1.5f);
                    Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Recording, with nothing but the driver calling the playtest");
                    Assert.GreaterOrEqual(source.Captures, 15, "A second and a half of play at 15 frames a second");

                    ProtokitePlaytest.HandleGameQuitting();
                    Assert.IsNotNull(ProtokitePlaytest.FinishedVideo, "Quitting finishes the file");
                    Assert.Greater(ProtokitePlaytest.FinishedVideo.FramesWritten, 0, ProtokitePlaytest.FinishedVideo.Error);
                }
            }
            finally
            {
                ProtokitePlaytest.ResetForNewLaunch();
                ProtokitePlaytest.VideoFrameSourceForTesting = null;
            }
        }

        [UnityTest]
        public IEnumerator TimeAwayFromTheGameIsLeftOutOfTheVideo()
        {
            if (Application.isBatchMode)
                Assert.Ignore("A batchmode editor never reaches the end of a frame, where the driver records; run the PlayMode tests in a windowed editor.");
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => new CountingFrameSource();
            try
            {
                ProtokitePlaytestDriver.StartWithTheGame();
                using (FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(VideoAnswer))))
                {
                    yield return FlockTestWait.Until(() => ProtokitePlaytest.VideoRecordingForTesting != null && ProtokitePlaytest.VideoRecordingForTesting.EncoderHasStarted,
                        "recording, from the driver alone, its encoder started");
                    double started = Time.realtimeSinceStartupAsDouble;
                    yield return new WaitForSecondsRealtime(1f);
                    // The game comes back from the background with its time away still to be measured, as a player's does.
                    ProtokitePlaytest.HandleGameLeftOrCameBack();
                    System.Threading.Thread.Sleep(1500);
                    yield return new WaitForSecondsRealtime(1f);
                    double played = Time.realtimeSinceStartupAsDouble - started;

                    ProtokitePlaytest.HandleGameQuitting();
                    ProtokitePlaytestVideoRecordingSummary video = ProtokitePlaytest.FinishedVideo;
                    Assert.IsNotNull(video, "Quitting finishes the file");
                    Assert.Less(video.VideoSeconds, played - 1.2, $"The second and a half away is not in the video ({video.VideoSeconds:0.00} s of {played:0.00} s)");
                    Assert.Greater(video.VideoSeconds, played - 2.2, $"And nothing else is left out ({video.VideoSeconds:0.00} s of {played:0.00} s)");
                }
            }
            finally
            {
                ProtokitePlaytest.ResetForNewLaunch();
                ProtokitePlaytest.VideoFrameSourceForTesting = null;
            }
        }

        [UnityTest]
        public IEnumerator TheDriverGoesThroughWhatEarlierLaunchesLeftWhenItStarts()
        {
            // A recording an earlier launch left, whose Protokite session never started.
            string run = System.IO.Path.Combine(_recordings, "Playtest", "20260101-000000-00000001");
            System.IO.Directory.CreateDirectory(run);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(run, "in-use.lock"), new byte[0]);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(run, "recording-20260101-000000-00000001.webm"), new byte[100]);

            ProtokitePlaytestDriver.StartWithTheGame();
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(System.IO.Directory.Exists(run), "Deleted by the next launch, with nothing but Unity starting the driver");
            yield return null;
        }

        [UnityTest]
        public IEnumerator WithPlaytestingOffTheDriverStillUploadsWhatEarlierLaunchesKeptAndDoesNothingElse()
        {
            _settings.PlaytestingEnabled = false;
            // A recording a playtest build left, waiting for its session.
            string run = System.IO.Path.Combine(_recordings, "Playtest", "20260101-000000-00000001");
            System.IO.Directory.CreateDirectory(run);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(run, "in-use.lock"), new byte[0]);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(run, "recording-20260101-000000-00000001.webm"), new byte[100]);
            System.IO.File.WriteAllText(System.IO.Path.Combine(run, "session.json"),
                "{\"protokite_session_id\":\"pk-9\",\"protokite_api_url\":\"http://protokite.test\",\"flock_game_version_id\":\"session-gvid\"}");
            AcceptingUploader uploader = new AcceptingUploader();
            FlockHttpClient.UseFileUploader(uploader);
            FlockFakeTransport transport = new FlockFakeTransport()
                .On("/playtest-session/pk-9/recording-upload", FlockFakeTransport.Ok(
                    "{\"result\":{\"upload_url\":\"http://storage.test/pk-9?X-Amz-Signature=1\",\"bucket\":\"b\",\"key\":\"k\"}}"))
                .On(ConfigRoute, FlockFakeTransport.Ok(Answer));
            try
            {
                ProtokitePlaytestDriver.StartWithTheGame();
                using (FlockTestClient.Create(transport))
                {
                    float until = Time.realtimeSinceStartup + 10f;
                    while (Time.realtimeSinceStartup < until && System.IO.Directory.Exists(run))
                        yield return null;
                    // A second more, in which nothing else may be sent.
                    yield return new WaitForSecondsRealtime(1f);

                    Assert.AreEqual(1, uploader.Calls, "Uploaded, with nothing but the driver Unity started");
                    Assert.AreEqual(1, transport.CountTo("/recording-upload"));
                    Assert.IsFalse(System.IO.Directory.Exists(run), "Uploaded, so no longer kept");
                    Assert.AreEqual(0, transport.CountTo(ConfigRoute), "No playtest config is asked for");
                    Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.Status);
                    Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo);
                }
            }
            finally
            {
                FlockHttpClient.UseFileUploader(null);
            }
        }

        /// <summary>Takes every file upload and answers 200.</summary>
        private sealed class AcceptingUploader : IFlockFileUploader
        {
            public int Calls;

            public System.Threading.Tasks.Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType,
                System.Threading.CancellationToken cancellationToken)
            {
                Calls++;
                return System.Threading.Tasks.Task.FromResult(new FlockFileUploadOutcome { Result = FlockHttpResult.Success, StatusCode = 200 });
            }
        }

        /// <summary>Frames of a moving picture, counted.</summary>
        private sealed class CountingFrameSource : IProtokitePlaytestFrameSource
        {
            private readonly System.Collections.Generic.List<ProtokitePlaytestCapturedFrame> _arrived = new System.Collections.Generic.List<ProtokitePlaytestCapturedFrame>();
            // The real encoder records these frames: graphics card encoders measured here refuse anything smaller than about 256x144.
            public int Width => 256;
            public int Height => 144;
            public bool IsReadyForAnotherFrame => true;
            public int Captures;
            public int FramesLostOnTheGraphicsCard => 0;
            public int FramesDroppedForWantOfABlock => 0;

            public void CaptureFrame(long timestampMs)
            {
                Captures++;
                byte[] pixels = new byte[ProtokitePlaytestNv12.FrameLength(Width, Height)];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = (byte)(timestampMs / 7 + i);
                _arrived.Add(new ProtokitePlaytestCapturedFrame(pixels, timestampMs));
            }

            public void TakeCapturedFrames(System.Collections.Generic.List<ProtokitePlaytestCapturedFrame> frames)
            {
                frames.AddRange(_arrived);
                _arrived.Clear();
            }

            public void ReturnBlock(byte[] pixels) { }
            public void Stop(System.Collections.Generic.List<ProtokitePlaytestCapturedFrame> frames) => TakeCapturedFrames(frames);
            public void Dispose() { }
        }
    }
}
