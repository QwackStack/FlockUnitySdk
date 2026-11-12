using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Flock.Analytics;
using Flock.Http;
using Flock.Logging;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    /// <summary>The session record: the session_end event the SDK records, and what the session's end sends the server.</summary>
    public class FlockSessionEndEventTests
    {
        private IDisposable _consentAside;
        private const string ServerSessionId = "01K5SERVERSESSION00000001";

        private string _runFolder;
        private string _folder;
        private FlockTestClient _h;

        // What the fake server answers: a session registration, a session end, everything else.
        private Func<FlockHttpResponse> _register;
        private Func<FlockHttpResponse> _end;

        [SetUp]
        public void SetUp()
        {
            _consentAside = FlockTestClient.SetConsentAnswerAside();
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_session_end_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
            _register = () => FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}");
            _end = () => FlockFakeTransport.Ok("{\"ok\":true}");
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
            transport.Default(request =>
            {
                if (request.Url.Contains("/" + FlockEndpoints.AnalyticsSessions))
                    return request.Method == "POST" ? _register() : _end();
                return FlockFakeTransport.Ok("{\"ok\":true}");
            });
            _h = FlockTestClient.Create(transport, config =>
            {
                config.AnalyticsConfig.TrackFps = false;
                // A game's default: the session starts at sign-in.
                config.AnalyticsConfig.AutoStartSession = true;
                config.AnalyticsConfig.SendDefaultEvents = true;
                tweak?.Invoke(config);
            });
            _h.SetReachable(true);
            return transport;
        }

        private void SignInAndStart(string playerId)
        {
            _h.LoginAs(playerId);
            _h.Run(() => _h.Client.Analytics.InitializeAsync(CancellationToken.None));
        }

        private void Flush() => _h.Run(() => _h.Client.Analytics.FlushAsync());

        private static List<JObject> SessionEndEvents(FlockFakeTransport transport) =>
            transport.AllTo(FlockEndpoints.AnalyticsEvents)
                .SelectMany(r => (JArray)JObject.Parse(r.JsonBody)["events"])
                .Cast<JObject>()
                .Where(e => (string)e["event_name"] == "session_end")
                .ToList();

        private static List<FlockHttpRequest> Registrations(FlockFakeTransport transport) =>
            transport.AllTo(FlockEndpoints.AnalyticsSessions).Where(r => r.Method == "POST").ToList();

        private static List<FlockHttpRequest> Ends(FlockFakeTransport transport) =>
            transport.AllTo(FlockEndpoints.AnalyticsSessions).Where(r => r.Method == "PATCH").ToList();

        [Test]
        public void AnEndedSessionRecordsSessionEndWithItsReasonLengthScreensAndBounce()
        {
            // The session-end queue on disk, a game's default; the other tests run without it.
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            SignInAndStart("player-a");
            _h.Client.Analytics.RecordScreenView("menu");
            _h.Client.Analytics.RecordScreenView("level-1");
            _h.Run(() => _h.Client.Analytics.EndSessionAsync());
            Flush();

            JObject sent = SessionEndEvents(transport).Single();
            Assert.AreEqual("session", (string)sent["event_category"]);
            Assert.AreEqual("player-a", (string)sent["player_id"]);
            Assert.AreEqual(ServerSessionId, (string)sent["session_id"], "The server's id, so the event is tied to its session");
            JObject properties = (JObject)sent["properties"];
            Assert.AreEqual("ended_by_the_game", (string)properties["reason"]);
            Assert.AreEqual(JTokenType.Integer, properties["duration_seconds"].Type, "Whole seconds, as the session record keeps them");
            Assert.AreEqual(2, (int)properties["screens_viewed"]);
            Assert.AreEqual(JTokenType.Boolean, properties["is_bounce"].Type);
            Assert.IsTrue((bool)properties["is_bounce"], "A session shorter than the bounce threshold (10 s) is a bounce");
            Assert.AreEqual(_h.Client.Session.SessionNumber, (int)properties["session_number"], "The session's number on this device");
            Assert.AreEqual(JTokenType.Boolean, properties["is_first_session"].Type);
            Assert.AreEqual(0, (int)properties["pause_count"]);
            Assert.IsNull(properties["average_fps"], "No frame rate without a sample (Track FPS is off here)");
            Assert.AreEqual((int)properties["session_number"], (int)properties["flock_session_number"], "The ended session's number, not the next one's");
            Assert.AreEqual(FlockSdkVersion.Current, (string)properties["flock_sdk_version"]);
        }

        [Test]
        public void ASignOutRecordsSignOutForThePlayerWhoseSessionItWas()
        {
            FlockFakeTransport transport = Start();
            SignInAndStart("player-a");
            _h.Client.ClearTokens();
            SignInAndStart("player-b");
            Flush();

            JObject sent = SessionEndEvents(transport).Single(e => (string)e["player_id"] == "player-a");
            Assert.AreEqual("sign_out", (string)sent["properties"]["reason"]);
        }

        [Test]
        public void AnotherPlayerSigningInRecordsPlayerChanged()
        {
            FlockFakeTransport transport = Start();
            SignInAndStart("player-a");
            SignInAndStart("player-b");
            Flush();

            JObject sent = SessionEndEvents(transport).Single(e => (string)e["player_id"] == "player-a");
            Assert.AreEqual("player_changed", (string)sent["properties"]["reason"]);
        }

        [Test]
        public void WithoutConsentASessionEndRecordsNothing()
        {
            FlockFakeTransport transport = Start();
            SignInAndStart("player-a");
            Assert.IsTrue(_h.Client.HasActiveSession, "Precondition: a session runs");
            _h.Client.Analytics.SetConsent(false);
            Assert.IsFalse(_h.Client.HasActiveSession, "Revoking consent stops the session");
            Flush();

            Assert.AreEqual(0, SessionEndEvents(transport).Count + EventsQueued(), "A session stopped by revoking consent records nothing");
        }

        [Test]
        public void WithoutConsentASessionAnEndedLaunchLeftOpenRecordsNoEvent()
        {
            PlantOpenSession("s-no-consent", 30f);
            FlockFakeTransport transport = Start();
            _h.Client.Analytics.SetConsent(false);
            SignInAndStart("player-a");
            Flush();

            Assert.AreEqual(0, SessionEndEvents(transport).Count + EventsQueued(), "Nothing about the player is kept without consent");
        }

        private void BlockTheSessionEndQueue()
        {
            // A file where this launch's session-end queue folder should be: every write fails.
            string queue = Path.Combine(_h.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.SessionEndsQueueName);
            if (Directory.Exists(queue))
                Directory.Delete(queue, true);
            File.WriteAllText(queue, "blocked");
        }

        [Test]
        public void AnEndTheQueueCouldNotSaveIsRecordedOnceAndItsKeptRecordSaysSo()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            SignInAndStart("player-a");
            BlockTheSessionEndQueue();

            _h.Run(() => _h.Client.Analytics.EndSessionAsync());
            Flush();

            Assert.AreEqual("ended_by_the_game", (string)SessionEndEvents(transport).Single()["properties"]["reason"]);
            FlockSessionSnapshot kept = FlockSession.ReadOrphanedSession(
                Path.Combine(_h.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.SessionStateFileName), new NullFlockLogger());
            Assert.IsNotNull(kept, "Kept for the next launch, which ends it on the server");
            Assert.IsTrue(kept.EndEventRecorded, "So the next launch records no second event");
        }

        [Test]
        public void AnEndTheQueueCouldNotSaveIsSentBeforeTheNextSessionReplacesItsRecord()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            SignInAndStart("player-a");
            BlockTheSessionEndQueue();
            _h.Run(() => _h.Client.Analytics.EndSessionAsync());
            Assert.AreEqual(0, Ends(transport).Count, "Precondition: the end was neither queued nor sent");

            _h.Run(() => _h.Client.Analytics.StartSessionAsync());

            Assert.AreEqual(1, Ends(transport).Count(r => r.Url.Contains(ServerSessionId)), "Ended on the server before its record was replaced");
        }

        [Test]
        public void ASessionAnEndedLaunchLeftOpenWhoseEventWasRecordedIsEndedWithoutASecondEvent()
        {
            PlantOpenSession("s-recorded", 30f, endEventRecorded: true);
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            SignInAndStart("player-a");
            Flush();

            Assert.AreEqual(0, SessionEndEvents(transport).Count + EventsQueued());
            Assert.AreEqual(1, Ends(transport).Count, "The session itself is still ended on the server");
        }

        [Test]
        public void ARecoveredSessionsLengthReachesTheServerRounded()
        {
            PlantOpenSession("s-rounded", 3.6f);
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            SignInAndStart("player-a");
            Flush();

            Assert.AreEqual(4, (int)JObject.Parse(Ends(transport).Single().JsonBody)["duration_seconds"], "3.6 s is 4, not 3");
        }

        [Test]
        public void TheLiveRecordIsSavedOnceTheSessionOutlastsTheBounceThreshold()
        {
            string statePath = Path.Combine(_folder, "bounce_threshold_" + FlockAnalyticsLaunches.SessionStateFileName);
            Directory.CreateDirectory(_folder);
            FlockSession session = new FlockSession(new FlockAnalyticsConfig
            {
                PersistSessionOnDisk = true, TrackFps = false, HeartbeatIntervalSeconds = 0f, EventBufferFlushIntervalSeconds = 0f,
                BounceThresholdSeconds = 0.05f
            }, new NullFlockLogger(), statePath);
            session.Start("player-1", 1);
            Assert.IsTrue(FlockSession.ReadOrphanedSession(statePath, new NullFlockLogger(), 0.05f).IsBounce,
                "Control: the record saved at the start is a bounce");

            System.Threading.Thread.Sleep(120);
            session.HandleTick();

            Assert.IsFalse(FlockSession.ReadOrphanedSession(statePath, new NullFlockLogger(), 0.05f).IsBounce,
                "A crash after the threshold is not a bounce, though the heartbeat had not saved the record yet");
        }

        [Test]
        public void AScreenViewWithNoNameIsNotCountedAndTheDeveloperIsTold()
        {
            Start();
            SignInAndStart("player-a");
            _h.Client.Analytics.RecordScreenView(" ");
            _h.Client.Analytics.RecordScreenView("menu");

            Assert.AreEqual(1, _h.Client.Session.ScreensViewed);
            Assert.IsTrue(_h.Logger.Warnings.Exists(line => line.Contains("Screen view not counted")), string.Join(" | ", _h.Logger.Warnings));
        }

        private int EventsQueued()
        {
            string queue = Path.Combine(_h.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName);
            // Only session_end entries: app_open is queued at every start.
            return Directory.Exists(queue)
                ? Directory.GetFiles(queue, "*" + FlockEventCacheNames.Extension).Count(file => File.ReadAllText(file).Contains("\"session_end\""))
                : 0;
        }

        [Test]
        public void WithTheEventQueueOffTheEventIsSentAtOnce()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.CacheFailedEvents = false);
            SignInAndStart("player-a");
            _h.Run(() => _h.Client.Analytics.EndSessionAsync());

            Assert.AreEqual("ended_by_the_game", (string)SessionEndEvents(transport).Single()["properties"]["reason"]);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ASessionAnEndedLaunchLeftOpenIsRecordedAsAnUnexpectedExitWithItsBounceJudged(bool sessionEndQueue)
        {
            PlantOpenSession("s-short", 3f);
            PlantOpenSession("s-long", 50f);

            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = sessionEndQueue);
            SignInAndStart("player-a");
            Flush();

            List<JObject> recovered = SessionEndEvents(transport).Where(e => (string)e["properties"]["reason"] == "unexpected_exit").ToList();
            Assert.AreEqual(2, recovered.Count, "Each open session once");
            JObject shortOne = recovered.Single(e => (int)e["properties"]["duration_seconds"] == 3);
            JObject longOne = recovered.Single(e => (int)e["properties"]["duration_seconds"] == 50);
            Assert.IsTrue((bool)shortOne["properties"]["is_bounce"]);
            Assert.IsFalse((bool)longOne["properties"]["is_bounce"]);
            Assert.AreEqual("player-ended", (string)shortOne["player_id"], "The player of the ended launch");

            Assert.IsTrue(Ends(transport).Any(r => (bool)JObject.Parse(r.JsonBody)["is_bounce"]),
                "The session record itself carries the bounce, which was always false for a recovered session");
        }

        private void PlantOpenSession(string sessionId, float durationSeconds, bool endEventRecorded = false)
        {
            FlockLaunchFolder ended = FlockLaunchFolder.Create(FlockTestGame.LaunchesFolderIn(_folder));
            File.WriteAllText(Path.Combine(ended.Path, FlockAnalyticsLaunches.SessionStateFileName),
                "{\"session_id\":\"" + sessionId + "\",\"player_id\":\"player-ended\",\"start_time_utc\":\"2026-11-12T10:00:00Z\"," +
                "\"last_heartbeat_utc\":\"2026-11-12T10:01:00Z\",\"duration_seconds\":" + durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"is_active\":true" + (endEventRecorded ? ",\"end_event_recorded\":true" : "") + "}");
            ended.Dispose();
        }

        [Test]
        public void ASpooledSessionRegisteredOnceIsNotRegisteredAgainWhenItsEndFails()
        {
            _register = () => FlockFakeTransport.Offline();
            // The session-end queue on disk, which a game has by default.
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            Assert.IsNotNull(_h.Client.Analytics, "Precondition");
            SignInAndStart("player-a");
            Assert.IsNull(_h.Client.ServerSessionId, "Precondition: the session never reached the server");
            _h.SetReachable(false);
            _h.Run(() => _h.Client.Analytics.EndSessionAsync());
            int registrationsBefore = Registrations(transport).Count;

            _h.SetReachable(true);
            _register = () => FlockFakeTransport.Ok("{\"session_id\":\"" + ServerSessionId + "\"}");
            _end = () => FlockFakeTransport.Status(503, "{}");
            Flush();
            Assert.AreEqual(registrationsBefore + 1, Registrations(transport).Count, "Control: the spooled session was registered");

            _end = () => FlockFakeTransport.Ok("{\"ok\":true}");
            Flush();
            Assert.AreEqual(registrationsBefore + 1, Registrations(transport).Count, "A retry ends the session it registered, never a second one");
            Assert.IsTrue(Ends(transport).Last().Url.Contains(ServerSessionId));
        }

        [Test]
        public void TheSessionsLengthIsRoundedToWholeSeconds()
        {
            Assert.AreEqual(1, FlockSessionEndEvent.WholeSeconds(0.9f), "0.9 s is a second, not zero");
            Assert.AreEqual(0, FlockSessionEndEvent.WholeSeconds(0.4f));
            Assert.AreEqual(3, FlockSessionEndEvent.WholeSeconds(2.5f));
            Assert.AreEqual(0, FlockSessionEndEvent.WholeSeconds(-1f), "Never negative");
        }

        [Test]
        public void AScreenViewWithNoNameIsNotCounted()
        {
            FlockSession session = new FlockSession(new FlockAnalyticsConfig { PersistSessionOnDisk = false, TrackFps = false, HeartbeatIntervalSeconds = 0f, EventBufferFlushIntervalSeconds = 0f },
                new NullFlockLogger(), null);
            session.Start("player-1", 1);
            session.RecordScreenView("");
            session.RecordScreenView("   ");
            session.RecordScreenView(null);
            session.RecordScreenView("menu");
            Assert.AreEqual(1, session.ScreensViewed);
            session.End(FlockSessionEndReason.Quit);
        }

        [Test]
        public void EveryEndReasonHasItsOwnName()
        {
            string[] names = Enum.GetValues(typeof(FlockSessionEndReason)).Cast<FlockSessionEndReason>().Select(FlockSessionEndEvent.ReasonName).ToArray();
            CollectionAssert.AllItemsAreUnique(names);
            CollectionAssert.DoesNotContain(names, FlockSessionEndEvent.PlayerChanged, "Given by the player change itself");
            CollectionAssert.DoesNotContain(names, FlockSessionEndEvent.UnexpectedExit, "Given by the next launch");
        }
    }
}
