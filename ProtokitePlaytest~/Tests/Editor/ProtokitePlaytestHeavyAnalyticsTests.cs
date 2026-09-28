using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>Heavy analytics through the playtest and the Flock SDK's own event queue: what is sent, when, and what stops it.</summary>
    public class ProtokitePlaytestHeavyAnalyticsTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string SessionStartRoute = "/game/sdk/playtest-session";
        private const string FlockSessionRoute = "/analytics/sessions";
        // The Flock SDK's gameplay events route.
        private const string EventsRoute = "/analytics/events";
        private const long Megabyte = 1024 * 1024;

        private ProtokitePlaytestSettingsForTests _settings;
        private IDisposable _analyticsFolder;
        private string _folder;
        private FlockTestClient _h;
        private FlockFakeTransport _transport;

        private static string Config(bool heavyAnalytics) =>
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"heavy_analytics\":"
            + (heavyAnalytics ? "true" : "false") + "},\"form\":null}}";

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_heavy_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            ProtokitePlaytest.MemoryUsedBytesForTesting = () => 512 * Megabyte;
            // These tests count what a launch sends, so no test's queued events may reach another's.
            _analyticsFolder = FlockTestSavedFiles.UseFolderOfItsOwn();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            ProtokitePlaytest.ResetForNewLaunch();
            ProtokitePlaytest.MemoryUsedBytesForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            // Guarded: a SetUp precondition that failed made neither, and its message must not be buried under this one's.
            _analyticsFolder?.Dispose();
            _settings?.Dispose();
            try
            {
                if (Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        // A Flock client with a signed-in player (events wait for one) and the playtest ready with this config.
        private void Start(bool heavyAnalytics = true, Action<FlockInitConfig> tweak = null)
        {
            _transport = new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Config(heavyAnalytics)));
            _transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(_transport, tweak);
            _h.SetReachable(true);
            _h.LoginAs("player-a");
            ProtokitePlaytest.Refresh();
            Assert.AreEqual(ProtokitePlaytestStatus.Ready, ProtokitePlaytest.Status, "Precondition: the playtest is ready");
        }

        // Frames of play, each the real time since the driver's last Update.
        private static void Play(int frames, double frameSeconds = 0.25)
        {
            for (int i = 0; i < frames; i++)
                ProtokitePlaytest.UpdateHeavyAnalytics(frameSeconds);
        }

        private List<JObject> SentPlaytestEvents(string eventName)
        {
            _h.Run(() => _h.Client.Analytics.FlushAsync());
            return _transport.AllTo(EventsRoute)
                .SelectMany(request => (JArray)JObject.Parse(request.JsonBody)["events"])
                .Cast<JObject>()
                .Where(e => (string)e["event_category"] == ProtokitePlaytestEvents.Category && (string)e["event_name"] == eventName)
                .ToList();
        }

        private static JObject Properties(JObject sentEvent) => (JObject)sentEvent["properties"];

        [Test]
        public void HeavyAnalyticsOffInThePlaytestRecordsNothing()
        {
            Start(heavyAnalytics: false);
            Play(240);
            Assert.IsFalse(ProtokitePlaytest.IsMeasuringPerformance);
            Assert.IsFalse(ProtokitePlaytest.RecordPlaytestEvent("boss_fight_started"));
            _h.Run(() => _h.Client.Analytics.FlushAsync());
            Assert.IsFalse(_transport.Sent(EventsRoute), "No playtest event, nor any other");
        }

        [Test]
        public void PlaytestingOffRecordsNothingWhateverThePlaytestSays()
        {
            _settings.Settings.PlaytestingEnabled = false;
            _transport = new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Config(true)));
            _transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(_transport);
            _h.LoginAs("player-a");
            ProtokitePlaytest.Refresh();
            Play(240);
            Assert.AreEqual(ProtokitePlaytestStatus.TurnedOff, ProtokitePlaytest.Status);
            Assert.IsFalse(ProtokitePlaytest.IsMeasuringPerformance);
            Assert.IsFalse(_transport.Sent(ConfigRoute), "Not even asked");
        }

        [Test]
        public void SixWindowsInAMinuteOfPlayEachWithItsTypedFigures()
        {
            Start();
            Play(240);
            List<JObject> windows = SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow);
            Assert.AreEqual(6, windows.Count, "A minute of play at four frames a second");

            JObject properties = Properties(windows[0]);
            Assert.AreEqual(JTokenType.Float, properties["window_seconds"].Type);
            Assert.AreEqual(10.0, (double)properties["window_seconds"]);
            Assert.AreEqual(JTokenType.Integer, properties["frames"].Type);
            Assert.AreEqual(40, (int)properties["frames"]);
            Assert.AreEqual(250.0, (double)properties["median_frame_time_ms"]);
            Assert.AreEqual(250.0, (double)properties["frame_time_95th_percentile_ms"]);
            Assert.AreEqual(250.0, (double)properties["frame_time_99th_percentile_ms"]);
            Assert.AreEqual(JTokenType.Integer, properties["hitches"].Type);
            Assert.AreEqual(40, (int)properties["hitches"], "Every 250 ms frame is over the 60 ms default");
            Assert.AreEqual(60.0, (double)properties["hitch_threshold_ms"]);
            Assert.AreEqual(JTokenType.Integer, properties["memory_used_mb"].Type);
            Assert.AreEqual(512, (long)properties["memory_used_mb"]);
            Assert.AreEqual(512, (long)properties["memory_peak_mb"]);

            string scene = SceneManager.GetActiveScene().name;
            List<string> expected = new List<string> { "window_seconds", "frames", "median_frame_time_ms", "frame_time_95th_percentile_ms",
                "frame_time_99th_percentile_ms", "hitches", "hitch_threshold_ms", "memory_used_mb", "memory_peak_mb" };
            if (!string.IsNullOrEmpty(scene))
                expected.Add("map");
            CollectionAssert.AreEquivalent(expected, properties.Properties().Select(p => p.Name), "These names and no others; the session's own figures are the Flock SDK's");
            if (!string.IsNullOrEmpty(scene))
                Assert.AreEqual(scene, (string)properties["map"], "The scene playing when heavy analytics started");
        }

        [Test]
        public void TimeInTheBackgroundIsNotPlay()
        {
            Start();
            Play(20);
            ProtokitePlaytest.HandleGameLeftOrCameBack();
            Play(1, 300.0);
            Play(20);
            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.AreEqual(10.0, (double)window["window_seconds"]);
            Assert.AreEqual(40, (int)window["frames"]);
            Assert.AreEqual(250.0, (double)window["frame_time_99th_percentile_ms"], "Five minutes away are in no percentile");
        }

        [Test]
        public void ASceneLoadIsLeftOutOfPlayAndReportedWithTheTimeItHeldTheGameUp()
        {
            Start();
            string startScene = SceneManager.GetActiveScene().name;
            Play(10);
            // A load happens between two Updates, so the time the next one measures carries it.
            ProtokitePlaytest.NoteSceneLoaded("Level2", true);
            Play(1, 3.0);
            Play(30);

            JObject level = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.LevelLoaded).Single());
            Assert.AreEqual("Level2", (string)level["map"]);
            Assert.AreEqual(3.0, (double)level["load_seconds"], "The frame the load held up");
            if (string.IsNullOrEmpty(startScene))
                Assert.IsNull(level["previous_map"]);
            else
                Assert.AreEqual(startScene, (string)level["previous_map"]);

            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.AreEqual(40, (int)window["frames"], "Every frame but the one that carries the load");
            Assert.AreEqual(250.0, (double)window["frame_time_99th_percentile_ms"]);
            Assert.AreEqual("Level2", (string)window["map"]);
        }

        [Test]
        public void TwoLoadsInConsecutiveFramesEachLeaveOutTheirOwnFrame()
        {
            Start();
            Play(10);
            ProtokitePlaytest.NoteSceneLoaded("Level2", true);
            Play(1, 3.0);
            ProtokitePlaytest.NoteSceneLoaded("Level3", true);
            Play(1, 4.0);
            Play(30);

            List<JObject> levels = SentPlaytestEvents(ProtokitePlaytestEvents.LevelLoaded);
            Assert.AreEqual(2, levels.Count);
            Assert.AreEqual("Level2", (string)Properties(levels[0])["map"]);
            Assert.AreEqual(3.0, (double)Properties(levels[0])["load_seconds"]);
            Assert.AreEqual("Level3", (string)Properties(levels[1])["map"]);
            Assert.AreEqual("Level2", (string)Properties(levels[1])["previous_map"]);
            Assert.AreEqual(4.0, (double)Properties(levels[1])["load_seconds"]);

            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.AreEqual(40, (int)window["frames"], "Both loads' frames left out, and only those");
            Assert.AreEqual("Level3", (string)window["map"]);
        }

        [Test]
        public void ASceneAddedBesideTheCurrentOneIsNoNewLevelButIsStillNotPlay()
        {
            Start();
            string startScene = SceneManager.GetActiveScene().name;
            Play(10);
            ProtokitePlaytest.NoteSceneLoaded("Hud", false);
            Play(1, 3.0);
            Play(30);
            Assert.IsEmpty(SentPlaytestEvents(ProtokitePlaytestEvents.LevelLoaded));
            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.AreEqual(40, (int)window["frames"]);
            if (!string.IsNullOrEmpty(startScene))
                Assert.AreEqual(startScene, (string)window["map"], "The level is still the one it was");
        }

        [Test]
        public void StoppingDropsTheWindowCutShortAndTheLoadStillWaiting()
        {
            Start();
            // Two seconds of frames unlike the ones after, so a window that kept them could not look like one that did not.
            Play(20, 0.1);
            ProtokitePlaytest.NoteSceneLoaded("Level2", true);
            _h.Dispose();
            _h = null;
            Play(1);
            Assert.IsFalse(ProtokitePlaytest.IsMeasuringPerformance, "Precondition: the Flock client that loaded the playtest has gone");

            Start();
            Play(40);
            List<JObject> windows = SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow);
            Assert.AreEqual(1, windows.Count);
            Assert.AreEqual(40, (int)Properties(windows[0])["frames"], "The two seconds before the stop were dropped, not added to");
            Assert.AreEqual(250.0, (double)Properties(windows[0])["median_frame_time_ms"]);
            Assert.IsEmpty(SentPlaytestEvents(ProtokitePlaytestEvents.LevelLoaded), "Nor was the load that was waiting for its frame");
        }

        [UnityTest]
        public IEnumerator APlaytestThatClosesStopsHeavyAnalytics()
        {
            _transport = new FlockFakeTransport()
                .On(ConfigRoute, FlockFakeTransport.Ok(Config(true)))
                .On(SessionStartRoute, FlockFakeTransport.Status(400, "{\"detail\":\"This playtest is not launchable right now\"}"))
                .On(FlockSessionRoute, FlockFakeTransport.Ok("{\"session_id\":\"01K5SRVSESSION000000000001\"}"));
            _transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(_transport);
            _h.SetReachable(true);
            ProtokitePlaytest.Refresh();
            Play(1);
            Assert.IsTrue(ProtokitePlaytest.IsMeasuringPerformance, "Precondition: measuring while the playtest is open");

            // A Flock session reaches the server, and Protokite answers the playtest's session start that the playtest closed.
            LogAssert.Expect(LogType.Warning, new Regex("This playtest has closed"));
            _h.Client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            _h.Run(() => _h.Client.Analytics.StartSessionAsync());
            ProtokitePlaytest.Refresh();
            float until = Time.realtimeSinceStartup + 5f;
            while (ProtokitePlaytest.Status != ProtokitePlaytestStatus.PlaytestNoLongerCollecting && Time.realtimeSinceStartup < until)
                yield return null;
            Assert.AreEqual(ProtokitePlaytestStatus.PlaytestNoLongerCollecting, ProtokitePlaytest.Status, "Precondition: the playtest closed");

            Play(1);
            Assert.IsFalse(ProtokitePlaytest.IsMeasuringPerformance, "Nothing is measured for a playtest that closed");
            Assert.IsFalse(ProtokitePlaytest.RecordPlaytestEvent("boss_fight_started"));
        }

        [Test]
        public void WithTheFlockSdksAnalyticsOffNothingIsMeasuredAndTheStudioIsToldOnce()
        {
            Regex told = new Regex(@"turns heavy analytics on, but the Flock SDK's analytics is off.*Analytics Enabled");
            int warnings = 0;
            Application.LogCallback count = (message, stackTrace, type) =>
            {
                if (type == LogType.Warning && told.IsMatch(message))
                    warnings++;
            };
            Application.logMessageReceived += count;
            try
            {
                LogAssert.Expect(LogType.Warning, told);
                Start(tweak: config => config.AnalyticsConfig.Enabled = false);
                Play(240);
            }
            finally
            {
                Application.logMessageReceived -= count;
            }
            Assert.AreEqual(1, warnings, "Once, not once a frame");
            Assert.IsFalse(ProtokitePlaytest.IsMeasuringPerformance);
            Assert.IsFalse(ProtokitePlaytest.RecordPlaytestEvent("boss_fight_started"));
        }

        [Test]
        public void TheGamesOwnPlaytestEventsAreFiledWithThePlaytestsFromAnyThread()
        {
            Start();
            Play(1);
            Assert.IsTrue(ProtokitePlaytest.IsMeasuringPerformance, "Precondition: measuring from the first frame");

            LogAssert.Expect(LogType.Warning, new Regex("'performance_window' refused: the playtest sends events with that name itself"));
            LogAssert.Expect(LogType.Warning, new Regex("'level_loaded' refused: the playtest sends events with that name itself"));
            Assert.IsFalse(ProtokitePlaytest.RecordPlaytestEvent(ProtokitePlaytestEvents.PerformanceWindow));
            Assert.IsFalse(ProtokitePlaytest.RecordPlaytestEvent(ProtokitePlaytestEvents.LevelLoaded));

            Assert.IsTrue(ProtokitePlaytest.RecordPlaytestEvent("boss_fight_started", new Dictionary<string, object> { { "boss", "hydra" }, { "attempt", 3 } }));
            Assert.IsTrue(ProtokitePlaytest.RecordPlaytestEvent("Performance_Window"), "Another name: the server tells names apart by letter case");
            Assert.IsTrue(Task.Run(() => ProtokitePlaytest.RecordPlaytestEvent("checkpoint_reached")).Result, "From a thread of the game's own");

            JObject boss = Properties(SentPlaytestEvents("boss_fight_started").Single());
            Assert.AreEqual("hydra", (string)boss["boss"]);
            Assert.AreEqual(JTokenType.Integer, boss["attempt"].Type);
            Assert.AreEqual(1, SentPlaytestEvents("Performance_Window").Count);
            Assert.AreEqual(1, SentPlaytestEvents("checkpoint_reached").Count);
        }

        [Test]
        public void TheHitchThresholdIsThePlaytestSettings()
        {
            _settings.Settings.HitchFrameTimeMs = 250f;
            Start();
            // 20 frames a hair under the threshold, then 21 at it: the window closes on the last, at 10.248 seconds.
            Play(20, 0.2499);
            Play(21);
            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.AreEqual(250.0, (double)window["hitch_threshold_ms"]);
            Assert.AreEqual(21, (int)window["hitches"], "The frames at the threshold, not those a hair under it");
        }

        [TestCase(0f)]
        [TestCase(-5f)]
        [TestCase(float.NaN)]
        public void AThresholdSetFromCodeUnderTheMinimumReadsAsTheMinimum(float setFromCode)
        {
            _settings.Settings.HitchFrameTimeMs = setFromCode;
            Start();
            // Half a millisecond, under the 1 ms minimum, then 40 frames over it.
            Play(1, 0.0005);
            Play(40);
            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.AreEqual(1.0, (double)window["hitch_threshold_ms"]);
            Assert.AreEqual(41, (int)window["frames"]);
            Assert.AreEqual(40, (int)window["hitches"], "Counted against the minimum too, so the quick frame is no hitch");
        }

        [Test]
        public void TheProcessesMemoryIsReadWhereTheEditorReportsIt()
        {
            ProtokitePlaytest.MemoryUsedBytesForTesting = null;
            Start();
            Play(40);
            JObject window = Properties(SentPlaytestEvents(ProtokitePlaytestEvents.PerformanceWindow).Single());
            Assert.IsNotNull(window["memory_used_mb"], "The editor reports its process's memory");
            Assert.Greater((long)window["memory_used_mb"], 0);
            Assert.GreaterOrEqual((long)window["memory_peak_mb"], (long)window["memory_used_mb"]);
        }
    }
}
