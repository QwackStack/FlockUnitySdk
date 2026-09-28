using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The heartbeat the running session raises, driven by the real player loop: it heals a failed registration and records
    // nothing on the Game Metrics dashboards, which are the game's.
    public class FlockHeartbeatTests
    {
        private string _runFolder;
        private string _folder;
        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_heartbeat_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        [UnityTest]
        public IEnumerator AHeartbeatHealsTheSessionAndRecordsNoGameplayEvent()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.AnalyticsSessions,
                FlockFakeTransport.Status(503, "{}"),
                FlockFakeTransport.Ok("{\"session_id\":\"01HEARTBEATSESSION00000000\"}"));
            transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(transport, config => config.AnalyticsConfig.HeartbeatIntervalSeconds = 0.3f);
            _h.LoginAs("player-a");
            _h.SetReachable(true);
            // What signing in runs: it wires the session and its heartbeat.
            _h.Run(() => _h.Client.Analytics.InitializeAsync(System.Threading.CancellationToken.None));
            _h.Run(() => _h.Client.Analytics.StartSessionAsync());
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.AnalyticsSessions), "Precondition: the first registration failed");

            // The heartbeat's own work, seen from outside: it registers the session again.
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.AnalyticsSessions) >= 2, "A heartbeat ran and healed the registration");

            Task flush = _h.Client.Analytics.FlushAsync();
            yield return FlockTestWait.Until(() => flush.IsCompleted, "The flush ended");

            Assert.IsFalse(transport.AllTo(FlockEndpoints.AnalyticsEvents).Any(r => r.JsonBody.Contains("sdk_heartbeat")),
                "The heartbeat records nothing on the Game Metrics dashboards");
        }
    }
}
