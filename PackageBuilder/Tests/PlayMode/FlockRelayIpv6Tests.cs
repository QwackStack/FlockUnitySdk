using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The relay on a network with IPv6 alone, against the relay in memory listening on both loopbacks: the name gives an IPv6 address
    // alone (or an IPv4 one nothing answers), and the relay is reached at its IPv6 address while still giving an IPv4 relay address, as
    // Cloudflare's relay did over IPv6 on 2026-10-25. A dual-stack network never tries IPv6. Skipped where the machine has no IPv6 loopback.
    public class FlockRelayIpv6Tests
    {
        private const string A = "player-a";
        private const string B = "player-b";

        private FakeRelayServer _relay;
        private RecordingFlockLogger _logger;
        private readonly List<FlockRelayConnection> _connections = new List<FlockRelayConnection>();

        [SetUp]
        public void SetUp()
        {
            _relay = new FakeRelayServer(withStreams: true);
            _logger = new RecordingFlockLogger();
            if (!_relay.HasIpv6)
                Assert.Ignore("This machine has no IPv6 loopback for the relay in memory to listen on.");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (FlockRelayConnection connection in _connections)
                connection.Close();
            _connections.Clear();
            _relay?.Dispose();
            _relay = null;
        }

        // As a network with IPv6 alone answers a name with a native or translated IPv6 address and no IPv4 one.
        private static Task<IPAddress[]> Ipv6Alone(string host) => Task.FromResult(new[] { IPAddress.IPv6Loopback });

        private static Task<IPAddress[]> BothFamilies(string host) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback });

        private FlockRelayLogin Way(string player, FlockRelayTransport transport)
            => new FlockRelayLogin("relay.test", transport == FlockRelayTransport.Tcp ? _relay.TcpPort : _relay.Port, "1760000000:game:" + player, "minted-for-the-test", transport);

        private Task<FlockRelayConnection> Race(string player, Func<string, Task<IPAddress[]>> lookUp, params FlockRelayTransport[] transports)
        {
            FlockRelayConnection.Timing timing = new FlockRelayConnection.Timing { NameLookUpForTesting = lookUp };
            return FlockRelayWays.OpenFirstAsync(transports.Select(transport => Way(player, transport)).ToList(), timing, _logger, CancellationToken.None);
        }

        private static IEnumerator Done(Task task, string what, float seconds = 15f) => FlockTestWait.Until(() => task.IsCompleted, what, seconds);

        private static Task<double> SecondsWhenDone(Task task, Stopwatch clock) => task.ContinueWith(_ => clock.Elapsed.TotalSeconds, TaskContinuationOptions.ExecuteSynchronously);

        private IEnumerator Opened(Task<FlockRelayConnection> opening, string what, List<FlockRelayConnection> into)
        {
            yield return Done(opening, what);
            Assert.IsFalse(opening.IsFaulted, $"{what}: {opening.Exception?.InnerException?.Message}");
            into.Add(opening.Result);
            _connections.Add(opening.Result);
        }

        private static IEnumerator Delivered(FlockRelayConnection from, FlockRelayPeer to, FlockRelayConnection receiver, string text)
        {
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.IsTrue(from.Send(to, payload, 0, payload.Length), $"Sent \"{text}\"");
            byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                while (receiver.TryReceive(buffer, out int count, out FlockRelayPeer peer))
                {
                    if (System.Text.Encoding.UTF8.GetString(buffer, 0, count) == text)
                        yield break;
                }
                yield return null;
            }
            Assert.Fail($"\"{text}\" did not arrive within 10 s");
        }

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        [UnityTest]
        public IEnumerator OnIpv6Alone_TheRelayOpensOverUdpOnIpv6_WithAnIpv4RelayAddress_AndCarriesPackets()
        {
            _relay.IgnoresIpv4 = true;
            List<FlockRelayConnection> pair = new List<FlockRelayConnection>();
            yield return Opened(Race(A, Ipv6Alone, FlockRelayTransport.Udp), "the host's relay opened", pair);
            yield return Opened(Race(B, Ipv6Alone, FlockRelayTransport.Udp), "the player's relay opened", pair);

            Assert.AreEqual($"relay.test:{_relay.Port} over UDP on IPv6", pair[0].Way);
            Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetworkV6, pair[0].ServerEndpoint.AddressFamily, "Reached at the server's IPv6 address");
            StringAssert.StartsWith(FakeRelayServer.RelayIp + ":", pair[1].Address.ToString(), "The relay address is IPv4, as the relay gives one to a client on IPv6");
            Assert.IsTrue(_relay.SeenRequests(FakeRelayServer.MethodAllocate).All(seen => seen.OverIpv6), "Every reservation came over IPv6");
            Assert.IsTrue(_logger.Infos.Any(line => line.Contains("on IPv6") && line.Contains("IPv6 alone")), "Said once why the relay is reached over IPv6");

            Task hostOpen = pair[0].OpenToHostAsync(pair[0].Address);
            Task playerOpen = pair[1].OpenToHostAsync(pair[0].Address);
            yield return Done(Task.WhenAll(hostOpen, playerOpen), "both opened to the host's relay address");
            yield return Delivered(pair[1], pair[0].Address, pair[0], "player to host on IPv6");
            yield return Delivered(pair[0], pair[1].Address, pair[1], "host to player on IPv6");
            pair[1].KeepChannelTo(pair[0].Address);
            pair[0].KeepChannelTo(pair[1].Address);
            yield return FlockTestWait.Until(() => pair[1].ChannelNumberForTesting(pair[0].Address) != 0 && pair[0].ChannelNumberForTesting(pair[1].Address) != 0, "channels bound");
            yield return Delivered(pair[1], pair[0].Address, pair[0], "on a channel, 19 b");
            yield return Delivered(pair[0], pair[1].Address, pair[1], "back on a channel 1");
        }

        [UnityTest]
        public IEnumerator OnIpv6Alone_WithUdpBlocked_TheRelayOpensOverTcpOnIpv6()
        {
            _relay.IgnoresIpv4 = true;
            _relay.IgnoresUdp = true;
            Stopwatch clock = Stopwatch.StartNew();
            Task<FlockRelayConnection> opening = Race(A, Ipv6Alone, FlockRelayTransport.Udp, FlockRelayTransport.Tcp);
            Task<double> openedAt = SecondsWhenDone(opening, clock);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(opening, "the relay opened", opened);

            Assert.AreEqual($"relay.test:{_relay.TcpPort} over TCP on IPv6", opened[0].Way);
            yield return Done(openedAt, "the time it opened");
            // The IPv4 ways end at once (the name gives no IPv4 address): UDP on IPv6 starts at once, TCP over IPv4 at 2 s fails at once, and
            // TCP on IPv6 starts then, though UDP on IPv6 is still waiting; not 2 s later.
            Assert.That(openedAt.Result, Is.InRange(1.8, 3.4), "UDP on IPv6 had its 2 s, then TCP on IPv6 opened as soon as TCP over IPv4 failed");
        }

        [UnityTest]
        public IEnumerator AnIpv4AddressNothingAnswers_IsGivenUp_ForTheServersIpv6Address()
        {
            // The name gives both, but nothing reaches the IPv4 one, as on a network with IPv6 alone that still hands out A records.
            _relay.IgnoresIpv4 = true;
            Stopwatch clock = Stopwatch.StartNew();
            Task<FlockRelayConnection> opening = Race(A, BothFamilies, FlockRelayTransport.Udp, FlockRelayTransport.Tcp);
            Task<double> openedAt = SecondsWhenDone(opening, clock);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(opening, "the relay opened", opened);

            Assert.AreEqual($"relay.test:{_relay.Port} over UDP on IPv6", opened[0].Way);
            yield return Done(openedAt, "the time it opened");
            // UDP over IPv4 waits unanswered; UDP on IPv6 starts at 2 s and opens.
            Assert.That(openedAt.Result, Is.InRange(1.8, 4.5), "Opened on IPv6 once UDP over IPv4 had its 2 s");
            yield return RealSeconds(1f);
            Assert.AreEqual(1, _relay.Reservations, "One reservation, the one kept");
        }

        [UnityTest]
        public IEnumerator ANetworkThatDropsUdpOverIpv4Alone_StaysOnUdp_OverIpv6()
        {
            // As the phone hotspot measured on 2026-10-25: UDP over IPv4 unanswered, TCP over IPv4 and everything over IPv6 working.
            _relay.IgnoresUdpOverIpv4 = true;
            Task<FlockRelayConnection> opening = Race(A, BothFamilies, FlockRelayTransport.Udp, FlockRelayTransport.Tcp);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(opening, "the relay opened", opened);

            Assert.AreEqual($"relay.test:{_relay.Port} over UDP on IPv6", opened[0].Way, "UDP over IPv6, not a stream over IPv4: a stream holds packets back for a lost one");
            yield return RealSeconds(1f);
            Assert.AreEqual(0, _relay.StreamsAccepted, "No TCP connection was made");
        }

        [UnityTest]
        public IEnumerator ADualStackNetwork_NeverTriesIpv6_WhileIpv4Opens()
        {
            Task<FlockRelayConnection> opening = Race(A, BothFamilies, FlockRelayTransport.Udp, FlockRelayTransport.Tcp);
            List<FlockRelayConnection> opened = new List<FlockRelayConnection>();
            yield return Opened(opening, "the relay opened", opened);

            Assert.AreEqual($"relay.test:{_relay.Port} over UDP", opened[0].Way);
            yield return RealSeconds(2.5f);
            Assert.IsFalse(_relay.AllSeen().Any(seen => seen.OverIpv6), "Nothing went over IPv6 while IPv4 worked");
            Assert.AreEqual(0, _relay.StreamsAccepted, "Nor over TCP");
            Assert.IsFalse(_logger.Infos.Any(line => line.Contains("relay is reached")), "Nothing to say when the relay opens as it always did");
        }
    }
}
