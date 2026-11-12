using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Flock.Analytics;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    /// <summary>The flock_ parameters on every analytics event, app_open and first_launch, and the Send Default Events opt-out.</summary>
    public class FlockDefaultEventsTests
    {
        private const string ConsentGrantedKey = "flock_analytics_consent";
        private const string ConsentSetKey = "flock_analytics_consent_set";

        private string _runFolder;
        private string _folder;
        private readonly List<FlockTestClient> _clients = new List<FlockTestClient>();
        private int _savedFirstLaunch;
        private static readonly string FirstLaunchPref = FlockDefaultEvents.FirstLaunchRecordedPrefFor(FlockTestGame.Key);

        [SetUp]
        public void SetUp()
        {
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_default_events_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
            _savedFirstLaunch = PlayerPrefs.GetInt(FirstLaunchPref, 0);
            PlayerPrefs.DeleteKey(FirstLaunchPref);
            // A consent answer saved on this machine beats Require Explicit Consent.
            PlayerPrefs.DeleteKey(ConsentGrantedKey);
            PlayerPrefs.DeleteKey(ConsentSetKey);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (FlockTestClient client in _clients)
                client.Dispose();
            _clients.Clear();
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            PlayerPrefs.DeleteKey(ConsentGrantedKey);
            PlayerPrefs.DeleteKey(ConsentSetKey);
            if (_savedFirstLaunch != 0)
                PlayerPrefs.SetInt(FirstLaunchPref, _savedFirstLaunch);
            else
                PlayerPrefs.DeleteKey(FirstLaunchPref);
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private FlockTestClient Start(FlockFakeTransport transport, Action<Flock.Config.FlockInitConfig> tweak = null)
        {
            transport.Default(request => request.Url.Contains("/" + FlockEndpoints.AnalyticsSessions) && request.Method == "POST"
                ? FlockFakeTransport.Ok("{\"session_id\":\"01K5SERVERSESSION00000001\"}")
                : FlockFakeTransport.Ok("{\"ok\":true}"));
            FlockTestClient h = FlockTestClient.Create(transport, config =>
            {
                config.AnalyticsConfig.SendDefaultEvents = true;
                config.AnalyticsConfig.TrackFps = false;
                tweak?.Invoke(config);
            });
            h.SetReachable(true);
            _clients.Add(h);
            return h;
        }

        private static void Flush(FlockTestClient h) => h.Run(() => h.Client.Analytics.FlushAsync());

        private static List<JObject> Sent(FlockFakeTransport transport) =>
            transport.AllTo(FlockEndpoints.AnalyticsEvents)
                .SelectMany(r => (JArray)JObject.Parse(r.JsonBody)["events"])
                .Cast<JObject>()
                .ToList();

        [Test]
        public void EveryEventCarriesTheFlockParametersAndTheGamesOwnValueIsKept()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport, config => config.AnalyticsConfig.SendDefaultEvents = false);
            h.LoginAs("player-a");
            Dictionary<string, object> mine = new Dictionary<string, object> { { "level", 3 }, { "flock_platform", "mine" } };

            Assert.IsTrue(h.Client.Analytics.TrackEvent("level_start", mine));
            Flush(h);

            JObject properties = (JObject)Sent(transport).Single()["properties"];
            Assert.AreEqual("mine", (string)properties["flock_platform"], "A property the game set keeps its value");
            Assert.AreEqual(SystemInfo.operatingSystem, (string)properties["flock_os"]);
            Assert.AreEqual(SystemInfo.deviceModel, (string)properties["flock_device_model"]);
            Assert.AreEqual(Application.version, (string)properties["flock_app_version"]);
            Assert.AreEqual(FlockSdkVersion.Current, (string)properties["flock_sdk_version"]);
            Assert.IsNull(properties["flock_session_number"], "No session runs, so no session number");
            Assert.AreEqual(3, (int)properties["level"]);
            Assert.AreEqual(2, mine.Count, "The game's own dictionary is never changed");
        }

        [Test]
        public void AnEventDuringASessionCarriesItsSessionNumber()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport, config => config.AnalyticsConfig.SendDefaultEvents = false);
            h.LoginAs("player-a");
            h.Run(() => h.Client.Analytics.InitializeAsync(CancellationToken.None));
            h.Run(() => h.Client.Analytics.StartSessionAsync());

            Assert.IsTrue(h.Client.Analytics.TrackEvent("level_start"));
            Flush(h);

            Assert.AreEqual(h.Client.Session.SessionNumber, (int)Sent(transport).Single()["properties"]["flock_session_number"]);
        }

        [Test]
        public void AppOpenAtEachStartAndFirstLaunchOnlyOnceAnInstall()
        {
            FlockFakeTransport first = new FlockFakeTransport();
            FlockTestClient h = Start(first);
            h.LoginAs("player-a");
            h.Run(() => h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush(h);
            List<JObject> firstEvents = Sent(first);
            CollectionAssert.AreEquivalent(new[] { "app_open", "first_launch" }, firstEvents.Select(e => (string)e["event_name"]).ToArray());
            Assert.IsTrue(firstEvents.All(e => (string)e["player_id"] == "player-a"), "Recorded before sign-in, credited to the player who signed in");
            Assert.IsTrue(firstEvents.All(e => (string)e["event_category"] == "app"));
            Assert.IsTrue(firstEvents.All(e => (string)e["properties"]["flock_sdk_version"] == FlockSdkVersion.Current));
            Assert.AreEqual(1, PlayerPrefs.GetInt(FirstLaunchPref, 0), "Marked once recorded");
            h.Dispose();
            _clients.Remove(h);
            // The test client puts the developer's own value back when it closes; this install has recorded first_launch.
            PlayerPrefs.SetInt(FirstLaunchPref, 1);

            FlockFakeTransport second = new FlockFakeTransport();
            FlockTestClient again = Start(second);
            again.LoginAs("player-a");
            again.Run(() => again.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush(again);
            CollectionAssert.AreEqual(new[] { "app_open" }, Sent(second).Select(e => (string)e["event_name"]).ToArray(), "first_launch once an install");
        }

        [Test]
        public void WithoutConsentAtStartTheLaunchEventsAreRecordedOnlyOnceConsentIsGiven()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport, config => config.AnalyticsConfig.RequireExplicitConsent = true);
            h.LoginAs("player-a");
            Assert.AreEqual(0, PlayerPrefs.GetInt(FirstLaunchPref, 0), "Nothing recorded before consent");

            h.Client.Analytics.SetConsent(true);
            h.Client.Analytics.SetConsent(false);
            h.Client.Analytics.SetConsent(true);
            h.Run(() => h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush(h);

            CollectionAssert.AreEquivalent(new[] { "app_open", "first_launch" }, Sent(transport).Select(e => (string)e["event_name"]).ToArray(),
                "Recorded when consent is given, once a launch however often it is given");
        }

        [Test]
        public void WithNoEventQueueTheLaunchEventsWaitForTheSignIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport, config => config.AnalyticsConfig.CacheFailedEvents = false);
            Assert.AreEqual(0, transport.CountTo(FlockEndpoints.AnalyticsEvents), "Nobody to send them for yet");
            Assert.AreEqual(0, PlayerPrefs.GetInt(FirstLaunchPref, 0), "first_launch is marked only once it went out");

            h.LoginAs("player-a");
            h.Run(() => h.Client.Analytics.InitializeAsync(CancellationToken.None));

            List<JObject> sent = Sent(transport);
            CollectionAssert.AreEquivalent(new[] { "app_open", "first_launch" }, sent.Select(e => (string)e["event_name"]).ToArray());
            Assert.IsTrue(sent.All(e => (string)e["player_id"] == "player-a"));
            Assert.AreEqual(1, PlayerPrefs.GetInt(FirstLaunchPref, 0));
        }

        [Test]
        public void ASessionRecoveredAfterAnUpdateCarriesTheVersionsItRanOn()
        {
            FlockLaunchFolder ended = FlockLaunchFolder.Create(FlockTestGame.LaunchesFolderIn(_folder));
            File.WriteAllText(Path.Combine(ended.Path, FlockAnalyticsLaunches.SessionStateFileName),
                "{\"session_id\":\"s-old\",\"player_id\":\"player-a\",\"session_number\":7,\"start_time_utc\":\"2026-11-12T10:00:00Z\"," +
                "\"duration_seconds\":40,\"is_active\":true,\"device_info\":{\"platform\":\"Android\",\"app_version\":\"0.9.0\",\"sdk_version\":\"1.90.0\"}}");
            ended.Dispose();
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport);
            h.LoginAs("player-a");
            h.Run(() => h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush(h);

            JObject properties = (JObject)Sent(transport).Single(e => (string)e["event_name"] == "session_end")["properties"];
            Assert.AreEqual("unexpected_exit", (string)properties["reason"]);
            Assert.AreEqual("0.9.0", (string)properties["flock_app_version"], "The build the session ran on, not this launch's");
            Assert.AreEqual("1.90.0", (string)properties["flock_sdk_version"]);
            Assert.AreEqual("Android", (string)properties["flock_platform"]);
            Assert.AreEqual(7, (int)properties["flock_session_number"]);
        }

        [Test]
        public void SendDefaultEventsOffRecordsNoneOfTheSdksEventsButKeepsTheParameters()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport, config =>
            {
                config.AnalyticsConfig.SendDefaultEvents = false;
                config.AnalyticsConfig.AutoStartSession = true;
            });
            h.LoginAs("player-a");
            h.Run(() => h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Assert.IsTrue(h.Client.Analytics.TrackEvent("level_start"));
            h.Run(() => h.Client.Analytics.EndSessionAsync());
            Flush(h);

            List<JObject> sent = Sent(transport);
            CollectionAssert.AreEqual(new[] { "level_start" }, sent.Select(e => (string)e["event_name"]).ToArray());
            Assert.AreEqual(FlockSdkVersion.Current, (string)sent[0]["properties"]["flock_sdk_version"]);
            Assert.AreEqual(1, transport.AllTo(FlockEndpoints.AnalyticsSessions).Count(r => r.Method == "PATCH"), "The session itself still reaches the server");
        }

        [TestCase("app_open")]
        [TestCase("first_launch")]
        [TestCase("store_purchase")]
        [TestCase("session_end")]
        public void TheSdksOwnEventNamesAreRefusedToTheGame(string name)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient h = Start(transport, config => config.AnalyticsConfig.SendDefaultEvents = false);
            h.LoginAs("player-a");

            Assert.IsFalse(h.Client.Analytics.TrackEvent(name));
            Assert.IsTrue(h.Logger.Warnings.Exists(line => line.Contains("'" + name + "' is reserved")), string.Join(" | ", h.Logger.Warnings));
        }
    }
}
