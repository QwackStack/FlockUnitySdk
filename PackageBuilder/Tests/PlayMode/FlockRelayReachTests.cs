using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Reaching a relay that answers as Cloudflare's did on 2026-10-12, a relay in memory on loopback: only some of a device's ports are
    // answered (each every time or never), each address gets an IP of its own, and a relay lets in only the IPs it was opened to.
    public class FlockRelayReachTests
    {
        private const string A = "player-a";
        private const string B = "player-b";
        private const string OtherRelayIp = "203.0.113.51";

        private FakeRelayServer _relay;
        private RecordingFlockLogger _logger;
        private readonly List<FlockRelayConnection> _connections = new List<FlockRelayConnection>();

        [SetUp]
        public void SetUp()
        {
            _relay = new FakeRelayServer();
            _logger = new RecordingFlockLogger();
        }

        [TearDown]
        public void TearDown()
        {
            FlockRelayConnection.MostOpeningsForTesting = null;
            FlockSockets.ResetForTesting();
            foreach (FlockRelayConnection connection in _connections)
                connection.Close();
            _connections.Clear();
            _relay?.Dispose();
            _relay = null;
        }

        // The SDK's own waits: a 5 s answer wait, and 1 s socket rounds that end at the first answer, so a busy machine's slow answer
        // is not taken for a port the relay ignores. A test shortens only the wait it proves.
        private Task<FlockRelayConnection> Open(string player, FlockRelayConnection.Timing timing = null)
            => FlockRelayConnection.OpenAsync(new FlockRelayLogin("127.0.0.1", _relay.Port, "1760000000:game:" + player, "minted-for-the-test"), timing ?? new FlockRelayConnection.Timing(), _logger, default);

        private static IEnumerator Done(Task task, string what, float seconds = 10f) => FlockTestWait.Until(() => task.IsCompleted, what, seconds);

        // Requests the relay saw under different transactions: a request sent again while its answer is on the way keeps its transaction.
        private static int Transactions(IEnumerable<FakeRelayServer.Seen> seen) => seen.Select(each => each.Transaction).Distinct().Count();

        private IEnumerator Opened(string player, List<FlockRelayConnection> into, FlockRelayConnection.Timing timing = null)
        {
            Task<FlockRelayConnection> opening = Open(player, timing);
            yield return Done(opening, $"{player}'s relay opened");
            Assert.IsFalse(opening.IsFaulted, $"{player}'s relay opened ({opening.Exception?.InnerException?.Message})");
            into.Add(opening.Result);
            _connections.Add(opening.Result);
        }

        // ---- a relay that answers only some ports ----

        [UnityTest]
        public IEnumerator ARelayThatAnswersOnlyTheSixthPortItHears_IsReserved_FromThatPort()
        {
            _relay.AnswersOnlyPortsHeard = new HashSet<int> { 6 };
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            Assert.IsTrue(opened[0].IsOpen);
            Assert.AreEqual(1, _relay.Reservations, "Reserved from the one port the relay answers");
        }

        [UnityTest]
        public IEnumerator ARelayThatAnswersNoPortOfTheFirstRound_IsReserved_FromALaterOne()
        {
            // The first round tries 8 ports; the 11th port heard is the third of the second round.
            _relay.AnswersOnlyPortsHeard = new HashSet<int> { 11 };
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            Assert.IsTrue(opened[0].IsOpen);
        }

        [UnityTest]
        public IEnumerator ARelayThatAnswersNoBinding_IsStillReserved_FromANewSocket_AfterEveryRound()
        {
            _relay.AnswersBindings = false;
            // Every round goes unanswered here, so the rounds are short.
            FlockRelayConnection.Timing timing = new FlockRelayConnection.Timing { SocketRoundWait = TimeSpan.FromSeconds(0.2) };
            Stopwatch took = Stopwatch.StartNew();
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened, timing);

            Assert.IsTrue(opened[0].IsOpen, "A relay that ignores bindings is reserved all the same");
            Assert.AreEqual(FlockSocketsTests.FamilyThisMachineGives(), opened[0].SocketFamilyForTesting, "The socket made when no search socket was answered is dual-stack too");
            Assert.GreaterOrEqual(took.Elapsed.TotalSeconds, 0.55, "After every round went unanswered");
        }

        [UnityTest]
        public IEnumerator ARelayThatAnswersNoPortAtAll_FailsAsUnreachable()
        {
            _relay.AnswersOnlyPortsHeard = new HashSet<int>();
            // The give-up is the claim, so the rounds and the answer wait are short: 1.2 s of rounds, then 2 s from a new socket.
            Task<FlockRelayConnection> opening = Open(A, new FlockRelayConnection.Timing { AnswerWait = TimeSpan.FromSeconds(2), SocketRoundWait = TimeSpan.FromSeconds(0.4) });
            yield return Done(opening, "gave up", 15f);

            Assert.IsTrue(opening.IsFaulted, $"It failed (it is {opening.Status})");
            Assert.AreEqual(FlockRelayFailure.Unreachable, ((FlockRelayException)opening.Exception.InnerException).Reason);
        }

        // ---- the socket's family ----

        [UnityTest]
        public IEnumerator ARelay_IsReachedFromADualStackSocket_WhereThePlatformAllows()
        {
            // A phone on mobile data heard Cloudflare's STUN server only on dual-stack sockets (measured): the search's sockets are dual-stack too.
            _relay.AnswersOnlyPortsHeard = new HashSet<int> { 3 };
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            Assert.AreEqual(FlockSocketsTests.FamilyThisMachineGives(), opened[0].SocketFamilyForTesting);
            Assert.IsTrue(opened[0].IsOpen);
            Assert.AreEqual(1, _relay.Reservations, "Reserved from the one port the relay answers, through the IPv4 address in its IPv6 form");
        }

        [UnityTest]
        public IEnumerator ARelayADualStackSocketCannotConnectTo_IsReachedFromAPlainIpv4Socket()
        {
            FlockSockets.DualStackConnectRefusedForTesting = true;
            _relay.AnswersOnlyPortsHeard = new HashSet<int> { 3 };
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            Assert.AreEqual(AddressFamily.InterNetwork, opened[0].SocketFamilyForTesting, "Each search socket fell back to IPv4");
            Assert.IsTrue(opened[0].IsOpen);
        }

        [UnityTest]
        public IEnumerator WithoutIpv6_ARelayIsReachedFromAPlainIpv4Socket()
        {
            FlockSockets.DualStackRefusedForTesting = true;
            _relay.AnswersOnlyPortsHeard = new HashSet<int> { 3 };
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);

            Assert.AreEqual(AddressFamily.InterNetwork, opened[0].SocketFamilyForTesting, "A platform without IPv6 gets a plain IPv4 socket");
            Assert.IsTrue(opened[0].IsOpen);
        }

        // ---- addresses on IPs of their own ----

        [UnityTest]
        public IEnumerator APlayerOnAnotherRelayIp_IsHeard_OnlyOnceTheHostOpensToIt()
        {
            _relay.RelayIps.Add(OtherRelayIp);
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return Opened(A, pair);
            yield return Opened(B, pair);
            Assert.AreNotEqual(pair[0].Address.Ip, pair[1].Address.Ip, "Precondition: an IP each");
            Task hostOpen = pair[0].OpenToHostAsync(pair[0].Address);
            Task playerOpen = pair[1].OpenToHostAsync(pair[0].Address);
            yield return Done(Task.WhenAll(hostOpen, playerOpen), "both opened to the host's relay IP");

            yield return SentAndHeard(pair[1], pair[0], "before", false);
            Task opened = pair[0].OpenToAsync(pair[1].Address);
            yield return Done(opened, "the host opened to the player's relay IP");
            Assert.IsFalse(opened.IsFaulted, opened.Exception?.InnerException?.Message);
            yield return SentAndHeard(pair[1], pair[0], "after", true);
        }

        private static IEnumerator SentAndHeard(FlockRelayConnection from, FlockRelayConnection to, string text, bool heard)
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.IsTrue(from.Send(to.Address, payload, 0, payload.Length), "Sent");
            byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
            DateTime until = DateTime.UtcNow.AddSeconds(heard ? 10 : 0.6);
            while (DateTime.UtcNow < until)
            {
                while (to.TryReceive(buffer, out int count, out FlockRelayPeer _))
                {
                    if (System.Text.Encoding.UTF8.GetString(buffer, 0, count) == text)
                    {
                        Assert.IsTrue(heard, $"\"{text}\" arrived though the host had not opened to the player");
                        yield break;
                    }
                }
                yield return null;
            }
            Assert.IsFalse(heard, $"\"{text}\" did not arrive");
        }

        [UnityTest]
        public IEnumerator OpeningToOneIp_AskedTwiceAtOnce_SendsOneRequest()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            _relay.AnswerDelay = TimeSpan.FromSeconds(0.3);
            FlockRelayPeer player = new FlockRelayPeer(0xCB007133, 5000);

            Task first = opened[0].OpenToAsync(player);
            Task second = opened[0].OpenToAsync(player);
            Assert.AreEqual(1, opened[0].OpeningsAskedForTesting, "One opening asked");
            yield return Done(Task.WhenAll(first, second), "both answered");
            Assert.IsFalse(first.IsFaulted || second.IsFaulted);
            // The 0.3 s answer comes near the 0.5 s resend, which keeps the request's transaction.
            Assert.AreEqual(1, Transactions(_relay.SeenRequests(FakeRelayServer.MethodCreatePermission)), "One request for both callers");
        }

        [UnityTest]
        public IEnumerator ARelayAddressOpenedToTheMostIps_RefusesAnother_WithoutAsking()
        {
            FlockRelayConnection.MostOpeningsForTesting = 2;
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            Task first = opened[0].OpenToAsync(new FlockRelayPeer(0xCB007141, 5000));
            Task second = opened[0].OpenToAsync(new FlockRelayPeer(0xCB007142, 5000));
            yield return Done(Task.WhenAll(first, second), "two IPs opened");
            Assert.IsFalse(first.IsFaulted || second.IsFaulted, "Precondition: the first two opened");

            Task third = opened[0].OpenToAsync(new FlockRelayPeer(0xCB007143, 5000));
            yield return Done(third, "refused");
            Assert.IsTrue(third.IsFaulted, $"A third IP past the most is refused (it is {third.Status})");
            Assert.AreEqual(FlockRelayFailure.OpenRefused, ((FlockRelayException)third.Exception.InnerException).Reason);
            Assert.AreEqual(2, Transactions(_relay.SeenRequests(FakeRelayServer.MethodCreatePermission)), "The third IP was never asked for");
            Assert.IsTrue(opened[0].RefusedToOpenTo(new FlockRelayPeer(0xCB007143, 5000)), "Remembered, so a host tells it once");
        }

        [UnityTest]
        public IEnumerator AnIpTheRelayRefused_IsRefusedAgain_WithoutAsking()
        {
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(A, opened);
            _relay.RefusedOpenings.Add("203.0.113.99");
            FlockRelayPeer refused = new FlockRelayPeer(0xCB007163, 5000);

            Task first = opened[0].OpenToAsync(refused);
            yield return Done(first, "refused");
            Task second = opened[0].OpenToAsync(refused);
            yield return Done(second, "refused again");
            Assert.IsTrue(first.IsFaulted && second.IsFaulted, $"Both refused (they are {first.Status} and {second.Status})");
            Assert.AreEqual(FlockRelayFailure.OpenRefused, ((FlockRelayException)first.Exception.InnerException).Reason);
            Assert.AreEqual(FlockRelayFailure.OpenRefused, ((FlockRelayException)second.Exception.InnerException).Reason);
            Assert.AreEqual(1, Transactions(_relay.SeenRequests(FakeRelayServer.MethodCreatePermission)), "Asked once");
        }
    }
}
