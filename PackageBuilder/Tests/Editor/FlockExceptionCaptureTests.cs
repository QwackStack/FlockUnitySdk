using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Logging;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // Exception capture through a real Flock client: Unity's own log, other threads, faulted tasks, the repeat rule, the switch,
    // and the launch's crash marker. What was queued is read off the launch's log queue on disk.
    public class FlockExceptionCaptureTests
    {
        private IDisposable _folder;
        private bool? _projectsConsent;

        [SetUp]
        public void SetUp()
        {
            // Every test counts what its launch queued, so none may take over another's.
            _folder = FlockTestSavedFiles.UseFolderOfItsOwn();
            // The project's own consent decision would decide these tests'; it is put back afterwards.
            _projectsConsent = new FlockConsentStore().Load();
            new FlockConsentStore().Clear();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Debug.unityLogger.logEnabled = true;
            FlockAnalyticsProvider.TrackTerminationInTheEditorForTesting = false;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            if (_projectsConsent.HasValue)
                new FlockConsentStore().Save(_projectsConsent.Value);
            else
                new FlockConsentStore().Clear();
            _folder?.Dispose();
        }

        private static FlockTestClient Create(Action<FlockInitConfig> tweak = null) => FlockTestClient.Create(new FlockFakeTransport(), tweak);

        private static FlockAnalyticsProvider Provider(FlockTestClient sdk) => (FlockAnalyticsProvider)sdk.Client.Analytics;

        private static string LogQueue(FlockTestClient sdk) => Path.Combine(sdk.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.LogEventsQueueName);

        private static List<JObject> Queued(string logQueue)
            => FlockAnalyticsLaunches.QueuedEntries(logQueue).Select(path => JObject.Parse(File.ReadAllText(path))).ToList();

        private static List<JObject> QueuedExceptions(FlockTestClient sdk)
            => Queued(LogQueue(sdk)).Where(entry => (string)entry["data"]["type"] == "exception").ToList();

        private static JObject Extra(JObject entry) => (JObject)entry["data"]["extra_data"];

        // A marker naming each test's exceptions, so a stray one from elsewhere is never counted.
        private static string Named(string what) => "FlockExceptionCaptureTests " + what;

        [Test]
        public void AnExceptionBeforeSignInIsQueuedAndSentOnceAPlayerSignsIn()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.Expect(LogType.Exception, new Regex("before anyone signed in"));
                Debug.LogException(new InvalidOperationException(Named("before anyone signed in")));
                Provider(sdk).HandleLaunchTick();

                JObject queued = QueuedExceptions(sdk).Single();
                StringAssert.EndsWith(Named("before anyone signed in"), (string)queued["message"]);
                Assert.AreEqual(FlockRepeatedExceptionCounter.SourceLog, (string)Extra(queued)["exception_source"]);
                Assert.IsFalse(sdk.Transport.Sent(FlockEndpoints.LogEvent), "Nothing sends before sign-in");

                sdk.LoginAs("player-1");
                sdk.Run(() => sdk.Client.Analytics.InitializeAsync(CancellationToken.None));
                JObject sent = (JObject)JObject.Parse(sdk.Transport.LastTo(FlockEndpoints.LogEvent).JsonBody)["events"][0];
                StringAssert.EndsWith(Named("before anyone signed in"), (string)sent["message"], "Sent once a player signed in");
            }
        }

        [Test]
        public void AnExceptionLoggedOnAnotherThreadIsCaptured()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.Expect(LogType.Exception, new Regex("on a thread of the game's own"));
                Thread thread = new Thread(() => Debug.LogException(new InvalidOperationException(Named("on a thread of the game's own"))));
                thread.Start();
                thread.Join();
                Provider(sdk).HandleLaunchTick();

                StringAssert.EndsWith(Named("on a thread of the game's own"), (string)QueuedExceptions(sdk).Single()["message"]);
            }
        }

        [Test]
        public void AFaultedTaskNobodyAwaitsIsCaptured()
        {
            using (FlockTestClient sdk = Create())
            {
                // Unity may log it as the finalizer runs; the capture is what this test is about.
                LogAssert.ignoreFailingMessages = true;
                FaultATaskNobodyAwaits(Named("a task nobody awaited"));
                for (int attempt = 0; attempt < 5 && QueuedExceptions(sdk).Count == 0; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Provider(sdk).HandleLaunchTick();
                }

                JObject queued = QueuedExceptions(sdk).Single();
                Assert.AreEqual("InvalidOperationException: " + Named("a task nobody awaited"), (string)queued["message"]);
                Assert.AreEqual(FlockRepeatedExceptionCounter.SourceUnobservedTask, (string)Extra(queued)["exception_source"]);
            }
        }

        // Out of line, so nothing in the test keeps the faulted task reachable once it has finished.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FaultATaskNobodyAwaits(string message)
        {
            Task faulted = Task.Run(() => throw new InvalidOperationException(message));
            while (!faulted.IsCompleted)
                Thread.Sleep(1);
        }

        [Test]
        public void AnExceptionNoThreadCaughtIsKeptOnlyWhileUnitysLoggingIsOff()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();
            UnhandledExceptionEventArgs unhandled = new UnhandledExceptionEventArgs(new InvalidOperationException(Named("no thread caught this")), true);

            capture.HandleUnhandledException(null, unhandled);
            Assert.IsFalse(capture.TryTake(out _), "With logging on, Unity's log hands the same exception over, so this one keeps nothing");

            Debug.unityLogger.logEnabled = false;
            capture.HandleUnhandledException(null, unhandled);
            Debug.unityLogger.logEnabled = true;
            Assert.IsTrue(capture.TryTake(out FlockCapturedException kept), "With logging off, nothing else hears it");
            Assert.AreEqual("InvalidOperationException: " + Named("no thread caught this"), kept.Message);
            Assert.AreEqual(FlockRepeatedExceptionCounter.SourceUnhandled, kept.Source);
        }

        [Test]
        public void AnExceptionLoggedWithNoStackKeepsTheFramesThatLoggedIt()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();
            capture.HandleLog(Named("logged with no stack"), string.Empty, LogType.Exception);
            Assert.IsTrue(capture.TryTake(out FlockCapturedException logged));
            StringAssert.StartsWith(typeof(FlockExceptionCaptureTests).FullName + ":" + nameof(AnExceptionLoggedWithNoStackKeepsTheFramesThatLoggedIt),
                logged.StackTrace, "A player set to log exceptions with no stack still says where it was logged from, and the SDK's frames are not the game's");

            capture.HandleUnobservedTask(null, new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException(Named("never thrown")))));
            Assert.IsTrue(capture.TryTake(out FlockCapturedException fromATask));
            Assert.AreEqual(string.Empty, fromATask.StackTrace, "A finalizer's frames say nothing about a task's fault");
        }

        // A game's own exception type whose Message getter throws, as one reading a null field does.
        private sealed class MessageThrowsException : Exception
        {
            public override string Message => throw new FormatException("the Message getter threw");
        }

        private sealed class StackTraceThrowsException : Exception
        {
            public StackTraceThrowsException(string message) : base(message) { }

            public override string StackTrace => throw new FormatException("the StackTrace getter threw");
        }

        // An aggregate's own Message is read by its Flatten, so this one makes Flatten throw on every runtime.
        private sealed class MessageThrowsAggregateException : AggregateException
        {
            public MessageThrowsAggregateException(params Exception[] inner) : base(inner) { }

            public override string Message => throw new FormatException("the aggregate's Message getter threw");
        }

        private const string UnreadableMessage = "MessageThrowsException: (its message could not be read: FormatException)";

        [Test]
        public void AnExceptionWhoseMessageThrowsIsKeptUnderItsTypeNameByEveryHook()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();

            Debug.unityLogger.logEnabled = false;
            Assert.DoesNotThrow(() => capture.HandleUnhandledException(null, new UnhandledExceptionEventArgs(new MessageThrowsException(), true)),
                "Thrown from the domain's hook, it ends an IL2CPP player");
            Debug.unityLogger.logEnabled = true;
            Assert.IsTrue(capture.TryTake(out FlockCapturedException unhandled));
            Assert.AreEqual(UnreadableMessage, unhandled.Message);

            UnobservedTaskExceptionEventArgs faulted = new UnobservedTaskExceptionEventArgs(new MessageThrowsAggregateException(new MessageThrowsException()));
            Assert.DoesNotThrow(() => capture.HandleUnobservedTask(null, faulted), "Thrown on the finalizer thread, the task's fault is lost");
            Assert.IsTrue(capture.TryTake(out FlockCapturedException fromATask));
            Assert.AreEqual(UnreadableMessage, fromATask.Message);
            Assert.AreEqual(FlockRepeatedExceptionCounter.SourceUnobservedTask, fromATask.Source);
        }

        [Test]
        public void AnExceptionWhoseStackTraceThrowsIsKeptWithNoStack()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();

            Debug.unityLogger.logEnabled = false;
            Assert.DoesNotThrow(() => capture.HandleUnhandledException(null, new UnhandledExceptionEventArgs(new StackTraceThrowsException(Named("unreadable stack")), true)));
            Debug.unityLogger.logEnabled = true;
            Assert.IsTrue(capture.TryTake(out FlockCapturedException unhandled));
            Assert.AreEqual("StackTraceThrowsException: " + Named("unreadable stack"), unhandled.Message);
            Assert.AreEqual(string.Empty, unhandled.StackTrace);

            Assert.DoesNotThrow(() => capture.HandleUnobservedTask(null,
                new UnobservedTaskExceptionEventArgs(new AggregateException(new StackTraceThrowsException(Named("unreadable stack in a task"))))));
            Assert.IsTrue(capture.TryTake(out FlockCapturedException fromATask));
            Assert.AreEqual("StackTraceThrowsException: " + Named("unreadable stack in a task"), fromATask.Message);
            Assert.AreEqual(string.Empty, fromATask.StackTrace);
        }

        [Test]
        public void EveryFaultInsideNestedAggregatesIsKeptInFlattensOrder()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();
            AggregateException nested = new AggregateException(
                new InvalidOperationException(Named("first")),
                new AggregateException(new InvalidOperationException(Named("third")), new AggregateException(new InvalidOperationException(Named("fourth")))),
                new InvalidOperationException(Named("second")));

            capture.HandleUnobservedTask(null, new UnobservedTaskExceptionEventArgs(nested));
            List<string> kept = new List<string>();
            while (capture.TryTake(out FlockCapturedException captured))
                kept.Add(captured.Message);

            List<string> flattened = nested.Flatten().InnerExceptions.Select(inner => "InvalidOperationException: " + inner.Message).ToList();
            CollectionAssert.AreEqual(flattened, kept, "Each fault once, no aggregate kept as itself, in the order Flatten gives");
            Assert.AreEqual(4, kept.Count);
        }

        [Test]
        public void ALongMessageAndStackAreCutWithTheirLengthNoted()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();
            string longMessage = new string('m', FlockExceptionCapture.MostMessageCharacters + 1);
            string longStack = new string('s', FlockExceptionCapture.MostStackTraceCharacters + 1);
            capture.HandleLog(longMessage, longStack, LogType.Exception);
            Assert.IsTrue(capture.TryTake(out FlockCapturedException cut));
            Assert.AreEqual(new string('m', FlockExceptionCapture.MostMessageCharacters) + $" [cut from {longMessage.Length} characters]", cut.Message);
            Assert.AreEqual(new string('s', FlockExceptionCapture.MostStackTraceCharacters) + $" [cut from {longStack.Length} characters]", cut.StackTrace);

            string fullMessage = new string('m', FlockExceptionCapture.MostMessageCharacters);
            string fullStack = new string('s', FlockExceptionCapture.MostStackTraceCharacters);
            capture.HandleLog(fullMessage, fullStack, LogType.Exception);
            Assert.IsTrue(capture.TryTake(out FlockCapturedException whole));
            Assert.AreEqual(fullMessage, whole.Message, "Text at the limit is kept whole");
            Assert.AreEqual(fullStack, whole.StackTrace, "Text at the limit is kept whole");
        }

        [Test]
        public void ACutNeverSplitsACharacterInTwo()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();
            // The emoji's two halves sit either side of the limit.
            string message = new string('a', FlockExceptionCapture.MostMessageCharacters - 1) + "\U0001F600" + "after";
            capture.HandleLog(message, "a frame", LogType.Exception);
            Assert.IsTrue(capture.TryTake(out FlockCapturedException cut));

            string kept = cut.Message.Substring(0, cut.Message.IndexOf(" [cut from", StringComparison.Ordinal));
            Assert.AreEqual(new string('a', FlockExceptionCapture.MostMessageCharacters - 1), kept, "The whole character goes, not its first half");
        }

        [Test]
        public void AFaultedTaskWhoseMessageThrowsIsCapturedUnderItsTypeName()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.ignoreFailingMessages = true;
                FaultATaskWithAnUnreadableMessage();
                for (int attempt = 0; attempt < 5 && QueuedExceptions(sdk).Count == 0; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Provider(sdk).HandleLaunchTick();
                }

                JObject queued = QueuedExceptions(sdk).Single();
                Assert.AreEqual(UnreadableMessage, (string)queued["message"]);
                Assert.AreEqual(FlockRepeatedExceptionCounter.SourceUnobservedTask, (string)Extra(queued)["exception_source"]);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FaultATaskWithAnUnreadableMessage()
        {
            Task faulted = Task.Run(() => throw new MessageThrowsException());
            while (!faulted.IsCompleted)
                Thread.Sleep(1);
        }

        [Test]
        public void AManualReportOfAnExceptionWhoseMessageThrowsIsStillRecorded()
        {
            using (FlockTestClient sdk = Create())
            {
                Assert.DoesNotThrow(() => sdk.Client.Analytics.LogDiagnosticException(new MessageThrowsException()),
                    "A report must not throw back at the game that made it");

                JObject queued = QueuedExceptions(sdk).Single();
                StringAssert.EndsWith("(its message could not be read: FormatException)", (string)queued["message"]);
            }
        }

        [Test]
        public void AnUnhandledExceptionOnAThreadIsCapturedWhileUnitysLoggingIsOff()
        {
            using (FlockTestClient sdk = Create())
            {
                Debug.unityLogger.logEnabled = false;
                Thread thread = new Thread(() => throw new InvalidOperationException(Named("no thread caught this")));
                thread.Start();
                thread.Join();
                Debug.unityLogger.logEnabled = true;
                Provider(sdk).HandleLaunchTick();

                JObject queued = QueuedExceptions(sdk).Single();
                Assert.AreEqual("InvalidOperationException: " + Named("no thread caught this"), (string)queued["message"]);
                Assert.AreEqual(FlockRepeatedExceptionCounter.SourceUnhandled, (string)Extra(queued)["exception_source"]);
            }
        }

        [Test]
        public void TheExceptionSettingsInFlockSettingsReachTheSdk()
        {
            FlockConfigAsset asset = ScriptableObject.CreateInstance<FlockConfigAsset>();
            try
            {
                asset.apiUrl = "https://test.invalid";
                asset.gameVersionId = "test-gvid";
                Assert.IsTrue(asset.ToInitConfig().AnalyticsConfig.CaptureExceptions, "On by default");
                Assert.AreEqual(60f, asset.ToInitConfig().AnalyticsConfig.ExceptionRepeatWindowSeconds);

                asset.analyticsCaptureExceptions = false;
                asset.analyticsExceptionRepeatWindow = 5f;
                FlockAnalyticsConfig analytics = asset.ToInitConfig().AnalyticsConfig;
                Assert.IsFalse(analytics.CaptureExceptions, "A studio's switch in Flock > Settings is the one a game reads");
                Assert.AreEqual(5f, analytics.ExceptionRepeatWindowSeconds);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void AnUnhandledExceptionOnAThreadIsReportedOnceWhileUnitysLoggingIsOn()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.ignoreFailingMessages = true;
                Thread thread = new Thread(() => throw new InvalidOperationException(Named("heard by two hooks")));
                thread.Start();
                thread.Join();
                Provider(sdk).HandleLaunchTick();

                JObject queued = QueuedExceptions(sdk).Single(entry => ((string)entry["message"]).EndsWith(Named("heard by two hooks")));
                Assert.AreEqual(FlockRepeatedExceptionCounter.SourceLog, (string)Extra(queued)["exception_source"],
                    "Unity's log and the domain both hear it; only the log's is kept");
            }
        }

        [Test]
        public void MoreExceptionsThanTheLimitBeforeTheNextFrameAreCountedAsLost()
        {
            FlockExceptionCapture capture = new FlockExceptionCapture();
            for (int exception = 0; exception < FlockExceptionCapture.MostWaiting + 44; exception++)
                capture.HandleLog(Named("burst"), string.Empty, LogType.Exception);

            int taken = 0;
            while (capture.TryTake(out _))
                taken++;
            Assert.AreEqual(256, FlockExceptionCapture.MostWaiting);
            Assert.AreEqual(256, taken, "A burst costs a bounded amount of memory");
            Assert.AreEqual(44, capture.TakeLost());
            Assert.AreEqual(0, capture.TakeLost(), "Taken, and started again");
        }

        [Test]
        public void AnExceptionSeenWithoutConsentIsReportedWhenItHappensAgainWithConsent()
        {
            using (FlockTestClient sdk = Create(config => config.AnalyticsConfig.RequireExplicitConsent = true))
            {
                LogAssert.ignoreFailingMessages = true;
                // Logged from one line both times, so the two are the same fault.
                for (int time = 0; time < 2; time++)
                {
                    if (time == 1)
                        sdk.Client.Analytics.SetConsent(true);
                    Debug.LogException(new InvalidOperationException(Named("before and after consent")));
                    Provider(sdk).ReportCapturedExceptions(10.0 + time, false);
                    if (time == 0)
                        Assert.IsEmpty(Queued(LogQueue(sdk)), "Precondition: nothing without consent");
                }
                Assert.AreEqual(1, QueuedExceptions(sdk).Count, "What was never sent is no repeat of anything");
            }
        }

        [Test]
        public void TheSdksOwnExceptionIsNotTheGames()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.Expect(LogType.Exception, new Regex("Token refresh failed"));
                new UnityFlockLogger(false).LogException(new FlockAuthException("Token refresh failed. Please log in again."));
                LogAssert.Expect(LogType.Exception, new Regex("the game's own"));
                Debug.LogException(new InvalidOperationException(Named("the game's own")));
                Provider(sdk).HandleLaunchTick();

                StringAssert.EndsWith(Named("the game's own"), (string)QueuedExceptions(sdk).Single()["message"], "Only the game's exception");
            }
        }

        [Test]
        public void AnErrorLineIsNotAnException()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.Expect(LogType.Error, new Regex("an error line"));
                Debug.LogError(Named("an error line"));
                Provider(sdk).HandleLaunchTick();

                Assert.IsEmpty(QueuedExceptions(sdk));
            }
        }

        [Test]
        public void WithCaptureSwitchedOffNothingIsCapturedButAManualReportIsStillRecorded()
        {
            using (FlockTestClient sdk = Create(config => config.AnalyticsConfig.CaptureExceptions = false))
            {
                Assert.IsFalse(Provider(sdk).IsListeningForExceptions);
                LogAssert.Expect(LogType.Exception, new Regex("with capture off"));
                Debug.LogException(new InvalidOperationException(Named("with capture off")));
                Provider(sdk).HandleLaunchTick();
                Assert.IsEmpty(QueuedExceptions(sdk));

                sdk.Client.Analytics.LogDiagnosticException(new InvalidOperationException(Named("reported by hand")));
                StringAssert.EndsWith(Named("reported by hand"), (string)QueuedExceptions(sdk).Single()["message"]);
            }
        }

        [Test]
        public void WithoutConsentNothingIsQueued()
        {
            using (FlockTestClient sdk = Create(config => config.AnalyticsConfig.RequireExplicitConsent = true))
            {
                LogAssert.Expect(LogType.Exception, new Regex("without consent"));
                Debug.LogException(new InvalidOperationException(Named("without consent")));
                Provider(sdk).HandleLaunchTick();

                Assert.IsEmpty(Queued(LogQueue(sdk)));
            }
        }

        [Test]
        public void AStormIsOneReportAndItsCountIsQueuedWhenTheSdkShutsDown()
        {
            FlockTestClient sdk = Create();
            string logQueue = LogQueue(sdk);
            LogAssert.ignoreFailingMessages = true;
            for (int frame = 0; frame < 100; frame++)
                Debug.LogException(new InvalidOperationException(Named("every frame")));
            Provider(sdk).HandleLaunchTick();
            Assert.AreEqual(1, QueuedExceptions(sdk).Count, "The first at once, the rest counted");

            // Shutting down closes the window early: no later frame would.
            sdk.Dispose();
            List<JObject> queued = Queued(logQueue).Where(entry => (string)entry["data"]["type"] == "exception").ToList();
            Assert.AreEqual(2, queued.Count);
            JObject summary = queued.Single(entry => Extra(entry)["repeat_count"] != null);
            Assert.AreEqual(JTokenType.Integer, Extra(summary)["repeat_count"].Type);
            Assert.AreEqual(99, (int)Extra(summary)["repeat_count"]);
            Assert.AreEqual(60.0, (double)Extra(summary)["repeat_window_seconds"]);
            StringAssert.EndsWith(Named("every frame"), (string)summary["message"]);
        }

        [Test]
        public void PastTheLaunchsLimitOfDifferentFaultsTheRestAreCountedOnceAMinute()
        {
            using (FlockTestClient sdk = Create())
            {
                LogAssert.ignoreFailingMessages = true;
                for (int fault = 0; fault <= FlockRepeatedExceptionCounter.MostDifferentFaultsPerLaunch; fault++)
                    Debug.LogException(new InvalidOperationException(Named("fault " + Letters(fault))));
                Provider(sdk).ReportCapturedExceptions(1000.0, false);

                Assert.AreEqual(FlockRepeatedExceptionCounter.MostDifferentFaultsPerLaunch, QueuedExceptions(sdk).Count);
                Assert.AreEqual(1, HeldBackCounts(sdk).Single(), "The 101st is counted and said so at once");

                for (int fault = 200; fault < 205; fault++)
                    Debug.LogException(new InvalidOperationException(Named("fault " + Letters(fault))));
                Provider(sdk).ReportCapturedExceptions(1030.0, false);
                Assert.AreEqual(1, HeldBackCounts(sdk).Count, "Not again within the minute");

                Provider(sdk).ReportCapturedExceptions(1060.0, false);
                CollectionAssert.AreEqual(new[] { 1, 5 }, HeldBackCounts(sdk), "A minute later, the five since");
            }
        }

        private static List<int> HeldBackCounts(FlockTestClient sdk)
            => Queued(LogQueue(sdk))
                .Where(entry => (string)entry["message"] == FlockAnalyticsProvider.ExceptionReportsHeldBackEvent)
                .Select(entry => (int)Extra(entry)["held_back_count"])
                .ToList();

        // Numbers are collapsed in a fault's key, so each fault is told apart by letters.
        private static string Letters(int number)
        {
            string letters = string.Empty;
            do
            {
                letters = (char)('a' + number % 26) + letters;
                number /= 26;
            } while (number > 0);
            return letters;
        }

        [Test]
        public void ShuttingDownTakesTheHooksOffUnity()
        {
            FlockTestClient sdk = Create();
            FlockAnalyticsProvider provider = Provider(sdk);
            Assert.IsTrue(provider.IsListeningForExceptions, "Precondition: listening from start-up");
            sdk.Dispose();
            Assert.IsFalse(provider.IsListeningForExceptions, "A stopped SDK would otherwise hear, and hold on to, every later exception");
        }

        // ---- The launch's crash marker ----

        private static FlockTestClient CreateTracking()
        {
            FlockAnalyticsProvider.TrackTerminationInTheEditorForTesting = true;
            return Create(config =>
            {
                config.AnalyticsConfig.PersistSessionOnDisk = true;
                config.AnalyticsConfig.HeartbeatIntervalSeconds = 60f;
            });
        }

        private static FlockTerminationMarker Marker(FlockTestClient sdk)
            => FlockTerminationTracker.ReadMarker(sdk.Client.AnalyticsLaunches.TerminationMarkerPath, new NullFlockLogger());

        [Test]
        public void TheCrashMarkerIsWrittenAtStartUpAndOutlivesASignOut()
        {
            using (FlockTestClient sdk = CreateTracking())
            {
                Assert.IsNotNull(Marker(sdk), "Before anyone signs in, so a crash there is reported");
                Assert.IsNull(Marker(sdk).SessionId);

                sdk.LoginAs("player-1");
                sdk.Run(() => sdk.Client.Analytics.InitializeAsync(CancellationToken.None));
                string sessionId = sdk.Run(() => sdk.Client.Analytics.StartSessionAsync());
                Assert.AreEqual(sessionId, Marker(sdk).SessionId);

                sdk.Client.ClearTokens();
                Assert.IsNotNull(Marker(sdk), "A crash after sign-out is still this launch's crash");
                Assert.IsNull(Marker(sdk).SessionId);
            }
        }

        [Test]
        public void GrantingConsentStartsTheCrashMarker()
        {
            FlockAnalyticsProvider.TrackTerminationInTheEditorForTesting = true;
            using (FlockTestClient sdk = Create(config =>
            {
                config.AnalyticsConfig.PersistSessionOnDisk = true;
                config.AnalyticsConfig.RequireExplicitConsent = true;
            }))
            {
                Assert.IsNull(Marker(sdk), "Precondition: nothing is collected before consent, a crash marker included");
                sdk.Client.Analytics.SetConsent(true);
                Assert.IsNotNull(Marker(sdk));
            }
        }

        [Test]
        public void ShuttingDownClearsTheCrashMarker()
        {
            FlockTestClient sdk = CreateTracking();
            string markerPath = sdk.Client.AnalyticsLaunches.TerminationMarkerPath;
            Assert.IsTrue(File.Exists(markerPath), "Precondition");
            sdk.Dispose();
            Assert.IsFalse(File.Exists(markerPath), "A clean end: left behind, the next Create would report a crash");
        }

        [Test]
        public void EveryCapturedExceptionCountsTowardTheCrashReport()
        {
            using (FlockTestClient sdk = CreateTracking())
            {
                LogAssert.ignoreFailingMessages = true;
                // Logged from one line twice, so the second is a repeat.
                for (int time = 0; time < 2; time++)
                    Debug.LogException(new InvalidOperationException(Named("counted")));
                Thread thread = new Thread(() => Debug.LogException(new InvalidOperationException(Named("counted on a thread"))));
                thread.Start();
                thread.Join();
                Provider(sdk).HandleLaunchTick();

                Assert.AreEqual(2, QueuedExceptions(sdk).Count, "Precondition: the second was counted as a repeat, not reported");
                Assert.AreEqual(3, Marker(sdk).ExceptionCount, "Repeats and other threads included, saved by the tick's heartbeat");
            }
        }

        [Test]
        public void ExceptionsLostToABurstStillCountTowardTheCrashReport()
        {
            using (FlockTestClient sdk = CreateTracking())
            {
                LogAssert.ignoreFailingMessages = true;
                for (int exception = 0; exception < FlockExceptionCapture.MostWaiting + 10; exception++)
                    Debug.LogException(new InvalidOperationException(Named("burst")));
                Provider(sdk).HandleLaunchTick();

                Assert.AreEqual(FlockExceptionCapture.MostWaiting + 10, Marker(sdk).ExceptionCount, "Kept or lost, each was heard");
            }
        }

        [Test]
        public void ACrashBeforeSignInIsReportedByTheNextLaunch()
        {
            FlockLaunchFolder ended = FlockLaunchFolder.Create(Path.Combine(FlockAnalyticsLaunches.FolderForTesting, FlockAnalyticsLaunches.LaunchesFolderName));
            File.WriteAllText(Path.Combine(ended.Path, FlockAnalyticsLaunches.TerminationMarkerFileName),
                "{\"session_id\":null,\"last_state\":\"foreground\",\"last_alive_utc\":\"2026-09-28T10:05:00Z\",\"exception_count\":0}");
            ended.Dispose();

            using (FlockTestClient sdk = Create(config => config.AnalyticsConfig.PersistSessionOnDisk = true))
            {
                sdk.LoginAs("player-1");
                sdk.Run(() => sdk.Client.Analytics.InitializeAsync(CancellationToken.None));

                // Queued, then sent by the flush that follows sign-in.
                JObject report = sdk.Transport.AllTo(FlockEndpoints.LogEvent)
                    .SelectMany(request => (JArray)JObject.Parse(request.JsonBody)["events"])
                    .Cast<JObject>()
                    .Single(entry => (string)entry["message"] == FlockTerminationTracker.EventName);
                Assert.AreEqual("abnormal", (string)Extra(report)["classification"]);
                Assert.IsNull(Extra(report)["previous_session_id"], "No session was running, so none is named");
            }
        }
    }
}
