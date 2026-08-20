using System.Collections.Generic;
using Flock.Analytics;
using Flock.Config;
using Flock.Http;
using Flock.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // Logging contract: severity is separate from verbosity. Errors, warnings and exceptions always reach
    // the console; EnableDebugLogs only adds info and debug on top. Before 1.33.0 the default config
    // selected a logger that no-oped every level, so a shipped build reported nothing at all.
    public class FlockLoggerTests
    {
        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        // Records what actually reached Unity, so "was suppressed" is asserted rather than assumed.
        private static List<string> CaptureLogTypes(System.Action action)
        {
            List<string> seen = new List<string>();
            Application.LogCallback handler = (string condition, string stackTrace, LogType type) => seen.Add(type.ToString());
            Application.logMessageReceived += handler;
            try
            {
                action();
            }
            finally
            {
                Application.logMessageReceived -= handler;
            }
            return seen;
        }

        // ---- LOG-01: a quiet logger still reports errors ----
        [Test]
        public void QuietLogger_StillReportsErrors()
        {
            IFlockLogger logger = new UnityFlockLogger(false);

            // LogAssert fails the test if the message never arrives, so this asserts the call is not a no-op.
            LogAssert.Expect(LogType.Error, "[Flock SDK] boom");
            logger.LogError("boom");
        }

        // ---- LOG-02: a quiet logger still reports warnings ----
        [Test]
        public void QuietLogger_StillReportsWarnings()
        {
            IFlockLogger logger = new UnityFlockLogger(false);

            LogAssert.Expect(LogType.Warning, "[Flock SDK] careful");
            logger.LogWarning("careful");
        }

        // ---- LOG-03: verbosity is what EnableDebugLogs controls, and only that ----
        [Test]
        public void QuietLogger_SuppressesInfoAndDebug()
        {
            IFlockLogger logger = new UnityFlockLogger(false);

            List<string> seen = CaptureLogTypes(() =>
            {
                logger.LogInfo("chatty");
                logger.LogDebug("chattier");
            });

            Assert.AreEqual(0, seen.Count, "Info and debug are the only levels EnableDebugLogs gates.");
        }

        // ---- LOG-04: the verbose logger emits them ----
        [Test]
        public void VerboseLogger_EmitsInfoAndDebug()
        {
            IFlockLogger logger = new UnityFlockLogger(true);

            List<string> seen = CaptureLogTypes(() =>
            {
                logger.LogInfo("chatty");
                logger.LogDebug("chattier");
            });

            Assert.AreEqual(2, seen.Count, "Both info and debug reach the console when verbose.");
            Assert.AreEqual("Log", seen[0]);
            Assert.AreEqual("Log", seen[1]);
        }

        // ---- LOG-05: NullFlockLogger is still total silence, for callers who want that ----
        [Test]
        public void NullLogger_SuppressesEverything()
        {
            IFlockLogger logger = new NullFlockLogger();

            List<string> seen = CaptureLogTypes(() =>
            {
                logger.LogInfo("a");
                logger.LogDebug("b");
                logger.LogWarning("c");
                logger.LogError("d");
            });

            Assert.AreEqual(0, seen.Count, "NullFlockLogger stays a deliberate opt-out from all output.");
        }

        // ---- LOG-06: the regression guard. A default build must not be mute. ----
        [Test]
        public void DebugLogsDisabled_StillSelectsAReportingLogger()
        {
            FlockClient client = FlockClient.Create(BuildConfig(false));

            Assert.IsInstanceOf<UnityFlockLogger>(client.Logger,
                "EnableDebugLogs=false must not select a logger that swallows errors — that shipped once and " +
                "silenced every failure in every default build.");
        }

        // ---- LOG-07: an explicit logger still wins over the default selection ----
        [Test]
        public void ExplicitLogger_OverridesTheDefault()
        {
            FlockClient client = FlockClient.Create(BuildConfig(false), new NullFlockLogger());

            Assert.IsInstanceOf<NullFlockLogger>(client.Logger, "A caller-supplied logger is used as given.");
        }

        // Offline cache off and analytics idle: this fixture only inspects logger selection.
        private static FlockInitConfig BuildConfig(bool enableDebugLogs)
        {
            FlockAnalyticsConfig analytics = new FlockAnalyticsConfig
            {
                PersistSessionOnDisk = false,
                AutoStartSession = false,
                HeartbeatIntervalSeconds = 0f,
                EventBufferFlushIntervalSeconds = 0f
            };

            return new FlockInitConfig(
                "https://test.invalid", "test-key", "test-game", "1.0.0",
                enableDebugLogs: enableDebugLogs,
                analyticsConfig: analytics,
                retryPolicy: new RetryPolicy { MaxRetries = 0 })
            {
                GameVersionId = "test-gvid",
                EnableOfflineCache = false
            };
        }
    }
}
