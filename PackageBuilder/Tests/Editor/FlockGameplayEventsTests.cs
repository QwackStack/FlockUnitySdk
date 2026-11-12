using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Flock.Analytics;
using Flock.Http;
using Flock.Interfaces;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    /// <summary>The typed calls for the tracking plan's gameplay events: the plan's names, categories and properties on the wire.</summary>
    public class FlockGameplayEventsTests
    {
        private IDisposable _consentAside;

        private string _runFolder;
        private string _folder;
        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            _consentAside = FlockTestClient.SetConsentAnswerAside();
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_gameplay_events_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            _consentAside?.Dispose();
            _consentAside = null;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private FlockFakeTransport Start(Action<Flock.Config.FlockInitConfig> tweak = null)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(transport, tweak);
            _h.SetReachable(true);
            _h.LoginAs("player-a");
            return transport;
        }

        private List<JObject> SentAfterFlush(FlockFakeTransport transport)
        {
            _h.Run(() => _h.Client.Analytics.FlushAsync());
            return transport.AllTo(FlockEndpoints.AnalyticsEvents)
                .SelectMany(r => (JArray)JObject.Parse(r.JsonBody)["events"])
                .Cast<JObject>()
                .ToList();
        }

        // The game's own properties: everything but the parameters the SDK adds to every event.
        private static Dictionary<string, JToken> GamesProperties(JObject sent) =>
            ((JObject)sent["properties"]).Properties()
                .Where(p => !p.Name.StartsWith("flock_", StringComparison.Ordinal))
                .ToDictionary(p => p.Name, p => p.Value);

        public class Call
        {
            public string EventName;
            public string Category;
            public Func<IAnalyticProvider, bool> Send;
            public Func<IAnalyticProvider, bool> SendNothing;
            public Dictionary<string, object> Expected;
            public override string ToString() => EventName;
        }

        // Every argument set, so each property's wire name and type is checked against the tracking plan's.
        private static IEnumerable<Call> EveryCall()
        {
            yield return new Call
            {
                EventName = "level_start", Category = "gameplay",
                Send = a => a.TrackLevelStart(level: 3, difficulty: "hard"),
                SendNothing = a => a.TrackLevelStart(),
                Expected = new Dictionary<string, object> { { "level", 3 }, { "difficulty", "hard" } }
            };
            yield return new Call
            {
                EventName = "level_complete", Category = "gameplay",
                Send = a => a.TrackLevelComplete(level: 3, score: 1200, durationSeconds: 95, stars: 2),
                SendNothing = a => a.TrackLevelComplete(),
                Expected = new Dictionary<string, object> { { "level", 3 }, { "score", 1200 }, { "duration_seconds", 95 }, { "stars", 2 } }
            };
            yield return new Call
            {
                EventName = "level_fail", Category = "gameplay",
                Send = a => a.TrackLevelFail(level: 3, reason: "timeout", durationSeconds: 40),
                SendNothing = a => a.TrackLevelFail(),
                Expected = new Dictionary<string, object> { { "level", 3 }, { "reason", "timeout" }, { "duration_seconds", 40 } }
            };
            yield return new Call
            {
                EventName = "checkpoint_start", Category = "gameplay",
                Send = a => a.TrackCheckpoint(level: 3, checkpoint: "bridge"),
                SendNothing = a => a.TrackCheckpoint(),
                Expected = new Dictionary<string, object> { { "level", 3 }, { "checkpoint", "bridge" } }
            };
            yield return new Call
            {
                EventName = "tutorial_step", Category = "ui",
                Send = a => a.TrackTutorialStep(step: 2, stepName: "jump"),
                SendNothing = a => a.TrackTutorialStep(),
                Expected = new Dictionary<string, object> { { "step", 2 }, { "step_name", "jump" } }
            };
            yield return new Call
            {
                EventName = "settings_change", Category = "ui",
                Send = a => a.TrackSettingsChange(setting: "music_volume", value: "0.5"),
                SendNothing = a => a.TrackSettingsChange(),
                Expected = new Dictionary<string, object> { { "setting", "music_volume" }, { "value", "0.5" } }
            };
            yield return new Call
            {
                EventName = "match_start", Category = "gameplay",
                Send = a => a.TrackMatchStart(mode: "ranked", map: "harbour", teamSize: 4),
                SendNothing = a => a.TrackMatchStart(),
                Expected = new Dictionary<string, object> { { "mode", "ranked" }, { "map", "harbour" }, { "team_size", 4 } }
            };
            yield return new Call
            {
                EventName = "match_complete", Category = "gameplay",
                Send = a => a.TrackMatchComplete(mode: "ranked", result: FlockMatchResult.Win, score: 30, durationSeconds: 600, kills: 7),
                SendNothing = a => a.TrackMatchComplete(),
                Expected = new Dictionary<string, object> { { "mode", "ranked" }, { "result", "win" }, { "score", 30 }, { "duration_seconds", 600 }, { "kills", 7 } }
            };
        }

        [TestCaseSource(nameof(EveryCall))]
        public void EachCallSendsThePlansNameCategoryAndProperties(Call call)
        {
            FlockFakeTransport transport = Start();

            Assert.IsTrue(call.Send(_h.Client.Analytics));

            JObject sent = SentAfterFlush(transport).Single();
            Assert.AreEqual(call.EventName, (string)sent["event_name"]);
            Assert.AreEqual(call.Category, (string)sent["event_category"]);
            Assert.AreEqual("player-a", (string)sent["player_id"]);
            Dictionary<string, JToken> properties = GamesProperties(sent);
            CollectionAssert.AreEquivalent(call.Expected.Keys, properties.Keys);
            foreach (KeyValuePair<string, object> expected in call.Expected)
            {
                JTokenType type = expected.Value is int ? JTokenType.Integer : JTokenType.String;
                Assert.AreEqual(type, properties[expected.Key].Type, expected.Key);
                Assert.AreEqual(expected.Value.ToString(), properties[expected.Key].ToString(), expected.Key);
            }
            Assert.IsNotNull(sent["properties"]["flock_sdk_version"], "The SDK adds its parameters, as to every event");
        }

        [TestCaseSource(nameof(EveryCall))]
        public void EachCallWithNoArgumentsSendsNoPropertiesOfTheGames(Call call)
        {
            FlockFakeTransport transport = Start();

            Assert.IsTrue(call.SendNothing(_h.Client.Analytics));

            JObject sent = SentAfterFlush(transport).Single();
            Assert.AreEqual(call.EventName, (string)sent["event_name"]);
            CollectionAssert.IsEmpty(GamesProperties(sent).Keys, "Nothing set, nothing sent, not nulls");
        }

        [Test]
        public void OnlyTheArgumentsSetAreSentAndZeroIsAValue()
        {
            FlockFakeTransport transport = Start();

            Assert.IsTrue(_h.Client.Analytics.TrackLevelComplete(level: 4, stars: 0));

            Dictionary<string, JToken> sent = GamesProperties(SentAfterFlush(transport).Single());
            CollectionAssert.AreEquivalent(new[] { "level", "stars" }, sent.Keys);
            Assert.AreEqual(0, (int)sent["stars"], "Zero is a value, not unset");
        }

        [Test]
        public void ANamedArgumentWinsOverTheSameExtraPropertyAndTheGamesDictionaryIsNotChanged()
        {
            FlockFakeTransport transport = Start();
            Dictionary<string, object> extra = new Dictionary<string, object>
            {
                { "kills", 1 }, { "score", 5 }, { "weapon", "bow" }
            };

            Assert.IsTrue(_h.Client.Analytics.TrackMatchComplete(result: FlockMatchResult.Draw, kills: 7, extraProperties: extra));

            Dictionary<string, JToken> sent = GamesProperties(SentAfterFlush(transport).Single());
            Assert.AreEqual(7, (int)sent["kills"], "The named argument");
            Assert.AreEqual(5, (int)sent["score"], "An extra property whose argument is unset is sent");
            Assert.AreEqual("bow", (string)sent["weapon"]);
            Assert.AreEqual("draw", (string)sent["result"]);
            CollectionAssert.AreEquivalent(new[] { "kills", "score", "weapon" }, extra.Keys);
            Assert.AreEqual(1, extra["kills"], "The game's dictionary is never changed");
        }

        [Test]
        public void WithoutConsentACallIsRefusedAndNothingIsSent()
        {
            FlockFakeTransport transport = Start();
            _h.Client.Analytics.SetConsent(false);

            Assert.IsFalse(_h.Client.Analytics.TrackLevelStart(level: 1));
            _h.Client.Analytics.SetConsent(true);

            Assert.AreEqual(0, SentAfterFlush(transport).Count(e => (string)e["event_name"] == "level_start"));
        }

        [Test]
        public void TheCallsAreSentWithSendDefaultEventsOnAndOff()
        {
            foreach (bool sendDefaultEvents in new[] { true, false })
            {
                FlockFakeTransport transport = Start(config => config.AnalyticsConfig.SendDefaultEvents = sendDefaultEvents);

                Assert.IsTrue(_h.Client.Analytics.TrackMatchStart(mode: "casual"));

                Assert.AreEqual(1, SentAfterFlush(transport).Count(e => (string)e["event_name"] == "match_start"), "Send Default Events " + sendDefaultEvents);
                _h.Dispose();
                _h = null;
            }
        }

        [Test]
        public void WithAnalyticsSwitchedOffACallReturnsFalseAndSendsNothing()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.Enabled = false);

            Assert.IsInstanceOf<NullAnalyticsProvider>(_h.Client.Analytics);
            Assert.IsFalse(_h.Client.Analytics.TrackLevelStart(level: 1));
            Assert.IsFalse(_h.Client.Analytics.TrackMatchComplete(result: FlockMatchResult.Loss));
            Assert.AreEqual(0, transport.AllTo(FlockEndpoints.AnalyticsEvents).Count());
        }

        [Test]
        public void TheMatchResultsAreThePlansWords()
        {
            Assert.AreEqual("win", FlockMatchResult.Win);
            Assert.AreEqual("loss", FlockMatchResult.Loss);
            Assert.AreEqual("draw", FlockMatchResult.Draw);
        }
    }
}
