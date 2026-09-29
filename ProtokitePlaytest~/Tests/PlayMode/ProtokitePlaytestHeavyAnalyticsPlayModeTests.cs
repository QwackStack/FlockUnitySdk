using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    // Heavy analytics fed by Unity's own player loop and the driver Unity starts: a real scene load, and the frames around it.
    public class ProtokitePlaytestHeavyAnalyticsPlayModeTests : IPrebuildSetup, IPostBuildCleanup
    {
        private const string ScenePath = "Assets/ProtokitePlaytestHeavyAnalyticsTestScene.unity";
        private const string SceneName = "ProtokitePlaytestHeavyAnalyticsTestScene";
        private const string AddedLevelName = "ProtokitePlaytestAddedLevel";
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string EventsRoute = "/analytics/events";
        private const string Answer =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"driven\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"heavy_analytics\":true},\"form\":null}}";

        private ProtokitePlaytestSettings _settings;
        private bool _wasEnabled;
        private string _oldUrl;
        private string _folder;
        private IDisposable _analyticsFolder;
        private ProtokitePlaytestConsentForPlayModeTests _consent;

        // An empty scene of the test's own, in the build list while the tests run, so SceneManager can load it by path.
        public void Setup()
        {
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive), ScenePath);
            List<UnityEditor.EditorBuildSettingsScene> scenes = new List<UnityEditor.EditorBuildSettingsScene>(UnityEditor.EditorBuildSettings.scenes);
            scenes.Add(new UnityEditor.EditorBuildSettingsScene(ScenePath, true));
            UnityEditor.EditorBuildSettings.scenes = scenes.ToArray();
