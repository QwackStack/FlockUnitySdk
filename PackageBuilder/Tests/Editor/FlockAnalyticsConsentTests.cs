using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Http;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    // Locks the consent gate end-to-end through the real FlockAnalyticsProvider: no
    // session without consent when RequireExplicitConsent is on, revoke pauses without
    // deleting anything, and the decision persists across a FlockClient recreation
    // (simulating a relaunch).
    public class FlockAnalyticsConsentTests
    {
        private const string KeyGranted = "flock_analytics_consent";
        private const string KeySet = "flock_analytics_consent_set";

        // Answers any session-start POST with a fixed server session id so StartSessionAsync's
        // registration attempt succeeds instead of retrying against a real (fake) host.
        private sealed class AlwaysSuccessAdapter : IFlockHttpAdapter
        {
            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                string body = request.Url.Contains("/analytics/sessions")
                    ? "{\"session_id\":\"srv-session-1\"}"
                    : "{}";
                return Task.FromResult(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = body });
            }
        }

        [SetUp]
        public void SetUp()
        {
            PlayerPrefs.DeleteKey(KeyGranted);
            PlayerPrefs.DeleteKey(KeySet);
            FlockHttpClient.Configure(new AlwaysSuccessAdapter());
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            PlayerPrefs.DeleteKey(KeyGranted);
            PlayerPrefs.DeleteKey(KeySet);
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
        }

        private static FlockClient CreateClient(bool requireExplicitConsent)
        {
            FlockAnalyticsConfig analyticsConfig = new FlockAnalyticsConfig
            {
                RequireExplicitConsent = requireExplicitConsent,
                PersistSessionOnDisk = false,
                AutoStartSession = false,
                HeartbeatIntervalSeconds = 0f,
                EventBufferFlushIntervalSeconds = 0f
            };

            FlockInitConfig initConfig = new FlockInitConfig(
                "https://test.invalid", "test-key", "test-game", "1.0.0",
                analyticsConfig: analyticsConfig,
                // Bounds worst-case blocking time to near-zero if the fake adapter below is
                // ever misconfigured again — defense in depth, not the primary fix.
                retryPolicy: new RetryPolicy { MaxRetries = 0, InitialDelay = TimeSpan.Zero })
            {
                GameVersionId = "test-gvid",
                EnableOfflineCache = false
            };

            FlockClient client = FlockClient.Create(initConfig);

            // FlockClient's constructor calls FlockHttpClient.Configure(initConfig.HttpTimeout),
            // which rebuilds a real network adapter and overwrites whatever [SetUp] configured.
            // The fake adapter has to be (re-)applied after Create, not just before it — mirrors
            // how FlockConfigResolutionTests configures its fake adapter per-test, after Create.
            FlockHttpClient.Configure(new AlwaysSuccessAdapter());

            // Mirrors what a real login does (FlockAuthProvider calls this on success): wires
            // FlockAnalyticsProvider's private _session to Client.Session. Without it, _session
            // stays null and StartSessionAsync always no-ops regardless of consent — these
            // tests call StartSessionAsync directly without a real login, so this has to be
            // done explicitly. AutoStartSession is false above, so this does not itself start
            // a session.
            client.Analytics.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

            return client;
        }

        // Runs inline on the calling (Unity main) thread, unlike the Task.Run-based helper
        // used elsewhere in this test suite — StartSessionAsync touches Time.realtimeSinceStartup,
        // which throws off the main thread. Safe to block synchronously here because
        // AlwaysSuccessAdapter always returns an already-completed task, so every await in
        // this chain resolves inline with no real suspension point to deadlock on.
        private static T Run<T>(Func<Task<T>> action) => action().GetAwaiter().GetResult();

        [Test]
        public void RequireExplicitConsent_NoDecisionYet_HasConsentFalse()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);
            Assert.IsFalse(client.Analytics.HasConsent);
        }

        [Test]
        public void RequireExplicitConsent_False_DefaultsToConsentTrue()
        {
            FlockClient client = CreateClient(requireExplicitConsent: false);
            Assert.IsTrue(client.Analytics.HasConsent);
        }

        [Test]
        public void StartSessionAsync_NoConsent_NoOpsAndReturnsNull()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);

            string sessionId = Run(() => client.Analytics.StartSessionAsync());

            Assert.IsNull(sessionId);
            Assert.IsFalse(client.HasActiveSession);
        }

        [Test]
        public void SetConsent_Grant_AllowsSessionToStart()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);

            client.Analytics.SetConsent(true);
            Assert.IsTrue(client.Analytics.HasConsent);

            string sessionId = Run(() => client.Analytics.StartSessionAsync());

            Assert.IsNotNull(sessionId);
            Assert.IsTrue(client.HasActiveSession);
        }

        [Test]
        public void SetConsent_Revoke_StopsActiveSession()
        {
            FlockClient client = CreateClient(requireExplicitConsent: false);
            Run(() => client.Analytics.StartSessionAsync());
            Assert.IsTrue(client.HasActiveSession);

            client.Analytics.SetConsent(false);

            Assert.IsFalse(client.Analytics.HasConsent);
            Assert.IsFalse(client.HasActiveSession);
        }

        [Test]
        public void SetConsent_SameValueTwice_IsIdempotent()
        {
            FlockClient client = CreateClient(requireExplicitConsent: false);

            client.Analytics.SetConsent(true);
            client.Analytics.SetConsent(true);

            Assert.IsTrue(client.Analytics.HasConsent);
        }

        [Test]
        public void ConsentDecision_PersistsAcrossClientRecreation()
        {
            FlockClient first = CreateClient(requireExplicitConsent: true);
            first.Analytics.SetConsent(true);
            FlockClient.Shutdown();

            FlockClient second = CreateClient(requireExplicitConsent: true);

            Assert.IsTrue(second.Analytics.HasConsent);
        }

        [Test]
        public void EraseLocalAnalyticsData_DoesNotThrow_RegardlessOfConsentState()
        {
            FlockClient client = CreateClient(requireExplicitConsent: false);

            client.Analytics.EraseLocalAnalyticsData();

            client.Analytics.SetConsent(false);
            client.Analytics.EraseLocalAnalyticsData();
        }

        // LogException/LogError/LogEvent are gated the same as TrackEvent (soft no-op, not a
        // throw). Unlike TrackEvent, these are public on IAnalyticProvider, so we can call them
        // directly here — but _logEventCache is still private, so (same limitation as
        // _eventCache) this only proves the call is safe with no consent, not that nothing was
        // enqueued; that part is verified by code inspection of EnqueueLog's consent guard.
        [Test]
        public void LogMethods_NoConsent_DoNotThrow()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);

            Assert.DoesNotThrow(() => client.Analytics.LogEvent("test event"));
            Assert.DoesNotThrow(() => client.Analytics.LogException("boom", "at Foo.Bar()"));
            Assert.DoesNotThrow(() => client.Analytics.LogError("bad state"));
        }

        // The one deliberate exclusion: unaffected by consent regardless of state, since
        // purchase records need financial/tax retention independent of tracking consent.
        [Test]
        public void RecordTransactionAsync_NoConsent_StillSucceeds()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);

            Assert.DoesNotThrowAsync(() => client.Analytics.RecordTransactionAsync(1.0));
        }

        [Test]
        public void LogMethods_WithConsent_DoNotThrow()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);
            client.Analytics.SetConsent(true);

            Assert.DoesNotThrow(() => client.Analytics.LogEvent("test event"));
            Assert.DoesNotThrow(() => client.Analytics.LogException("boom", "at Foo.Bar()"));
            Assert.DoesNotThrow(() => client.Analytics.LogError("bad state"));
        }

        // Locks the never-throws contract: empty caches resolve immediately, no consent needed.
        [Test]
        public void FlushAsync_NoConsent_DoesNotThrow()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);

            Assert.DoesNotThrowAsync(() => client.Analytics.FlushAsync());
        }

        [Test]
        public void OnConsentChanged_FiresOnGrantAndRevoke()
        {
            FlockClient client = CreateClient(requireExplicitConsent: true);
            bool? lastValue = null;
            Action<bool> handler = granted => lastValue = granted;
            FlockEvents.OnConsentChanged += handler;

            try
            {
                client.Analytics.SetConsent(true);
                Assert.AreEqual(true, lastValue);

                client.Analytics.SetConsent(false);
                Assert.AreEqual(false, lastValue);
            }
            finally
            {
                FlockEvents.OnConsentChanged -= handler;
            }
        }
    }
}
