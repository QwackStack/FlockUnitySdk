using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Connecting players directly, on real frames, against a session server in memory and a STUN server on loopback: what a host
    // publishes, how a player waits for it, and which of the host's addresses a player connects to.
    public class FlockDirectConnectionTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const string HostsPublicAddress = "203.0.113.7";

        private FlockTestClient _h;
        private FakeSessionServer _server;
        private FakeStunServer _stun;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            _stun?.Dispose();
            _stun = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private FlockMultiplayerProvider SignedInAs(string player, double waitReadSeconds = 0.2, double stunWaitSeconds = 0.5)
        {
            _server = new FakeSessionServer();
            _stun = new FakeStunServer();
            _server.StunUrls.Add(_stun.Url);
            _h = FlockTestClient.Create(new FlockFakeTransport(), config => config.PartyRefreshInterval = TimeSpan.Zero);
            FlockHttpClient.Configure(_server);
            _h.LoginAs(player);
            FlockMultiplayerProvider multiplayer = _h.Client.Multiplayer;
            multiplayer.Sessions.SetConnectionWaitIntervalForTesting(TimeSpan.FromSeconds(waitReadSeconds));
            multiplayer.Sessions.DirectAddresses.SetStunWaitForTesting(TimeSpan.FromSeconds(stunWaitSeconds));
            return multiplayer;
        }

        private static IEnumerator Done(Task task, string what) => FlockTestWait.Until(() => task.IsCompleted, what);

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        private static T Failure<T>(Task task) where T : Exception
        {
            Assert.IsTrue(task.IsFaulted, "The call failed");
            T failure = task.Exception.InnerException as T;
            Assert.IsNotNull(failure, $"The call failed with {typeof(T).Name}, not {task.Exception.InnerException?.GetType().Name}");
            return failure;
        }

        private JObject LastPublished() => JObject.Parse(_server.RequestsTo(FakeSessionServer.Publish).Last().JsonBody)["connection_info"] as JObject;

        private IEnumerator Hosted(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held)
        {
            Task<FlockMultiplayerSession> hosted = multiplayer.HostSessionAsync();
            yield return Done(hosted, "the session was hosted");
            Assert.IsFalse(hosted.IsFaulted, "Precondition: hosted");
            held.Add(hosted.Result);
        }

        // A session A hosts, joined by this game as B.
        private IEnumerator JoinedAsB(FlockMultiplayerProvider multiplayer, List<FlockMultiplayerSession> held, List<FakeSessionServer.Session> served)
        {
            FakeSessionServer.Session session = _server.HostedBy(A);
            Task<FlockMultiplayerSession> joined = multiplayer.JoinSessionAsync(session.Code);
            yield return Done(joined, "the session was joined");
            Assert.IsFalse(joined.IsFaulted, "Precondition: joined");
            held.Add(joined.Result);
            served.Add(session);
        }

        private static JObject Direct(object address, object port, string lanAddress = "192.168.1.20", string publicAddress = HostsPublicAddress)
        {
            JObject connection = new JObject { ["mode"] = "direct", ["address"] = JToken.FromObject(address), ["port"] = JToken.FromObject(port) };
            if (lanAddress != null)
                connection["lan_address"] = lanAddress;
            if (publicAddress != null)
                connection["public_address"] = publicAddress;
            return connection;
        }

        // ---- the host publishes ----

        [UnityTest]
        public IEnumerator PublishDirect_PublishesThePublicAddressThePortAndBothAddresses()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task publishing = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(publishing, "published");
            Assert.IsFalse(publishing.IsFaulted, publishing.Exception?.InnerException?.Message);
            JObject published = LastPublished();
            Assert.AreEqual("direct", (string)published["mode"]);
            Assert.AreEqual(HostsPublicAddress, (string)published["address"], "The address players outside the LAN use");
            Assert.AreEqual(JTokenType.Integer, published["port"].Type, "The port is a number on the wire");
            Assert.AreEqual(7777, (int)published["port"]);
            Assert.AreEqual(HostsPublicAddress, (string)published["public_address"]);
            Assert.AreEqual("127.0.0.1", (string)published["lan_address"], "Routed toward the STUN server, here on loopback");
            Assert.AreEqual(1, _stun.Requests, "One STUN request");
            Assert.AreEqual(1, _server.Count(FakeSessionServer.RelayCredentials));
            Assert.AreEqual("{}", _server.RequestsTo(FakeSessionServer.RelayCredentials)[0].JsonBody, "No session named: a direct connection is not relay use");
            Assert.AreEqual("direct", held[0].Connection.Mode, "The session holds what it published");
        }

        [UnityTest]
        public IEnumerator ASecondPublish_ReadsTheStunServersOnce_AndAsksTheStunServerAgain()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task first = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(first, "published");
            _stun.PublicAddress = "198.51.100.4";
            Task second = held[0].PublishDirectConnectionAsync(7778);
            yield return Done(second, "published again");
            Assert.IsFalse(second.IsFaulted);
            Assert.AreEqual(1, _server.Count(FakeSessionServer.RelayCredentials), "The STUN servers are read once a launch");
            Assert.AreEqual(2, _stun.Requests, "A public address can change, so it is asked each time");
            Assert.AreEqual("198.51.100.4", (string)LastPublished()["address"]);
            Assert.AreEqual(7778, (int)LastPublished()["port"]);
        }

        [UnityTest]
        public IEnumerator ASilentStunServer_PublishesTheLanAddressAlone_WarnedOnce()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _stun.Silent = true;
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task first = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(first, "published");
            Task second = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(second, "published again");
            Assert.IsFalse(second.IsFaulted);
            JObject published = LastPublished();
            Assert.AreEqual("127.0.0.1", (string)published["address"], "The LAN address when the public one is unknown");
            Assert.AreEqual("127.0.0.1", (string)published["lan_address"]);
            Assert.IsNull(published["public_address"], "An unknown address is left out, not sent empty");
            Assert.AreEqual(1, _h.Logger.Warnings.Count(warning => warning.Contains("public address could not be found")), "Warned once");
        }

        [UnityTest]
        public IEnumerator TheRelayRouteFailing_StillPublishesTheLanAddress_AndIsAskedAgainNextTime()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            _server.AnswerNext(FakeSessionServer.RelayCredentials, FakeSessionServer.Refused(400, "request.validation_failed"));
            Task first = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(first, "published");
            Assert.IsFalse(first.IsFaulted, first.Exception?.InnerException?.Message);
            Assert.IsNull(LastPublished()["public_address"]);
            Assert.AreEqual(0, _stun.Requests, "No STUN server known");
            StringAssert.Contains(".", (string)LastPublished()["lan_address"], "Routed toward the internet instead");

            Task second = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(second, "published again");
            Assert.AreEqual(2, _server.Count(FakeSessionServer.RelayCredentials), "A failed read is not kept");
            Assert.AreEqual(HostsPublicAddress, (string)LastPublished()["address"]);
        }

        [UnityTest]
        public IEnumerator ANameLookUpThatNeverAnswers_LeavesThePublicAddressUnknown_WithinTheStunWait()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            _server.StunUrls.Clear();
            _server.StunUrls.Add("stun:stun.example.test:19302");
            multiplayer.Sessions.DirectAddresses.SetNameLookUpForTesting(host => new TaskCompletionSource<System.Net.IPAddress[]>().Task);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            Task publishing = held[0].PublishDirectConnectionAsync(7777);
            yield return Done(publishing, "published without the name ever resolving");
            Assert.IsFalse(publishing.IsFaulted, publishing.Exception?.InnerException?.Message);
            Assert.IsNull(LastPublished()["public_address"]);
            Assert.AreEqual((string)LastPublished()["lan_address"], (string)LastPublished()["address"]);
        }

        [UnityTest]
        public IEnumerator TheGamesToken_EndsAPublishWaitingOnANameLookUp()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A, stunWaitSeconds: 30);
            _server.StunUrls.Clear();
            _server.StunUrls.Add("stun:stun.example.test:19302");
            multiplayer.Sessions.DirectAddresses.SetNameLookUpForTesting(host => new TaskCompletionSource<System.Net.IPAddress[]>().Task);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);

            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task publishing = held[0].PublishDirectConnectionAsync(7777, giveUp.Token);
            yield return RealSeconds(0.3f);
            Assert.IsFalse(publishing.IsCompleted, "Precondition: waiting on the name");
            giveUp.Cancel();
            yield return FlockTestWait.Until(() => publishing.IsCompleted, "given up", 3f);
            Assert.IsTrue(publishing.IsCanceled);
            Assert.AreEqual(0, _server.Count(FakeSessionServer.Publish), "Nothing published");
        }

        [UnityTest]
        public IEnumerator APortOutOfRange_IsRefusedBeforeAnyRequest()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(A);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            yield return Hosted(multiplayer, held);
            int before = _server.TotalRequests;

            foreach (int port in new[] { 0, -1, 65536 })
            {
                Task refused = held[0].PublishDirectConnectionAsync(port);
                yield return Done(refused, "refused");
                StringAssert.Contains("from 1 to 65535", Failure<FlockValidationException>(refused).Message);
            }
            Task edge = held[0].PublishDirectConnectionAsync(65535);
            yield return Done(edge, "the highest port");
            Assert.IsFalse(edge.IsFaulted, "65535 is a port");
            Assert.AreEqual(0, _server.RequestsTo(FakeSessionServer.Publish).Count(request => (int)JObject.Parse(request.JsonBody)["connection_info"]["port"] != 65535), "Only the good port was sent");
            Assert.AreEqual(before + 2, _server.TotalRequests, "The relay route and one publish");
        }

        // ---- a player waits for the host ----

        [UnityTest]
        public IEnumerator AWaitingPlayer_LearnsTheHostsAddressFromItsOwnReads_NotOnlyHeartbeats()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            Assert.AreEqual(20, (int)held[0].HeartbeatInterval.TotalSeconds, "Precondition: heartbeats every 20 s");

            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(8));
            yield return RealSeconds(0.5f);
            Assert.IsFalse(waiting.IsCompleted, "Nothing published yet");
            _server.Publishes(served[0], Direct(HostsPublicAddress, 7777));
            yield return FlockTestWait.Until(() => waiting.IsCompleted, "learned well before a heartbeat", 3f);
            Assert.AreSame(held[0].Connection, waiting.Result);
            Assert.AreEqual("direct", waiting.Result.Mode);
            Assert.GreaterOrEqual(_server.Count(FakeSessionServer.Read), 1, "Read while waiting");

            int reads = _server.Count(FakeSessionServer.Read);
            yield return RealSeconds(0.8f);
            Assert.AreEqual(reads, _server.Count(FakeSessionServer.Read), "No reads once nobody waits");
        }

        [UnityTest]
        public IEnumerator AlreadyPublished_IsReturnedAtOnce_WithoutARead()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            FakeSessionServer.Session session = served[0];
            _server.Publishes(session, Direct(HostsPublicAddress, 7777));
            Task<FlockMultiplayerSession> read = multiplayer.GetSessionAsync(session.Id);
            yield return Done(read, "read");
            int reads = _server.Count(FakeSessionServer.Read);

            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(waiting.IsCompleted, "Returned at once");
            Assert.AreSame(held[0].Connection, waiting.Result);
            yield return RealSeconds(0.5f);
            Assert.AreEqual(reads, _server.Count(FakeSessionServer.Read), "No read started");
        }

        [UnityTest]
        public IEnumerator AWaitThatRunsOutOfTime_ReturnsNull_AndTheSessionGoesOn()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);

            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(0.6));
            yield return Done(waiting, "gave up");
            Assert.IsFalse(waiting.IsFaulted);
            Assert.IsNull(waiting.Result);
            Assert.IsFalse(held[0].HasEnded, "Only the wait ended");
            int reads = _server.Count(FakeSessionServer.Read);
            yield return RealSeconds(0.6f);
            Assert.AreEqual(reads, _server.Count(FakeSessionServer.Read), "The reads stopped with the wait");
        }

        [UnityTest]
        public IEnumerator TheSessionEndingWhileWaiting_ReturnsNull_WithTheReason()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);

            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(8));
            _server.Ends(served[0]);
            yield return FlockTestWait.Until(() => waiting.IsCompleted, "the end was read", 3f);
            Assert.IsNull(waiting.Result);
            Assert.AreEqual(FlockMultiplayerSessionEndReason.EndedByHost, held[0].EndReason);
        }

        [UnityTest]
        public IEnumerator TwoWaits_ShareOneRead_AndBothGetTheConnection()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B, waitReadSeconds: 0.5);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);

            Task<FlockMultiplayerSessionConnection> first = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(8));
            Task<FlockMultiplayerSessionConnection> second = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(8));
            yield return RealSeconds(1.3f);
            Assert.LessOrEqual(_server.Count(FakeSessionServer.Read), 3, "One read every half second, not two");
            _server.Publishes(served[0], Direct(HostsPublicAddress, 7777));
            yield return FlockTestWait.Until(() => first.IsCompleted && second.IsCompleted, "both learned", 3f);
            Assert.AreSame(first.Result, second.Result);
        }

        [UnityTest]
        public IEnumerator AnAddressPublishedThenClearedBetweenTwoReads_KeepsTheWaitGoing_UntilTheNewHostPublishes()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            _server.Joins(served[0], "player-c");

            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(8));
            // Published, then hosting moved on, before this game reads again: the reading holds a moved epoch and no address.
            _server.Publishes(served[0], Direct(HostsPublicAddress, 7777));
            _server.MakesHost(served[0], "player-c");
            yield return RealSeconds(1f);
            Assert.IsFalse(waiting.IsCompleted, "Still waiting: the new host has published nothing yet");
            Assert.IsNull(held[0].Connection, "Precondition: the reading cleared the address");
            _server.Publishes(served[0], Direct("198.51.100.4", 7778, publicAddress: "198.51.100.4"));
            yield return FlockTestWait.Until(() => waiting.IsCompleted, "the new host's address", 3f);
            Assert.IsTrue(waiting.Result.TryGetValue("port", out int port));
            Assert.AreEqual(7778, port);
        }

        [UnityTest]
        public IEnumerator TheGamesToken_EndsTheWait_AndItsReads()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);

            CancellationTokenSource giveUp = new CancellationTokenSource();
            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(8), giveUp.Token);
            yield return RealSeconds(0.3f);
            giveUp.Cancel();
            yield return Done(waiting, "given up");
            Assert.IsTrue(waiting.IsCanceled);
            int reads = _server.Count(FakeSessionServer.Read);
            yield return RealSeconds(0.6f);
            Assert.AreEqual(reads, _server.Count(FakeSessionServer.Read), "No reads after the game gave up");
        }

        [UnityTest]
        public IEnumerator FlockShuttingDown_EndsTheWaitAsCancelled()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);

            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(30));
            yield return RealSeconds(0.3f);
            FlockClient.Shutdown();
            yield return FlockTestWait.Until(() => waiting.IsCompleted, "ended with Flock", 3f);
            Assert.IsTrue(waiting.IsCanceled, "Ended as cancelled, as every call does at shutdown");
        }

        [UnityTest]
        public IEnumerator AWaitOfNoLength_OrOnAnEndedSession_IsRefused()
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);

            Task zero = held[0].WaitForConnectionAsync(TimeSpan.Zero);
            Assert.IsTrue(zero.IsFaulted);
            StringAssert.Contains("longer than zero", Failure<FlockValidationException>(zero).Message);
            Task leaving = held[0].LeaveAsync();
            yield return Done(leaving, "left");
            Task ended = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(5));
            StringAssert.Contains("has ended", Failure<FlockValidationException>(ended).Message);
        }

        // ---- which of the host's addresses a player connects to ----

        private IEnumerator Found(JObject published, Action<FlockDirectAddress> check, bool stunSilent = false)
        {
            FlockMultiplayerProvider multiplayer = SignedInAs(B);
            _stun.Silent = stunSilent;
            List<FlockMultiplayerSession> held = new List<FlockMultiplayerSession>();
            List<FakeSessionServer.Session> served = new List<FakeSessionServer.Session>();
            yield return JoinedAsB(multiplayer, held, served);
            _server.Publishes(served[0], published);
            Task<FlockMultiplayerSessionConnection> waiting = held[0].WaitForConnectionAsync(TimeSpan.FromSeconds(5));
            yield return Done(waiting, "published");
            Task<FlockDirectAddress> finding = waiting.Result.FindDirectAddressAsync();
            yield return Done(finding, "found");
            Assert.IsFalse(finding.IsFaulted, finding.Exception?.InnerException?.ToString());
            check(finding.Result);
        }

        [UnityTest]
        public IEnumerator APlayerSharingTheHostsPublicAddress_ConnectsToItsLanAddress()
        {
            yield return Found(Direct(HostsPublicAddress, 7777), found =>
            {
                Assert.AreEqual("192.168.1.20", found.Address);
                Assert.AreEqual(7777, found.Port);
                Assert.IsTrue(found.IsLan);
            });
            Assert.AreEqual(1, _stun.Requests, "This device's own public address was asked");
        }

        [UnityTest]
        public IEnumerator APlayerElsewhere_ConnectsToThePublishedAddress()
        {
            yield return Found(Direct("198.51.100.4", 7777, publicAddress: "198.51.100.4"), found =>
            {
                Assert.AreEqual("198.51.100.4", found.Address);
                Assert.IsFalse(found.IsLan);
            });
        }

        [UnityTest]
        public IEnumerator APlayerWhosePublicAddressIsUnknown_ConnectsToThePublishedAddress()
        {
            yield return Found(Direct(HostsPublicAddress, 7777), found =>
            {
                Assert.AreEqual(HostsPublicAddress, found.Address, "Not the LAN address: this device may be elsewhere");
                Assert.IsFalse(found.IsLan);
            }, stunSilent: true);
            Assert.AreEqual(1, _stun.Requests, "Asked, and no answer came");
        }

        [UnityTest]
        public IEnumerator AHostThatKnewOnlyItsLanAddress_IsReachedThere()
        {
            yield return Found(Direct("192.168.1.20", 7777, publicAddress: null), found =>
            {
                Assert.AreEqual("192.168.1.20", found.Address);
                Assert.IsTrue(found.IsLan);
            });
            Assert.AreEqual(0, _stun.Requests, "Nothing to compare, so nothing asked");
        }

        [UnityTest]
        public IEnumerator APortPublishedAsText_IsStillAPort()
        {
            yield return Found(Direct("198.51.100.4", "7777", publicAddress: "198.51.100.4"), found => Assert.AreEqual(7777, found.Port));
        }

        [UnityTest]
        public IEnumerator ADescriptorPlayersCannotUse_FindsNoAddress()
        {
            JObject[] unusable =
            {
                new JObject { ["mode"] = "steam", ["steam_id"] = "76561198000000000" },
                new JObject { ["mode"] = "direct", ["address"] = "198.51.100.4" },
                Direct("198.51.100.4", 0, null, null),
                Direct("198.51.100.4", 65536, null, null),
                Direct("198.51.100.4", 99999999999L, null, null),
                Direct("198.51.100.4", 7777.5, null, null),
                Direct("7", 7777, null, null),
                Direct("host.example", 7777, null, null),
                Direct("::1", 7777, null, null),
                new JObject { ["mode"] = "Direct", ["address"] = "198.51.100.4", ["port"] = 7777 },
            };
            foreach (JObject descriptor in unusable)
            {
                yield return Found(descriptor, found => Assert.IsNull(found, "No address in " + descriptor.ToString(Newtonsoft.Json.Formatting.None)));
                TearDown();
            }
        }
    }
}
