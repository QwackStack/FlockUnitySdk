using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The calls multiplayer repeats (search checks, heartbeats, party refreshes), driven by the game's real frames: one request
    // at a time, a late answer dropped by sign-in, and every call stopped by sign-out, a switch, Flock shutting down and quitting.
    public class FlockRepeatingCallsTests
    {
        private const string Route = "probe/repeat";

        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Transport.ReleaseGate();
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private FlockRepeatingCalls Calls => _h.Client.Multiplayer.RepeatingCalls;

        private FlockFakeTransport SignedIn(FlockHttpResponse answer)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(Route, answer);
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            return transport;
        }

        // Ignores the call's token, as a transport that cannot take back a request already sent would; tokens it was handed are kept.
        private Func<CancellationToken, Task<Dictionary<string, object>>> Send(List<CancellationToken> tokensHanded = null)
        {
            string url = _h.Client.GetVersionedApiUrl() + "/" + Route;
            return token =>
            {
                tokensHanded?.Add(token);
                return FlockHttpClient.GetAsync<Dictionary<string, object>>(url, _h.Client.GetBaseHeaders(), CancellationToken.None);
            };
        }

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        // Raises quitting for the provider's own handlers only, so the analytics' quit work does not run inside a test.
        private static void RaiseQuitFor(object target)
        {
            FieldInfo field = typeof(FlockBehaviour).GetField("OnQuit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "FlockBehaviour's quit event is where the test expects it");
            Action quit = (Action)field.GetValue(FlockBehaviour.Instance);
            Delegate[] handlers = quit == null ? new Delegate[0] : quit.GetInvocationList().Where(handler => handler.Target == target).ToArray();
            Assert.AreEqual(1, handlers.Length, "The provider listens for quitting");
            handlers[0].DynamicInvoke();
        }

        private static int FrameHandlersOf(object target)
        {
            FieldInfo field = typeof(FlockBehaviour).GetField("OnTick", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "FlockBehaviour's frame event is where the test expects it");
            Action tick = (Action)field.GetValue(FlockBehaviour.Instance);
            return tick == null ? 0 : tick.GetInvocationList().Count(handler => handler.Target == target);
        }

        [UnityTest]
        public IEnumerator ACallIsSentAtItsInterval_AndEachAnswerHandedOver()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            int answers = 0;
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.15), Send(), _ => answers++, null);
            Assert.AreEqual(0, transport.CountTo(Route), "Nothing is sent before the first interval");

            yield return FlockTestWait.Until(() => answers >= 3, "Three answers were handed over");

            Assert.AreEqual(answers, transport.CountTo(Route), "One answer for each request");
            Assert.IsTrue(call.IsRunning);
        }

        [UnityTest]
        public IEnumerator OneRequestAtATime_WhileItsAnswerIsHeld()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.GateNext(Route);
            int answers = 0;
            Calls.Start("poll", TimeSpan.FromSeconds(0.05), Send(), _ => answers++, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The first request went");

            // A dozen intervals pass with the answer held.
            yield return RealSeconds(0.6f);
            Assert.AreEqual(1, transport.CountTo(Route), "No second request while the first is unanswered");

            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => answers == 1, "The held answer was handed over");
            yield return FlockTestWait.Until(() => transport.CountTo(Route) >= 2, "The next request goes once the answer is in");
        }

        [UnityTest]
        public IEnumerator NothingIsSentAfterSignOut()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            int answers = 0;
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(), _ => answers++, null);
            yield return FlockTestWait.Until(() => answers >= 1, "The call ran while signed in");

            _h.Client.Authentication.Logout();
            int sentBySignOut = transport.CountTo(Route);
            yield return RealSeconds(0.6f);

            Assert.AreEqual(sentBySignOut, transport.CountTo(Route), "Nothing is sent once the player signed out");
            Assert.IsFalse(call.IsRunning);
        }

        [UnityTest]
        public IEnumerator AnAnswerAfterAPlayerSwitch_IsNotUsed_AndNothingMoreIsSent()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.GateNext(Route);
            int answers = 0;
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(), _ => answers++, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The request went as player A");

            // Straight to another player, with no sign-out between.
            _h.LoginAs("player-b");
            transport.ReleaseGate();
            yield return RealSeconds(0.6f);

            Assert.AreEqual(0, answers, "Player A's answer is not used once player B is signed in");
            Assert.AreEqual(1, transport.CountTo(Route), "Nothing more is sent for player A");
            Assert.IsFalse(call.IsRunning);
        }

        [UnityTest]
        public IEnumerator AnAnswerAfterStop_IsNotUsed()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.GateNext(Route);
            int answers = 0;
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(), _ => answers++, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The request went");

            call.Stop();
            transport.ReleaseGate();
            yield return RealSeconds(0.5f);

            Assert.AreEqual(0, answers, "An answer that lands after Stop is not used");
            Assert.AreEqual(1, transport.CountTo(Route));
        }

        [UnityTest]
        public IEnumerator AFailureAfterSignOut_IsNotReported()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Coded(404, "multiplayer.session_not_found"));
            transport.GateNext(Route);
            List<Exception> failures = new List<Exception>();
            Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(), null, failures.Add);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The request went");

            _h.Client.Authentication.Logout();
            transport.ReleaseGate();
            yield return RealSeconds(0.5f);

            Assert.AreEqual(0, failures.Count, "A failure that lands after sign-out says nothing to anyone");
        }

        [UnityTest]
        public IEnumerator FailuresThatMayPass_KeepTheCallGoing()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.OnSequence(Route,
                FlockFakeTransport.Status(503, "{}"),
                FlockFakeTransport.Offline(),
                FlockFakeTransport.Timeout(),
                FlockFakeTransport.Status(200, "<html>captive portal</html>"),
                FlockFakeTransport.Ok("{\"n\":1}"));
            int answers = 0;
            List<Exception> failures = new List<Exception>();
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.05), Send(), _ => answers++, failures.Add);

            yield return FlockTestWait.Until(() => answers >= 1, "An answer came after the failures");

            Assert.AreEqual(0, failures.Count, "None of those failures ended the call");
            Assert.AreEqual(5, transport.CountTo(Route));
            Assert.IsTrue(call.IsRunning);
        }

        [UnityTest]
        public IEnumerator AFailureTheServerMeans_StopsTheCall_AndIsReportedOnce()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Coded(404, "multiplayer.session_not_found"));
            List<Exception> failures = new List<Exception>();
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.05), Send(), null, failures.Add);

            yield return FlockTestWait.Until(() => failures.Count == 1, "The failure was reported");
            yield return RealSeconds(0.5f);

            Assert.AreEqual(1, failures.Count, "Reported once");
            Assert.AreEqual(FlockErrorCode.MultiplayerSessionNotFound, ((FlockException)failures[0]).ErrorCode);
            Assert.AreEqual(1, transport.CountTo(Route), "Nothing is sent after it");
            Assert.IsFalse(call.IsRunning);
        }

        [UnityTest]
        public IEnumerator TooManyRequests_WaitsAsLongAsTheServerAsks()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.OnSequence(Route,
                new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 429, Body = "{}", RetryAfterHeader = "1" },
                FlockFakeTransport.Ok("{\"n\":1}"));
            Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(), null, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The first request went");
            Stopwatch sinceRefused = Stopwatch.StartNew();

            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 2, "The call went again");

            Assert.GreaterOrEqual(sinceRefused.Elapsed.TotalSeconds, 0.9, "It waited for the second Retry-After asked for, not its 0.1 s interval");
        }

        [Test]
        public void ASecondStartOfARunningName_IsRefused_ButTheNextSignInMayStartIt()
        {
            SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            FlockRepeatingCall first = Calls.Start("poll", TimeSpan.FromSeconds(10), Send(), null, null);

            Assert.Throws<InvalidOperationException>(() => Calls.Start("poll", TimeSpan.FromSeconds(10), Send(), null, null));

            _h.LoginAs("player-b");
            FlockRepeatingCall second = Calls.Start("poll", TimeSpan.FromSeconds(10), Send(), null, null);
            Assert.IsFalse(first.IsRunning);
            Assert.IsTrue(second.IsRunning);
        }

        [UnityTest]
        public IEnumerator WaitsFollowTheJitter()
        {
            SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            int rolls = 0;
            // Rolls 0, 1, 0, 1: waits of three quarters and one and a quarter of the interval, in turn.
            FlockRepeatingCalls calls = new FlockRepeatingCalls(_h.Client, () => rolls++ % 2);
            Stopwatch clock = Stopwatch.StartNew();
            List<double> sentAt = new List<double>();
            Func<CancellationToken, Task<Dictionary<string, object>>> send = Send();
            calls.Start("poll", TimeSpan.FromSeconds(0.4), token =>
            {
                sentAt.Add(clock.Elapsed.TotalSeconds);
                return send(token);
            }, null, null);

            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (sentAt.Count < 4 && DateTime.UtcNow < until)
            {
                calls.SendThoseDue();
                yield return null;
            }
            calls.StopAll();

            Assert.AreEqual(4, sentAt.Count, "Four requests went");
            double first = sentAt[1] - sentAt[0];
            double second = sentAt[2] - sentAt[1];
            double third = sentAt[3] - sentAt[2];
            Assert.AreEqual(0.5, first, 0.08, "A roll of 1 waits one and a quarter of the interval");
            Assert.AreEqual(0.3, second, 0.08, "A roll of 0 waits three quarters of it");
            Assert.AreEqual(0.5, third, 0.08);
        }

        [UnityTest]
        public IEnumerator ShuttingFlockDown_StopsEveryCall_AndCancelsTheRequestOnItsWay()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.GateNext(Route);
            List<CancellationToken> tokens = new List<CancellationToken>();
            int answers = 0;
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(tokens), _ => answers++, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The request went");
            FlockMultiplayerProvider provider = _h.Client.Multiplayer;
            Assert.AreEqual(1, FrameHandlersOf(provider), "Precondition: the provider runs on the game's frames");

            FlockClient.Shutdown();

            Assert.IsTrue(tokens[0].IsCancellationRequested, "The request on its way is called off");
            Assert.IsFalse(call.IsRunning);
            Assert.AreEqual(0, FrameHandlersOf(provider), "The provider let go of the game's frames");
            transport.ReleaseGate();
            yield return RealSeconds(0.4f);
            Assert.AreEqual(0, answers);
        }

        [UnityTest]
        public IEnumerator QuittingStopsEveryCall_AndCancelsTheRequestOnItsWay()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.GateNext(Route);
            List<CancellationToken> tokens = new List<CancellationToken>();
            int answers = 0;
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(tokens), _ => answers++, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The request went");

            // Raised through the event, not OnApplicationQuit, which would end FlockBehaviour for the rest of the run.
            RaiseQuitFor(_h.Client.Multiplayer);

            Assert.IsTrue(tokens[0].IsCancellationRequested, "The request on its way is called off");
            Assert.IsFalse(call.IsRunning);
            transport.ReleaseGate();
            yield return RealSeconds(0.4f);
            Assert.AreEqual(0, answers);
            Assert.AreEqual(1, transport.CountTo(Route), "Nothing more is sent after quitting");
        }

        [UnityTest]
        public IEnumerator TheResetForPlayWithoutADomainReload_StopsEveryCall()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            transport.GateNext(Route);
            List<CancellationToken> tokens = new List<CancellationToken>();
            FlockRepeatingCall call = Calls.Start("poll", TimeSpan.FromSeconds(0.1), Send(tokens), null, null);
            yield return FlockTestWait.Until(() => transport.CountTo(Route) == 1, "The request went");

            FlockClient client = _h.Client;
            MethodInfo reset = typeof(FlockClient).GetMethod("ResetStaticState", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reset, "FlockClient's reset is where the test expects it");
            try
            {
                reset.Invoke(null, null);

                Assert.IsTrue(tokens[0].IsCancellationRequested, "The request on its way is called off");
                Assert.IsFalse(call.IsRunning);
            }
            finally
            {
                // The reset forgets the client without shutting it down, as entering Play Mode does; let go of what it left listening.
                client.Commands?.UnsubscribeFlushTriggers();
                (client.Analytics as FlockAnalyticsProvider)?.StopForShutdown();
                FlockEvents.ClearAll();
            }
        }

        [UnityTest]
        public IEnumerator AHandlerThatThrows_IsLogged_AndTheCallGoesOn()
        {
            FlockFakeTransport transport = SignedIn(FlockFakeTransport.Ok("{\"n\":1}"));
            int answers = 0;
            FlockRepeatingCall call = Calls.Start<Dictionary<string, object>>("poll", TimeSpan.FromSeconds(0.05), Send(), _ =>
            {
                answers++;
                throw new InvalidOperationException("the game's handler broke");
            }, null);

            yield return FlockTestWait.Until(() => answers >= 2, "The call went on after its handler threw");

            Assert.IsTrue(call.IsRunning);
            Assert.IsTrue(_h.Logger.Errors.Any(e => e.Contains("the game's handler broke")), "The handler's exception was logged");
        }
    }
}
