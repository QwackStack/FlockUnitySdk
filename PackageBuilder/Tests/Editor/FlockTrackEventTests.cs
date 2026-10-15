using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Exceptions;
using Flock.Http;
using Flock.Logging;
using Flock.Models;
using Flock.Interfaces;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    /// <summary>The public gameplay-events call, and the diagnostics calls kept apart from it: what each sends, where, and what it refuses.</summary>
    public class FlockTrackEventTests
    {
        private const string ConsentGrantedKey = "flock_analytics_consent";
        private const string ConsentSetKey = "flock_analytics_consent_set";

        private string _runFolder;
        private string _folder;
        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            // A folder of this test's own: the run-wide one holds queues earlier tests' launches left.
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_track_event_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            PlayerPrefs.DeleteKey(ConsentGrantedKey);
            PlayerPrefs.DeleteKey(ConsentSetKey);
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private FlockFakeTransport Start(Action<Flock.Config.FlockInitConfig> tweak = null)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(transport, tweak);
            _h.SetReachable(true);
            return transport;
        }

        private void Flush() => _h.Run(() => _h.Client.Analytics.FlushAsync());

        // Every analytics event the transport received, in order, across batches.
        private static List<JObject> SentEvents(FlockFakeTransport transport) =>
            transport.AllTo(FlockEndpoints.AnalyticsEvents)
                .SelectMany(r => (JArray)JObject.Parse(r.JsonBody)["events"])
                .Cast<JObject>()
                .ToList();

        private static List<JObject> SentLogEvents(FlockFakeTransport transport) =>
            transport.AllTo(FlockEndpoints.LogEvent)
                .SelectMany(r => (JArray)JObject.Parse(r.JsonBody)["events"])
                .Cast<JObject>()
                .ToList();

        [Test]
        public void ATrackedEventIsSentWithEachPropertysTypeKept()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "level", 3 }, { "big", 9007199254740993L }, { "time", 2.5 }, { "won", true }, { "none", null },
                { "items", new List<object> { 1, "a", false } }, { "nested", new Dictionary<string, object> { { "depth", 1 } } }
            };

            Assert.IsTrue(_h.Client.Analytics.TrackEvent("level_complete", properties, "progress"));
            properties["level"] = 99;
            Flush();

            JObject sent = SentEvents(transport).Single();
            Assert.AreEqual("level_complete", (string)sent["event_name"]);
            Assert.AreEqual("progress", (string)sent["event_category"]);
            Assert.AreEqual("player-a", (string)sent["player_id"]);
            JObject sentProperties = (JObject)sent["properties"];
            Assert.AreEqual(JTokenType.Integer, sentProperties["level"].Type);
            Assert.AreEqual(3, (int)sentProperties["level"], "What the game had when it called, not what it changed afterwards");
            Assert.AreEqual(9007199254740993L, (long)sentProperties["big"], "A 64-bit value exactly");
            Assert.AreEqual(JTokenType.Float, sentProperties["time"].Type);
            Assert.AreEqual(JTokenType.Boolean, sentProperties["won"].Type);
            Assert.AreEqual(JTokenType.Null, sentProperties["none"].Type);
            Assert.AreEqual(JTokenType.Array, sentProperties["items"].Type);
            Assert.AreEqual(1, (int)sentProperties["nested"]["depth"]);
        }

        [Test]
        public void TheLongestNameAndCategoryTheServerStoresAreSentAndOneCharacterMoreIsRefused()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");

            Assert.IsTrue(_h.Client.Analytics.TrackEvent(new string('n', FlockAnalyticsProvider.MaxEventNameLength)));
            Assert.IsFalse(_h.Client.Analytics.TrackEvent(new string('m', FlockAnalyticsProvider.MaxEventNameLength + 1)));
            Assert.IsTrue(_h.Client.Analytics.TrackEvent("category_fits", null, new string('c', FlockAnalyticsProvider.MaxEventCategoryLength)));
            Assert.IsFalse(_h.Client.Analytics.TrackEvent("category_too_long", null, new string('c', FlockAnalyticsProvider.MaxEventCategoryLength + 1)));
            Flush();

            CollectionAssert.AreEqual(new[] { new string('n', 200), "category_fits" }, SentEvents(transport).Select(e => (string)e["event_name"]).ToArray());
            Assert.AreEqual(2, _h.Logger.Warnings.Count(line => line.Contains("Track event refused") && line.Contains("stores at most 200 and 100")),
                string.Join(" | ", _h.Logger.Warnings));
        }

        [Test]
        public void AnEventWithNoNameIsRefused()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");

            foreach (string name in new[] { null, "", "   " })
                Assert.IsFalse(_h.Client.Analytics.TrackEvent(name), "'" + name + "'");
            Flush();

            Assert.AreEqual(0, SentEvents(transport).Count);
        }

        [Test]
        public void TheEventTheServerRecordsWhenASessionStartsIsRefusedAndOnlyThatName()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");

            Assert.IsFalse(_h.Client.Analytics.TrackEvent(FlockAnalyticsProvider.ReservedSessionStartedEvent));
            Assert.IsTrue(_h.Client.Analytics.TrackEvent("Session_Started"), "The server matches the name exactly");
            Flush();

            CollectionAssert.AreEqual(new[] { "Session_Started" }, SentEvents(transport).Select(e => (string)e["event_name"]).ToArray());
        }

        [Test]
        public void PropertiesThatCannotBeWrittenAsJsonAreRefusedWithoutThrowing()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            Dictionary<string, object> loop = new Dictionary<string, object>();
            loop["self"] = loop;

            bool recorded = true;
            Assert.DoesNotThrow(() => recorded = _h.Client.Analytics.TrackEvent("loops", loop));
            Assert.IsFalse(recorded);
            Flush();
            Assert.AreEqual(0, SentEvents(transport).Count);
        }

        [Test]
        public void WithoutConsentNothingIsQueuedOrSent()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            _h.Client.Analytics.SetConsent(false);

            Assert.IsFalse(_h.Client.Analytics.TrackEvent("no_consent"));
            _h.Client.Analytics.SetConsent(true);
            Flush();

            Assert.AreEqual(0, SentEvents(transport).Count, "Refused, not held until consent returned");
        }

        [Test]
        public void AnEventRecordedWhileSignedOutWaitsForTheNextSignInAndIsCreditedToThatPlayer()
        {
            FlockFakeTransport transport = Start();

            Assert.IsTrue(_h.Client.Analytics.TrackEvent("before_sign_in"), "Held, not refused");
            Flush();
            Assert.AreEqual(0, transport.CountTo(FlockEndpoints.AnalyticsEvents), "Nothing is sent while nobody is signed in: the server refuses a player it does not know");

            _h.LoginAs("player-b");
            _h.Run(() => _h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush();

            JObject sent = SentEvents(transport).Single();
            Assert.AreEqual("before_sign_in", (string)sent["event_name"]);
            Assert.AreEqual("player-b", (string)sent["player_id"]);
        }

        [Test]
        public void WithTheEventQueueOffAnEventIsSentAtOnceWhileSignedInAndDroppedWhileSignedOut()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.CacheFailedEvents = false);

            Assert.IsFalse(_h.Client.Analytics.TrackEvent("no_queue_signed_out"), "Nowhere to hold it, and the server would refuse it");
            _h.LoginAs("player-a");
            Assert.IsTrue(_h.Client.Analytics.TrackEvent("no_queue_signed_in"));

            List<JObject> sent = SentEvents(transport);
            CollectionAssert.AreEqual(new[] { "no_queue_signed_in" }, sent.Select(e => (string)e["event_name"]).ToArray());
            Assert.AreEqual("player-a", (string)sent[0]["player_id"]);
        }

        [Test]
        public void AnEventTheQueueCouldNotSaveIsDroppedLoudlyWhileSignedOutAndSentWhileSignedIn()
        {
            FlockFakeTransport transport = Start();
            // A file where this launch's event queue folder should be: the queue is on, and every write fails.
            string queue = Path.Combine(_h.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName);
            if (Directory.Exists(queue))
                Directory.Delete(queue, true);
            File.WriteAllText(queue, "blocked");

            Assert.IsFalse(_h.Client.Analytics.TrackEvent("unsaved_signed_out"));
            Assert.IsTrue(_h.Logger.Warnings.Exists(line => line.Contains("unsaved_signed_out") && line.Contains("could not save")),
                "The queue was on and failed, which the developer needs to see: " + string.Join(" | ", _h.Logger.Warnings));

            _h.LoginAs("player-a");
            Assert.IsTrue(_h.Client.Analytics.TrackEvent("unsaved_signed_in"));
            CollectionAssert.AreEqual(new[] { "unsaved_signed_in" }, SentEvents(transport).Select(e => (string)e["event_name"]).ToArray());
        }

        [Test]
        public void AnEventSentWithoutTheQueueIsSentOnceEvenWhenTheServerFails()
        {
            FlockFakeTransport transport = Start(config =>
            {
                config.AnalyticsConfig.CacheFailedEvents = false;
                config.RetryPolicy = new RetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero };
            });
            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Status(503, "{}"));
            _h.LoginAs("player-a");

            Assert.IsTrue(_h.Client.Analytics.TrackEvent("sent_once"));

            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.AnalyticsEvents), "A failure that may already be stored is not sent again");
        }

        [Test]
        public void AQueuedEventTheServerFailsIsRetriedByTheSamePolicy()
        {
            // The control for the test above: this policy does retry, so a single send there is the rule, not the policy.
            FlockFakeTransport transport = Start(config => config.RetryPolicy = new RetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero });
            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Status(503, "{}"));
            _h.LoginAs("player-a");

            _h.Client.Analytics.TrackEvent("retried");
            Flush();

            Assert.AreEqual(3, transport.CountTo(FlockEndpoints.AnalyticsEvents));
        }

        [Test]
        public void AnEventRecordedOnAnotherThreadIsQueued()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");

            bool recorded = Task.Run(() => _h.Client.Analytics.TrackEvent("from_a_worker")).Result;
            Flush();

            Assert.IsTrue(recorded);
            CollectionAssert.AreEqual(new[] { "from_a_worker" }, SentEvents(transport).Select(e => (string)e["event_name"]).ToArray());
        }

        [Test]
        public void EachCallWritesToItsOwnSurface()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");

            _h.Client.Analytics.TrackEvent("gameplay_only");
            _h.Client.Analytics.LogDiagnosticEvent("diagnostic_message");
            _h.Client.Analytics.LogDiagnosticError("diagnostic_error");
            _h.Client.Analytics.LogDiagnosticException("diagnostic_exception", "at Game.Run()");
            _h.Client.Analytics.LogDiagnosticException(new InvalidOperationException("diagnostic_exception_object"));
            Flush();

            CollectionAssert.AreEqual(new[] { "gameplay_only" }, SentEvents(transport).Select(e => (string)e["event_name"]).ToArray());
            Dictionary<string, string> typeByMessage = SentLogEvents(transport).ToDictionary(e => (string)e["message"], e => (string)e["data"]["type"]);
            Assert.AreEqual("debug", typeByMessage["diagnostic_message"]);
            Assert.AreEqual("logic_error", typeByMessage["diagnostic_error"]);
            Assert.AreEqual("exception", typeByMessage["diagnostic_exception"]);
            Assert.AreEqual("exception", typeByMessage["diagnostic_exception_object"]);
            Assert.AreEqual(4, typeByMessage.Count, "Nothing gameplay under Diagnostics");
        }

        [Test]
        public void TheFormerDiagnosticsNamesStillWriteToDiagnostics()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");

