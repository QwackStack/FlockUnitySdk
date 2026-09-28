using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>The events pipeline against a real backend: a gameplay event and a diagnostic, sent by the SDK and read back from the server's own stores by hand.</summary>
    public class FlockTrackEventLiveTests
    {
        private string _runFolder;
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_track_event_live_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
            {
                FlockClient.Instance.Authentication.Logout();
                FlockClient.Shutdown();
            }
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static IEnumerator Await(Task task, float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(task.IsCompleted, $"Ended within {seconds} s");
            if (task.IsFaulted)
                Assert.Fail(task.Exception.GetBaseException().ToString());
        }

        // Writes the run's ids to a temporary file for the shell to read the stores with.
        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; the rows are read back from Postgres and ClickHouse by hand.")]
        public IEnumerator Live_AGameplayEventAndADiagnosticReachTheirOwnStores()
        {
            FlockConfigAsset asset = Resources.Load<FlockConfigAsset>("FlockConfig");
            if (asset == null || !asset.IsValid(out string _) || string.IsNullOrEmpty(asset.gameVersionId))
            {
                Assert.Ignore("No usable FlockConfig.asset.");
                yield break;
            }
            FlockInitConfig config = asset.ToInitConfig();
            config.OfflineCacheDirectory = Path.Combine(_folder, "snapshots");
            FlockClient.Create(config, new NullFlockLogger());

            string run = Guid.NewGuid().ToString("N").Substring(0, 12);
            yield return Await(FlockClient.Instance.Authentication.RegisterWithDeviceAsync("events-live-" + run, "events-live"), 30);
            string player = FlockClient.Instance.CurrentPlayerId;
            Assert.IsFalse(string.IsNullOrEmpty(player), "Registered and signed in");

            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "level", 3 }, { "big", 9007199254740993L }, { "time", 2.5 }, { "won", true }, { "none", null },
                { "items", new List<object> { 1, "a", false } }, { "nested", new Dictionary<string, object> { { "depth", 1 } } }
            };
            Assert.IsTrue(FlockClient.Instance.Analytics.TrackEvent("events_live_typed", properties, "events_live"));
            Assert.IsFalse(FlockClient.Instance.Analytics.TrackEvent(new string('n', 201)), "Refused before it could fail the batch");
            FlockClient.Instance.Analytics.LogDiagnosticEvent("events_live_diagnostic_" + run, new Dictionary<string, object> { { "run", run } });
#pragma warning disable CS0618
            FlockClient.Instance.Analytics.LogEvent("events_live_former_name_" + run);
#pragma warning restore CS0618
            yield return Await(FlockClient.Instance.Analytics.FlushAsync(), 30);

            string report = Path.Combine(Path.GetTempPath(), "flock-events-live-run.txt");
            File.WriteAllText(report, player + "\n" + run + "\n" + FlockClient.Instance.ServerSessionId + "\n");
            Debug.Log($"[events live] player={player} run={run} session={FlockClient.Instance.ServerSessionId} -> {report}");
        }
    }
}
