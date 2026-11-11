using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>Config reads as the signed-in player against a local backend, compared with the backend's own answers. Run by hand: FLOCK_PLAYER_CONFIG_LIVE names the file Qwacks/Libraries/Unity/player-config/o41_live.py writes.</summary>
    public class FlockPlayerConfigLiveTests
    {
        private const string UnknownId = "01ZZZZZZZZZZZZZZZZZZZZZZZZ";
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _folder = Path.Combine(Path.GetTempPath(), "flock_player_config_live_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
            {
                FlockClient.Instance.Authentication.Logout();
                FlockClient.Shutdown();
            }
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        [UnityTest, Explicit("Hits a local Flock backend set up by o41_live.py; run by hand.")]
        public IEnumerator Live_EachPlayerReadsTheirOwnValues_AndNobodyReadsTheGames()
        {
            string livePath = Environment.GetEnvironmentVariable("FLOCK_PLAYER_CONFIG_LIVE");
            if (string.IsNullOrEmpty(livePath) || !File.Exists(livePath))
                Assert.Ignore("FLOCK_PLAYER_CONFIG_LIVE names no file.");
            JObject live = JObject.Parse(File.ReadAllText(livePath));
            string configId = (string)live["config_id"];
            RecordingFlockLogger logger = new RecordingFlockLogger();
            FlockInitConfig config = new FlockInitConfig((string)live["api_url"], (string)JObject.Parse(File.ReadAllText((string)live["key_file"]))["api_key"],
                (string)live["game_id"], "1.0.0", analyticsConfig: new FlockAnalyticsConfig
                {
                    PersistSessionOnDisk = false, AutoStartSession = false, HeartbeatIntervalSeconds = 0f, EventBufferFlushIntervalSeconds = 0f
                })
            {
                GameVersionId = (string)live["version_id"],
                EnableOfflineCache = true,
                OfflineCacheDirectory = Path.Combine(_folder, "snapshots")
            };
            FlockClient.Create(config, logger);
            FlockClient.Instance.ReachabilityProbe = () => true;

            Task<JObject> signedOut = FlockClient.Instance.Config.GetByConfigIdAsync<JObject>(configId);
            yield return Await(signedOut);
            AssertSame(live["game_values"], signedOut.Result, "Nobody signed in: the game's values");

            Task<PlayerLoginResponse> a = FlockClient.Instance.Authentication.LoginWithDeviceAsync((string)live["a_device"]);
            yield return Await(a);
            Task<JObject> forA = FlockClient.Instance.Config.GetByConfigIdAsync<JObject>(configId);
            yield return Await(forA);
            AssertSame(live["a_values"], forA.Result, "A: the segment's override and A's own, as the backend answers A");

            // Counter-case: an id no config has falls back to the game's read and fails with that read's own code, as before.
            Task<JObject> unknown = FlockClient.Instance.Config.GetByConfigIdAsync<JObject>(UnknownId);
            yield return Settle(unknown);
            Assert.AreEqual(FlockErrorCode.GamePatchGameConfigNotFound, (unknown.Exception?.GetBaseException() as FlockException)?.ErrorCode);
            Assert.IsFalse(logger.Logged(logger.Warnings, "Config values"), "Only no Game Version warns");

            Task<PlayerLoginResponse> b = FlockClient.Instance.Authentication.LoginWithDeviceAsync((string)live["b_device"]);
            yield return Await(b);
            Task<JObject> forB = FlockClient.Instance.Config.GetByConfigIdAsync<JObject>(configId);
            yield return Await(forB);
            AssertSame(live["b_values"], forB.Result, "Counter-case: B is in no segment and has no override, so never A's values");

            FlockClient.Instance.Authentication.Logout();
            Task<JObject> afterSignOut = FlockClient.Instance.Config.GetByConfigIdAsync<JObject>(configId);
            yield return Await(afterSignOut);
            AssertSame(live["game_values"], afterSignOut.Result, "Signed out again: the game's values");
        }

        private static void AssertSame(JToken expected, JObject read, string what)
        {
            Assert.IsNotNull(expected, "live.json has the backend's answer");
            Assert.IsTrue(JToken.DeepEquals(expected, read), $"{what}: backend {expected.ToString(Newtonsoft.Json.Formatting.None)}, SDK {read?.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        private static IEnumerator Await(Task task, float seconds = 30f)
        {
            yield return Settle(task, seconds);
            if (!task.IsFaulted)
                yield break;
            Exception failure = task.Exception.GetBaseException();
            if (failure is ResultStateException)
                throw failure;
            Assert.Fail(failure.ToString());
        }

        private static IEnumerator Settle(Task task, float seconds = 30f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(task.IsCompleted, $"Ended within {seconds} s");
        }
    }
}