#endif
        }

        public void Cleanup()
        {
#if UNITY_EDITOR
            List<UnityEditor.EditorBuildSettingsScene> scenes = new List<UnityEditor.EditorBuildSettingsScene>(UnityEditor.EditorBuildSettings.scenes);
            scenes.RemoveAll(scene => scene.path == ScenePath);
            UnityEditor.EditorBuildSettings.scenes = scenes.ToArray();
            UnityEditor.AssetDatabase.DeleteAsset(ScenePath);
#endif
        }

        // Before the driver's Update, once, as a player's return from the background: the return is announced, then a second passes.
        [DefaultExecutionOrder(-32000)]
        private sealed class ReturnFromTheBackground : MonoBehaviour
        {
            private bool _returned;

            private void Update()
            {
                if (_returned)
                    return;
                _returned = true;
                ProtokitePlaytest.HandleGameLeftOrCameBack();
                Thread.Sleep(1000);
            }
        }

        // After every Update of a frame: how many frame times the window in progress has counted.
        private sealed class CountRecorder : MonoBehaviour
        {
            public readonly Dictionary<int, int> CountedAfterFrame = new Dictionary<int, int>();

            private void LateUpdate()
            {
                IReadOnlyList<double> counted = ProtokitePlaytest.PerformanceFrameTimesMsForTesting;
                if (counted != null)
                    CountedAfterFrame[Time.frameCount] = counted.Count;
            }
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
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
            _folder = Path.Combine(Path.GetTempPath(), "protokite_heavy_play_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            _analyticsFolder = FlockTestSavedFiles.UseFolderOfItsOwn();
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
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            _analyticsFolder?.Dispose();
            _consent?.Dispose();
            _consent = null;
            if (_settings != null)
            {
                _settings.PlaytestingEnabled = _wasEnabled;
                _settings.ProtokiteApiUrl = _oldUrl;
            }
            try
            {
                if (_folder != null && Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private static FlockFakeTransport HeavyAnalyticsTransport()
        {
            FlockFakeTransport transport = new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(Answer));
            transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            return transport;
        }

        private static JObject SentLevelLoaded(FlockTestClient flock, FlockFakeTransport transport)
        {
            flock.Run(() => flock.Client.Analytics.FlushAsync());
            return transport.AllTo(EventsRoute)
                .SelectMany(request => (JArray)JObject.Parse(request.JsonBody)["events"])
                .Cast<JObject>()
                .Single(e => (string)e["event_name"] == ProtokitePlaytestEvents.LevelLoaded);
        }

        [UnityTest, PrebuildSetup(typeof(ProtokitePlaytestHeavyAnalyticsPlayModeTests)), PostBuildCleanup(typeof(ProtokitePlaytestHeavyAnalyticsPlayModeTests))]
        public IEnumerator ALevelTheGameAddsAndMakesActiveIsTheMapTheNextLoadLeaves()
        {
            ProtokitePlaytestDriver.StartWithTheGame();
            FlockFakeTransport transport = HeavyAnalyticsTransport();
            using (FlockTestClient flock = FlockTestClient.Create(transport))
            {
                flock.SetReachable(true);
                flock.LoginAs("player-a");
                yield return FlockTestWait.Until(() => ProtokitePlaytest.IsMeasuringPerformance, "heavy analytics started, from the driver alone");

                // Added beside the scene playing and made active, as a game whose first scene never unloads moves between levels.
                SceneManager.SetActiveScene(SceneManager.CreateScene(AddedLevelName));
                yield return null;
                yield return null;
                SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
                yield return FlockTestWait.Until(() => SceneManager.GetActiveScene().name == SceneName, "the scene loaded");
                for (int frame = 0; frame < 3; frame++)
                    yield return null;

                JObject level = SentLevelLoaded(flock, transport);
                Assert.AreEqual(SceneName, (string)level["properties"]["map"]);
                Assert.AreEqual(AddedLevelName, (string)level["properties"]["previous_map"], "The scene that was active, though no load made it so");
            }
        }

        [UnityTest, PrebuildSetup(typeof(ProtokitePlaytestHeavyAnalyticsPlayModeTests)), PostBuildCleanup(typeof(ProtokitePlaytestHeavyAnalyticsPlayModeTests))]
        public IEnumerator ARealSceneLoadsStretchedFrameIsLeftOutAndTheNextFrameCounts()
        {
            ProtokitePlaytestDriver.StartWithTheGame();
            FlockFakeTransport transport = HeavyAnalyticsTransport();
            using (FlockTestClient flock = FlockTestClient.Create(transport))
            {
                flock.SetReachable(true);
                flock.LoginAs("player-a");
                yield return FlockTestWait.Until(() => ProtokitePlaytest.IsMeasuringPerformance, "heavy analytics started, from the driver alone");

                GameObject host = new GameObject("CountRecorder");
                Object.DontDestroyOnLoad(host);
                CountRecorder recorder = host.AddComponent<CountRecorder>();
                int loadFinishedInFrame = -1;
                UnityEngine.Events.UnityAction<Scene, LoadSceneMode> slowLoad = (scene, mode) =>
                {
                    loadFinishedInFrame = Time.frameCount;
                    // A scene whose objects take 400 ms to wake.
                    Thread.Sleep(400);
                };
                SceneManager.sceneLoaded += slowLoad;
                // In slow motion: what is measured is the frames the player saw, not the game's scaled time.
                float timeScaleAsItWas = Time.timeScale;
                Time.timeScale = 0.5f;
                try
                {
                    yield return null;
                    yield return null;
                    SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
                    yield return FlockTestWait.Until(() => loadFinishedInFrame >= 0, "the scene loaded");
                    for (int frame = 0; frame < 3; frame++)
                        yield return null;
                }
                finally
                {
                    SceneManager.sceneLoaded -= slowLoad;
                    Time.timeScale = timeScaleAsItWas;
                }

                Dictionary<int, int> countedAfter = recorder.CountedAfterFrame;
                Object.Destroy(host);
                // The load finished before its frame's Update, so that Update's time is the one that carries it.
                Assert.AreEqual(countedAfter[loadFinishedInFrame - 1], countedAfter[loadFinishedInFrame], "The frame that carries the load is not counted");
                Assert.AreEqual(countedAfter[loadFinishedInFrame] + 1, countedAfter[loadFinishedInFrame + 1], "And the next frame is");
                IReadOnlyList<double> counted = ProtokitePlaytest.PerformanceFrameTimesMsForTesting;
                Assert.IsNotNull(counted);
                Assert.IsFalse(counted.Any(ms => ms >= 400.0), "No counted frame holds the 400 ms the load took: " + string.Join(", ", counted));

                JObject level = SentLevelLoaded(flock, transport);
                Assert.AreEqual(ProtokitePlaytestEvents.Category, (string)level["event_category"]);
                Assert.AreEqual(SceneName, (string)level["properties"]["map"]);
                Assert.GreaterOrEqual((double)level["properties"]["load_seconds"], 0.4, "The whole stall, in real time, whatever the time scale");
            }
        }

        [UnityTest]
        public IEnumerator TimeAwayFromTheGameIsLeftOutOfTheFrameThatCarriesIt()
        {
            ProtokitePlaytestDriver.StartWithTheGame();
            using (FlockTestClient flock = FlockTestClient.Create(HeavyAnalyticsTransport()))
            {
                flock.SetReachable(true);
                flock.LoginAs("player-a");
                yield return FlockTestWait.Until(() => ProtokitePlaytest.IsMeasuringPerformance, "heavy analytics started, from the driver alone");
                yield return null;
                int countedBefore = ProtokitePlaytest.PerformanceFrameTimesMsForTesting.Count;

                GameObject host = new GameObject("ReturnFromTheBackground");
                host.AddComponent<ReturnFromTheBackground>();
                for (int frame = 0; frame < 3; frame++)
                    yield return null;
                Object.Destroy(host);

                IReadOnlyList<double> counted = ProtokitePlaytest.PerformanceFrameTimesMsForTesting;
                Assert.IsFalse(counted.Any(ms => ms >= 1000.0), "The second away is in no counted frame: " + string.Join(", ", counted));
                Assert.AreEqual(countedBefore + 2, counted.Count, "Only the frame that carried it is left out");
            }
        }
    }
}
