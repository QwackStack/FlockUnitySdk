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
        }

        [TearDown]
        public void TearDown()
        {
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (_settings != null)
            {
                _settings.PlaytestingEnabled = _wasEnabled;
                _settings.ProtokiteApiUrl = _oldUrl;
            }
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
    }
}
