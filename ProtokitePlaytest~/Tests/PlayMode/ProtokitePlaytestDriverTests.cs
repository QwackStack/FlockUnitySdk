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

        // What Unity's own start-up left, read once before any test tidies it away.
        private static bool _playtestingWasOnAtStartUp;
        private static int _driversStartedByUnity = -1;

        [OneTimeSetUp]
        public void ReadWhatStartUpLeft()
        {
            if (_driversStartedByUnity >= 0)
                return;
            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            _playtestingWasOnAtStartUp = settings != null && settings.PlaytestingEnabled;
            _driversStartedByUnity = Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>().Length;
        }

        [Test]
        public void UnityStartsOneDriverExactlyWhenTheProjectHasPlaytestingOn()
        {
            Assert.AreEqual(_playtestingWasOnAtStartUp ? 1 : 0, _driversStartedByUnity,
                $"Playtesting was {(_playtestingWasOnAtStartUp ? "on" : "off")} in the project's settings when Play Mode started");
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // Entering Play Mode ran the project's own start-up: its Flock client and, with playtesting on, a driver.
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
            ProtokitePlaytest.ResetForNewLaunch();
            // A session started here must never read or write the game's own device id.
            ProtokitePlaytest.DeviceIdFilePathForTesting = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "protokite_driver_" + Guid.NewGuid().ToString("N"), "device_id.txt");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
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
        }

        [Test]
        public void TheDriverEndsTheSessionWhenTheGameQuits()
        {
            ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();
            ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();

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
            ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();
            ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();
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
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "protokite_driver_video_" + Guid.NewGuid().ToString("N"));
            CountingFrameSource source = new CountingFrameSource();
            ProtokitePlaytest.RecordingsFolderForTesting = folder;
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => source;
            try
            {
                ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();
                using (FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(VideoAnswer))))
                {
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
                ProtokitePlaytest.RecordingsFolderForTesting = null;
                if (System.IO.Directory.Exists(folder))
                    System.IO.Directory.Delete(folder, true);
            }
        }

        /// <summary>Frames of a moving picture, counted.</summary>
        private sealed class CountingFrameSource : IProtokitePlaytestFrameSource
        {
            private readonly System.Collections.Generic.List<ProtokitePlaytestCapturedFrame> _arrived = new System.Collections.Generic.List<ProtokitePlaytestCapturedFrame>();
            public int Width => 64;
            public int Height => 48;
            public bool IsReadyForAnotherFrame => true;
            public int Captures;
            public int FramesLostOnTheGraphicsCard => 0;
            public int FramesDroppedForWantOfABlock => 0;

            public void CaptureFrame(long timestampMs)
            {
                Captures++;
                byte[] pixels = new byte[ProtokitePlaytestI420.FrameLength(Width, Height)];
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