#pragma warning disable CS0618
            _h.Client.Analytics.LogEvent("old_event");
            _h.Client.Analytics.LogError("old_error");
            _h.Client.Analytics.LogException("old_exception", "at Game.Run()");
            _h.Client.Analytics.LogException(new InvalidOperationException("old_exception_object"));
#pragma warning restore CS0618
            Flush();

            Dictionary<string, string> typeByMessage = SentLogEvents(transport).ToDictionary(e => (string)e["message"], e => (string)e["data"]["type"]);
            Assert.AreEqual("debug", typeByMessage["old_event"]);
            Assert.AreEqual("logic_error", typeByMessage["old_error"]);
            Assert.AreEqual("exception", typeByMessage["old_exception"]);
            Assert.AreEqual("exception", typeByMessage["old_exception_object"]);
            Assert.AreEqual(0, transport.CountTo(FlockEndpoints.AnalyticsEvents));
        }

        [Test]
        public void EveryDiagnosticsCallIsNamedForItsSurface()
        {
            List<MethodInfo> logCalls = typeof(IAnalyticProvider).GetMethods().Where(m => m.Name.StartsWith("Log", StringComparison.Ordinal)).ToList();
            List<MethodInfo> current = logCalls.Where(m => m.GetCustomAttribute<ObsoleteAttribute>() == null).ToList();
            List<MethodInfo> former = logCalls.Where(m => m.GetCustomAttribute<ObsoleteAttribute>() != null).ToList();

            Assert.IsNotEmpty(current);
            foreach (MethodInfo method in current)
                Assert.IsTrue(method.Name.StartsWith("LogDiagnostic", StringComparison.Ordinal), method.Name + " writes to Diagnostics under a name that does not say so");
            foreach (MethodInfo method in former)
            {
                string replacement = "LogDiagnostic" + method.Name.Substring("Log".Length);
                Type[] parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
                Assert.IsNotNull(typeof(IAnalyticProvider).GetMethod(replacement, parameters), method.Name + " has no " + replacement + " with the same parameters");
                StringAssert.Contains(replacement, method.GetCustomAttribute<ObsoleteAttribute>().Message);
            }
            Assert.IsNull(typeof(IAnalyticProvider).GetMethod("TrackEvent").GetCustomAttribute<ObsoleteAttribute>());
        }

        [Test]
        public void ACrashAnEndedLaunchLeftIsReportedUnderDiagnosticsNotAsAGameplayEvent()
        {
            FlockLaunchFolder ended = FlockLaunchFolder.Create(Path.Combine(_folder, FlockAnalyticsLaunches.LaunchesFolderName));
            File.WriteAllText(Path.Combine(ended.Path, FlockAnalyticsLaunches.TerminationMarkerFileName),
                "{\"session_id\":\"s-ended\",\"last_state\":\"foreground\",\"last_alive_utc\":\"2026-09-27T10:05:00Z\",\"exception_count\":2}");
            ended.Dispose();

            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            _h.Run(() => _h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush();

            JObject report = SentLogEvents(transport).Single(e => (string)e["message"] == "app_termination");
            Assert.AreEqual("debug", (string)report["data"]["type"], "A record about a crash, not an exception itself");
            JObject extra = (JObject)report["data"]["extra_data"];
            Assert.AreEqual("s-ended", (string)extra["previous_session_id"]);
            Assert.AreEqual("abnormal", (string)extra["classification"]);
            Assert.AreEqual(2, (int)extra["unhandled_exception_count"]);
            Assert.IsFalse(SentEvents(transport).Any(e => (string)e["event_name"] == "app_termination"), "Not on the Game Metrics dashboards");
            Assert.IsFalse(File.Exists(Path.Combine(ended.Path, FlockAnalyticsLaunches.TerminationMarkerFileName)), "Reported once");
        }

        // ---- Diagnostics with no queue, data JSON cannot hold, the termination report, transactions, the version ----

        [Test]
        public void WithTheQueueOffADiagnosticEntryIsSentAtOnce()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.CacheFailedEvents = false);

            _h.Client.Analytics.LogDiagnosticEvent("no_queue_diagnostic");

            CollectionAssert.Contains(SentLogEvents(transport).Select(e => (string)e["message"]).ToArray(), "no_queue_diagnostic",
                "With nothing to queue it, the entry goes at once rather than being dropped");
            Assert.IsFalse(_h.Logger.Debugs.Exists(line => line == "Log event queued"), "It was not queued, so the log does not say so");
        }

        private sealed class CannotBeWritten
        {
            public CannotBeWritten Itself => this;
        }

        [Test]
        public void ADiagnosticEntryWhoseDataJsonCannotHoldIsSentWithoutIt()
        {
            FlockFakeTransport transport = Start();

            Assert.DoesNotThrow(() => _h.Client.Analytics.LogDiagnosticEvent("odd_data",
                new Dictionary<string, object> { { "loop", new CannotBeWritten() } }));
            Flush();

            JObject sent = SentLogEvents(transport).Single(e => (string)e["message"] == "odd_data");
            Assert.IsTrue(sent["data"]["extra_data"] == null || sent["data"]["extra_data"].Type == JTokenType.Null, "Its data was left out");
            Assert.IsTrue(_h.Logger.Warnings.Exists(line => line.Contains("odd_data") && line.Contains("left out")), "And it says so");
        }

        [Test]
        public void ACrashReportNamesTheEndedLaunchsVersionsAndTheServersSessionId()
        {
            FlockLaunchFolder ended = FlockLaunchFolder.Create(Path.Combine(_folder, FlockAnalyticsLaunches.LaunchesFolderName));
            File.WriteAllText(Path.Combine(ended.Path, FlockAnalyticsLaunches.TerminationMarkerFileName),
                "{\"session_id\":\"s-local\",\"server_session_id\":\"srv-ended\",\"last_state\":\"foreground\",\"last_alive_utc\":\"2026-09-27T10:05:00Z\"," +
                "\"exception_count\":0,\"app_version\":\"0.9.1\",\"sdk_version\":\"1.70.0\"}");
            ended.Dispose();

            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            _h.Run(() => _h.Client.Analytics.InitializeAsync(CancellationToken.None));
            Flush();

            JObject extra = (JObject)SentLogEvents(transport).Single(e => (string)e["message"] == "app_termination")["data"]["extra_data"];
            Assert.AreEqual("srv-ended", (string)extra["previous_session_id"], "The id the server knows the session by");
            Assert.AreEqual("0.9.1", (string)extra["app_version"], "The ended launch's version, not this one's");
            Assert.AreEqual("1.70.0", (string)extra["sdk_version"]);
        }

        [Test]
        public void TheCrashMarkerHoldsTheLaunchsVersionsAndTheServersSessionId()
        {
            string marker = Path.Combine(_folder, "marker.json");
            Directory.CreateDirectory(_folder);
            FlockTerminationTracker tracker = new FlockTerminationTracker(new NullFlockLogger(), true, marker);
            tracker.BeginTracking();
            tracker.NoteSessionStarted("s-local");
            tracker.NoteSessionRegistered("srv-1");
            FlockTerminationMarker read = FlockTerminationTracker.ReadMarker(marker, new NullFlockLogger());
            tracker.StopTracking();

            Assert.AreEqual("srv-1", read.ServerSessionId);
            Assert.AreEqual(Application.version, read.AppVersion);
            Assert.AreEqual(FlockSdkVersion.Current, read.SdkVersion);
        }

        [Test]
        public void ATransactionTheServerMayHoldIsNeverSentAgain()
        {
            FlockFakeTransport transport = Start(config => config.RetryPolicy = new RetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero });
            _h.LoginAs("player-a");
            transport.On(FlockEndpoints.AnalyticsTransactions, FlockFakeTransport.Status(500, "{}"));
            Assert.Catch<Exception>(() => _h.Run(() => _h.Client.Analytics.RecordTransactionAsync(new AnalyticsTransactionRequest { Amount = 5 })));
            Assert.AreEqual(1, transport.CountTo(FlockEndpoints.AnalyticsTransactions), "A 500 may already be stored, so it is not sent again");

            transport.On(FlockEndpoints.AnalyticsTransactions, FlockFakeTransport.Status(429, "{}"));
            Assert.Catch<Exception>(() => _h.Run(() => _h.Client.Analytics.RecordTransactionAsync(new AnalyticsTransactionRequest { Amount = 5 })));
            Assert.AreEqual(1 + 3, transport.CountTo(FlockEndpoints.AnalyticsTransactions), "A 429 provably was not, so it is retried");
        }

        [Test]
        public void ALogEventCarriesTheGameVersionsId()
        {
            FlockFakeTransport transport = Start();
            _h.Client.Analytics.LogDiagnosticEvent("versioned");
            Flush();

            JObject sent = SentLogEvents(transport).Single(e => (string)e["message"] == "versioned");
            Assert.AreEqual(_h.Client.GameVersionId, (string)sent["data"]["game_version"], "The ID, as the request header carries");
        }

        [Test]
        public void ALaunchThatHoldsNoFolderKeepsNothingOnDisk()
        {
            Directory.CreateDirectory(_folder);
            // A file where the launches folder goes: no launch folder or lock can be made under it.
            File.WriteAllText(Path.Combine(_folder, FlockAnalyticsLaunches.LaunchesFolderName), "not a folder");
            FlockAnalyticsLaunches launch = FlockAnalyticsLaunches.Start(_folder, null);
            try
            {
                Assert.IsFalse(launch.IsHoldingItsFolder, "Precondition: the launch holds no folder");
                Assert.IsNull(launch.Folder);
                Assert.IsNull(launch.SessionStatePath);
                Assert.IsNull(launch.TerminationMarkerPath);
            }
            finally
            {
                launch.Dispose();
            }
        }

        // ---- Delivery: one player a batch, refused content sent apart, a longer wait after answered failures ----

        [Test]
        public void TwoPlayersEventsAreNeverSentInOneBatch()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            _h.Client.Analytics.TrackEvent("a_first");
            _h.LoginAs("player-b");
            _h.Client.Analytics.TrackEvent("b_first");
            Flush();

            foreach (FlockHttpRequest request in transport.AllTo(FlockEndpoints.AnalyticsEvents))
            {
                string[] players = ((JArray)JObject.Parse(request.JsonBody)["events"]).Select(e => (string)e["player_id"]).Distinct().ToArray();
                Assert.AreEqual(1, players.Length, "One player a batch: " + request.JsonBody);
            }
            CollectionAssert.AreEquivalent(new[] { "a_first", "b_first" }, SentEvents(transport).Select(e => (string)e["event_name"]).ToArray());
        }

        [Test]
        public void ABatchRefusedForWhatItHoldsIsSentOneEntryAtATime()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            // The server refuses any body holding the bad entry, as it does for one field it will not take.
            transport.Default(request => request.Url.Contains(FlockEndpoints.AnalyticsEvents) && request.JsonBody.Contains("\"bad\"")
                ? FlockFakeTransport.Status(422, "{\"detail\":[{\"loc\":[\"body\"],\"msg\":\"bad\"}]}")
                : FlockFakeTransport.Ok("{}"));
            _h.Client.Analytics.TrackEvent("good_one");
            _h.Client.Analytics.TrackEvent("bad");
            _h.Client.Analytics.TrackEvent("good_two");
            Flush();

            string[] delivered = transport.AllTo(FlockEndpoints.AnalyticsEvents)
                .Where(r => !r.JsonBody.Contains("\"bad\""))
                .SelectMany(r => ((JArray)JObject.Parse(r.JsonBody)["events"]).Select(e => (string)e["event_name"]))
                .ToArray();
            CollectionAssert.AreEquivalent(new[] { "good_one", "good_two" }, delivered, "The good entries are delivered");
            Assert.AreEqual(0, ((FlockAnalyticsProvider)_h.Client.Analytics).PendingAnalyticsEventCountForTesting, "And the bad one is dropped, not kept for ever");
        }

        [Test]
        public void ThePeriodicFlushWaitsLongerAfterAnAnsweredFailureAndNotAfterAnUnansweredOne()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.EventBufferFlushIntervalSeconds = 10f);
            _h.LoginAs("player-a");
            FlockAnalyticsProvider analytics = (FlockAnalyticsProvider)_h.Client.Analytics;
            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Status(503, "{}"));
            _h.Client.Analytics.TrackEvent("waiting");

            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            Assert.AreEqual(20.0, analytics.FlushWaitSecondsForTesting, 0.001, "An answered failure doubles the wait");
            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            Assert.AreEqual(40.0, analytics.FlushWaitSecondsForTesting, 0.001, "And again");

            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Offline());
            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            Assert.AreEqual(40.0, analytics.FlushWaitSecondsForTesting, 0.001, "An unanswered one spent nothing, so the wait stays");

            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Ok("{}"));
            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            Assert.AreEqual(0.0, analytics.FlushWaitSecondsForTesting, 0.001, "A delivery brings it back");
        }

        [Test]
        public void ThePeriodicFlushWaitDoesNotGrowForAQueueThePassDidNotDrain()
        {
            FlockFakeTransport transport = Start(config => config.AnalyticsConfig.EventBufferFlushIntervalSeconds = 10f);
            _h.LoginAs("player-a");
            FlockAnalyticsProvider analytics = (FlockAnalyticsProvider)_h.Client.Analytics;
            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Status(503, "{}"));
            _h.Client.Analytics.TrackEvent("waiting");
            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            Assert.AreEqual(20.0, analytics.FlushWaitSecondsForTesting, 0.001, "Precondition: an answered failure doubled the wait");

            // Signed out, the events queue is not drained: its old ending says nothing about the server now.
            _h.Client.Authentication.Logout();
            Assert.IsTrue(analytics.PendingAnalyticsEventCountForTesting > 0, "Precondition: the event is still queued");
            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            _h.Run(() => analytics.FlushOnTheIntervalAsync(0));
            Assert.AreEqual(20.0, analytics.FlushWaitSecondsForTesting, 0.001, "A pass that drained nothing leaves the wait as it was");
        }

        [Test]
        public void ASignInFailureIsNotTheServerStrugglingSoTheWaitStays()
        {
            string folder = Path.Combine(Path.GetTempPath(), "FlockEventCacheTests", Guid.NewGuid().ToString("N"));
            try
            {
                FlockEventCache<LogEventRequest> cache = new FlockEventCache<LogEventRequest>(folder, "log_events", 100, 10, null);
                cache.Enqueue(new LogEventRequest { Message = "waiting" });

                Task Refuse(IReadOnlyList<LogEventRequest> batch, CancellationToken token) => throw new FlockAuthException("signed out") { StatusCode = 401 };
                cache.FlushAsync(Refuse, CancellationToken.None, false).GetAwaiter().GetResult();
                Assert.AreEqual(FlockDrainEnding.StoppedUnanswered, cache.LastDrainEnding, "A 401 is about the sign-in, not the server");

                Task Struggle(IReadOnlyList<LogEventRequest> batch, CancellationToken token) => throw new FlockNetworkException("down", 503);
                cache.FlushAsync(Struggle, CancellationToken.None, false).GetAwaiter().GetResult();
                Assert.AreEqual(FlockDrainEnding.StoppedAnswered, cache.LastDrainEnding, "Control: a 503 the server answered is");
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Test]
        public void FlushAsyncReportsWhatWasSentAndWhatTheServerRefused()
        {
            FlockFakeTransport transport = Start();
            _h.LoginAs("player-a");
            transport.Default(request => request.Url.Contains(FlockEndpoints.AnalyticsEvents) && request.JsonBody.Contains("\"bad\"")
                ? FlockFakeTransport.Status(422, "{\"detail\":[{\"loc\":[\"body\"],\"msg\":\"bad\"}]}")
                : FlockFakeTransport.Ok("{}"));
            _h.Client.Analytics.TrackEvent("good_one");
            _h.Client.Analytics.TrackEvent("bad");
            _h.Client.Analytics.TrackEvent("good_two");

            FlockFlushResult flushed = _h.Run(() => _h.Client.Analytics.FlushAsync());
            Assert.AreEqual(2, flushed.Sent, "The two good entries were taken");
            Assert.AreEqual(1, flushed.Refused, "and the bad one refused");
            Assert.IsFalse(flushed.NothingLost);
            Assert.IsNotNull(flushed.FirstRefusal, "with the refusal's own words");

            FlockFlushResult again = _h.Run(() => _h.Client.Analytics.FlushAsync());
            Assert.AreEqual(0, again.Sent, "Nothing left to send");
            Assert.AreEqual(0, again.Refused);
            Assert.IsTrue(again.NothingLost);
            Assert.IsNull(again.FirstRefusal);

            // A batch taken whole counts every entry in it.
            _h.Client.Analytics.TrackEvent("later_one");
            _h.Client.Analytics.TrackEvent("later_two");
            FlockFlushResult whole = _h.Run(() => _h.Client.Analytics.FlushAsync());
            Assert.AreEqual(2, whole.Sent, "Both went in one batch");
            Assert.IsTrue(whole.NothingLost);

            // A transient failure keeps the entry queued: in neither count.
            transport.On(FlockEndpoints.AnalyticsEvents, FlockFakeTransport.Offline());
            _h.Client.Analytics.TrackEvent("kept");
            FlockFlushResult deferred = _h.Run(() => _h.Client.Analytics.FlushAsync());
            Assert.AreEqual(0, deferred.Sent);
            Assert.AreEqual(0, deferred.Refused);
            Assert.IsTrue(deferred.NothingLost, "Nothing was lost: it waits for a later flush");
            Assert.AreEqual(1, ((FlockAnalyticsProvider)_h.Client.Analytics).PendingAnalyticsEventCountForTesting);
        }

        [Test]
        public void ABatchOfOnePlayersEventsReadsAnotherPlayersFilesOnce()
        {
            string folder = Path.Combine(Path.GetTempPath(), "FlockEventCacheTests", Guid.NewGuid().ToString("N"));
            try
            {
                // Three players, so a file skipped by one batch is met again by the next: a0, b0, c0, c1, c2, a1 queued in that order.
                // A's batch skips B's and C's files; B's batch, built next, meets C's three files again and must not parse them again.
                FlockEventCache<AnalyticsEventRequest> cache = new FlockEventCache<AnalyticsEventRequest>(folder, "analytics_events", 100, 100, null,
                    request => request.PlayerId);
                cache.Enqueue(new AnalyticsEventRequest { PlayerId = "a", EventName = "a0" });
                cache.Enqueue(new AnalyticsEventRequest { PlayerId = "b", EventName = "b0" });
                for (int i = 0; i < 3; i++)
                    cache.Enqueue(new AnalyticsEventRequest { PlayerId = "c", EventName = "c" + i });
                cache.Enqueue(new AnalyticsEventRequest { PlayerId = "a", EventName = "a1" });

                List<string> sent = new List<string>();
                Task Take(IReadOnlyList<AnalyticsEventRequest> batch, CancellationToken token)
                {
                    foreach (AnalyticsEventRequest request in batch)
                        sent.Add(request.EventName);
                    return Task.CompletedTask;
                }
                cache.FlushAsync(Take, CancellationToken.None, false).GetAwaiter().GetResult();

                CollectionAssert.AreEqual(new[] { "a0", "a1", "b0", "c0", "c1", "c2" }, sent, "One player a batch, the oldest player's batch first");
                // A's batch parses all six (five first met there); B's parses b0 alone, skipping C's three by their kept keys; C's parses its
                // three to send them: ten. Parsing a skipped file again on every batch makes thirteen.
                Assert.AreEqual(10, cache.FilesReadForTesting, "A file read once keeps its key for the rest of the drain");
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }
    }
}
