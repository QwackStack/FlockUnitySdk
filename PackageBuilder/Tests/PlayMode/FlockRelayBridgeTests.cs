using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The bridge on real loopback sockets, with a plain UDP socket standing in for the netcode and relays in memory: a player's netcode
    // reaches the host's relay address, a host's netcode hears each relayed peer from a loopback port of its own.
    public class FlockRelayBridgeTests
    {
        private const string Host = "player-a";
        private const string Player = "player-b";
        private const string Outsider = "player-c";

        private FakeRelayServer _relay;
        private RecordingFlockLogger _logger;
        private readonly List<FlockRelayConnection> _connections = new List<FlockRelayConnection>();
        private readonly List<FlockRelayBridge> _bridges = new List<FlockRelayBridge>();
        private readonly List<StandInNetcode> _netcodes = new List<StandInNetcode>();

        // The netcode's own UDP socket on loopback, as Unity Transport keeps one.
        private sealed class StandInNetcode : IDisposable
        {
            private readonly Socket _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            public StandInNetcode()
            {
                _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }

            public int Port => ((IPEndPoint)_socket.LocalEndPoint).Port;

            public void SendTo(int port, string text) => SendTo(port, Encoding.UTF8.GetBytes(text));

            public void SendTo(int port, byte[] bytes) => _socket.SendTo(bytes, new IPEndPoint(IPAddress.Loopback, port));

            // Every packet waiting: its text and the loopback port it came from.
            public List<KeyValuePair<string, int>> Received()
            {
                List<KeyValuePair<string, int>> received = new List<KeyValuePair<string, int>>();
                byte[] buffer = new byte[2048];
                while (_socket.Available > 0)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int count = _socket.ReceiveFrom(buffer, ref from);
                    received.Add(new KeyValuePair<string, int>(Encoding.UTF8.GetString(buffer, 0, count), ((IPEndPoint)from).Port));
                }
                return received;
            }

            public void Dispose() => _socket.Close();
        }

        [SetUp]
        public void SetUp()
        {
            _relay = new FakeRelayServer();
            _logger = new RecordingFlockLogger();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (FlockRelayBridge bridge in _bridges)
                bridge.Close();
            _bridges.Clear();
            foreach (FlockRelayConnection connection in _connections)
                connection.Close();
            _connections.Clear();
            foreach (StandInNetcode netcode in _netcodes)
                netcode.Dispose();
            _netcodes.Clear();
            FlockRelayBridge.MostPlayersForTesting = null;
            FlockRelayBridge.NotLetInWithinForTesting = null;
            _relay?.Dispose();
            _relay = null;
        }

        private StandInNetcode Netcode()
        {
            StandInNetcode netcode = new StandInNetcode();
            _netcodes.Add(netcode);
            return netcode;
        }

        private FlockRelayBridge Kept(FlockRelayBridge bridge)
        {
            _bridges.Add(bridge);
            return bridge;
        }

        private static IEnumerator Done(Task task, string what, float seconds = 10f) => FlockTestWait.Until(() => task.IsCompleted, what, seconds);

        private static IEnumerator RealSeconds(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        // A relay address of the player's own, opened to the host's relay IP, as StartNetcodeAsync opens one.
        private IEnumerator Opened(string player, List<FlockRelayConnection> into, Func<FlockRelayPeer> openTo = null)
        {
            FlockRelayLogin login = new FlockRelayLogin("127.0.0.1", _relay.Port, "1760000000:game:" + player, "minted-for-the-test");
            Task<FlockRelayConnection> opening = FlockRelayConnection.OpenAsync(login, new FlockRelayConnection.Timing { AnswerWait = TimeSpan.FromSeconds(2) }, _logger, default);
            yield return Done(opening, $"{player}'s relay opened");
            Assert.IsFalse(opening.IsFaulted, $"Precondition: {player}'s relay opened ({opening.Exception?.InnerException?.Message})");
            FlockRelayConnection connection = opening.Result;
            _connections.Add(connection);
            into.Add(connection);
            Task openedTo = connection.OpenToHostAsync(openTo == null ? connection.Address : openTo());
            yield return Done(openedTo, $"{player}'s relay opened to the host");
            Assert.IsFalse(openedTo.IsFaulted, $"Precondition: {player} opened to the host");
        }

        private static IEnumerator Heard(StandInNetcode netcode, string text, List<KeyValuePair<string, int>> into, float seconds = 3f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                into.AddRange(netcode.Received());
                if (into.Any(packet => packet.Key == text))
                    yield break;
                yield return null;
            }
            Assert.Fail($"The netcode never heard \"{text}\"; heard: {string.Join(", ", into.Select(packet => packet.Key))}");
        }

        private static IEnumerator HeardOnTheRelay(FlockRelayConnection relay, string text, List<FlockRelayPeer> from, float seconds = 3f)
        {
            byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (relay.TryReceive(buffer, out int count, out FlockRelayPeer peer))
                {
                    if (Encoding.UTF8.GetString(buffer, 0, count) == text)
                    {
                        from.Add(peer);
                        yield break;
                    }
                }
                yield return null;
            }
            Assert.Fail($"The relay never handed over \"{text}\"");
        }

        private static void SendThrough(FlockRelayConnection relay, FlockRelayPeer to, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Assert.IsTrue(relay.Send(to, bytes, 0, bytes.Length), $"Sent \"{text}\" through the relay");
        }

        // ---- a player's bridge stands in for the host ----

        [UnityTest]
        public IEnumerator APlayersBridge_CarriesTheNetcodesPacketsToTheHost_AndBack()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            FlockRelayConnection host = relays[0];
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ToTheHost(relays[1], host.Address, _logger));
            StandInNetcode netcode = Netcode();

            netcode.SendTo(bridge.LocalPort, "connect");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(host, "connect", from);
            Assert.AreEqual(relays[1].Address, from[0], "The host hears the player from the player's relay address");

            SendThrough(host, relays[1].Address, "accepted");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(netcode, "accepted", heard);
            Assert.AreEqual(bridge.LocalPort, heard.First(packet => packet.Key == "accepted").Value, "The netcode hears the host from the port it connected to");
        }

        [UnityTest]
        public IEnumerator APlayersBridge_DropsWhatAnyoneButTheHostSends()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            yield return Opened(Outsider, relays, () => relays[0].Address);
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ToTheHost(relays[1], relays[0].Address, _logger));
            StandInNetcode netcode = Netcode();
            netcode.SendTo(bridge.LocalPort, "connect");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[0], "connect", from);

            SendThrough(relays[2], relays[1].Address, "from a stranger");
            SendThrough(relays[0], relays[1].Address, "from the host");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(netcode, "from the host", heard);
            yield return RealSeconds(0.3f);
            heard.AddRange(netcode.Received());
            Assert.IsFalse(heard.Any(packet => packet.Key == "from a stranger"), "Only the host reaches the player's netcode");
        }

        [UnityTest]
        public IEnumerator APlayersBridge_FollowsItsNetcodeToANewPort()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ToTheHost(relays[1], relays[0].Address, _logger));
            StandInNetcode first = Netcode();
            first.SendTo(bridge.LocalPort, "connect");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[0], "connect", from);

            // As Unity Transport does after a socket error: the old socket closes and a new one sends from another port.
            first.Dispose();
            StandInNetcode remade = Netcode();
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            bool reached = false;
            while (!reached && DateTime.UtcNow < until)
            {
                // The host keeps sending, which tells the bridge the old port is closed; the netcode keeps resending, as it does.
                SendThrough(relays[0], relays[1].Address, "host heartbeat");
                remade.SendTo(bridge.LocalPort, "from the new port");
                yield return RealSeconds(0.1f);
                byte[] buffer = new byte[FlockRelayConnection.MostBytesInAPacket];
                while (relays[0].TryReceive(buffer, out int count, out FlockRelayPeer peer))
                    reached |= Encoding.UTF8.GetString(buffer, 0, count) == "from the new port";
            }
            Assert.IsTrue(reached, "The netcode's packets from its new port reached the host");
            SendThrough(relays[0], relays[1].Address, "to the new port");
            yield return Heard(remade, "to the new port", heard);
            Assert.AreEqual(bridge.LocalPort, heard.First(packet => packet.Key == "to the new port").Value, "On the port it connected to");
        }

        [UnityTest]
        public IEnumerator AHostsBridge_ForgetsAPeerNeverLetIn_WhenANewPeerArrivesAfterItsTime()
        {
            FlockRelayBridge.NotLetInWithinForTesting = TimeSpan.FromSeconds(1);
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            yield return Opened(Outsider, relays, () => relays[0].Address);
            StandInNetcode netcode = Netcode();
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ForTheHost(relays[0], netcode.Port, _logger));
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            SendThrough(relays[1], relays[0].Address, "b");
            yield return Heard(netcode, "b", heard);
            bridge.LetIn(relays[1].Address);
            yield return RealSeconds(1.2f);
            Assert.AreEqual(1, bridge.PeersForTesting, "Precondition: only the player let in");

            SendThrough(relays[2], relays[0].Address, "c");
            yield return Heard(netcode, "c", heard);
            Assert.AreEqual(2, bridge.PeersForTesting, "The player let in keeps its place, however long ago");
            int portOfC = heard.First(packet => packet.Key == "c").Value;
            yield return RealSeconds(1.2f);
            yield return Opened("player-d", relays, () => relays[0].Address);
            SendThrough(relays[3], relays[0].Address, "d");
            yield return Heard(netcode, "d", heard);
            Assert.IsFalse(bridge.TryGetPeer(portOfC, out _), "The stranger never let in gave its place back, the bridge not full");
            Assert.AreEqual(2, bridge.PeersForTesting);
        }

        // ---- a host's bridge stands in for each relayed peer ----

        [UnityTest]
        public IEnumerator AHostsBridge_GivesEachRelayedPeerALoopbackPortOfItsOwn_BothWays()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            yield return Opened(Outsider, relays, () => relays[0].Address);
            StandInNetcode netcode = Netcode();
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ForTheHost(relays[0], netcode.Port, _logger));

            SendThrough(relays[1], relays[0].Address, "from b");
            SendThrough(relays[2], relays[0].Address, "from c");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(netcode, "from b", heard);
            yield return Heard(netcode, "from c", heard);
            int portOfB = heard.First(packet => packet.Key == "from b").Value;
            int portOfC = heard.First(packet => packet.Key == "from c").Value;
            Assert.AreNotEqual(portOfB, portOfC, "Each relayed peer reaches the netcode from a port of its own");
            Assert.IsTrue(bridge.TryGetPeer(portOfB, out FlockRelayPeer b));
            Assert.AreEqual(relays[1].Address, b);
            Assert.IsTrue(bridge.TryGetPeer(portOfC, out FlockRelayPeer c));
            Assert.AreEqual(relays[2].Address, c);
            Assert.IsFalse(bridge.TryGetPeer(netcode.Port, out _), "A port that is none of the bridge's");

            netcode.SendTo(portOfC, "to c");
            netcode.SendTo(portOfB, "to b");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[1], "to b", from);
            yield return HeardOnTheRelay(relays[2], "to c", from);
            Assert.AreEqual(new[] { relays[0].Address, relays[0].Address }, from.ToArray(), "Each heard the host from its relay address");
        }

        [UnityTest]
        public IEnumerator AHostsBridge_BindsAChannel_OnlyToAPeerLetIn()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            yield return Opened(Outsider, relays, () => relays[0].Address);
            StandInNetcode netcode = Netcode();
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ForTheHost(relays[0], netcode.Port, _logger));
            SendThrough(relays[1], relays[0].Address, "from b");
            SendThrough(relays[2], relays[0].Address, "from c");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(netcode, "from b", heard);
            yield return Heard(netcode, "from c", heard);
            netcode.SendTo(heard.First(packet => packet.Key == "from c").Value, "a reply to c");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[2], "a reply to c", from);
            yield return RealSeconds(0.3f);
            Assert.AreEqual(0, ChannelsBoundBy(relays[0]), "Hearing a peer, or answering it, binds it no channel");

            bridge.LetIn(relays[1].Address);
            yield return FlockTestWait.Until(() => ChannelsBoundBy(relays[0]) == 1, "the peer let in got a channel", 3f);
            yield return RealSeconds(0.3f);
            Assert.AreEqual(1, ChannelsBoundBy(relays[0]), "Only the peer let in");
        }

        private int ChannelsBoundBy(FlockRelayConnection relay)
            => _relay.SeenRequests(FakeRelayServer.MethodChannelBind).Count(seen => seen.ClientPort == relay.LocalPortForTesting);

        [UnityTest]
        public IEnumerator AHostsBridge_ForgetsAPeer_AndGivesItANewPortWhenItSendsAgain()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            StandInNetcode netcode = Netcode();
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ForTheHost(relays[0], netcode.Port, _logger));
            SendThrough(relays[1], relays[0].Address, "first");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(netcode, "first", heard);
            int firstPort = heard.First(packet => packet.Key == "first").Value;

            bridge.Forget(relays[1].Address);
            Assert.AreEqual(0, bridge.PeersForTesting);
            Assert.IsFalse(bridge.TryGetPeer(firstPort, out _), "Its port is nobody's");
            SendThrough(relays[1], relays[0].Address, "again");
            yield return Heard(netcode, "again", heard);
            Assert.AreNotEqual(firstPort, heard.First(packet => packet.Key == "again").Value, "A new place");
            Assert.AreEqual(1, bridge.PeersForTesting);
        }

        [UnityTest]
        public IEnumerator AFullHostsBridge_MakesWayOnlyForAPeerNeverLetIn_OnceItHadItsTime()
        {
            FlockRelayBridge.MostPlayersForTesting = 2;
            FlockRelayBridge.NotLetInWithinForTesting = TimeSpan.FromSeconds(1);
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            yield return Opened(Outsider, relays, () => relays[0].Address);
            yield return Opened("player-d", relays, () => relays[0].Address);
            StandInNetcode netcode = Netcode();
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ForTheHost(relays[0], netcode.Port, _logger));
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            SendThrough(relays[1], relays[0].Address, "b");
            yield return Heard(netcode, "b", heard);
            SendThrough(relays[2], relays[0].Address, "c");
            yield return Heard(netcode, "c", heard);
            bridge.LetIn(relays[1].Address);

            SendThrough(relays[3], relays[0].Address, "d too soon");
            yield return RealSeconds(0.3f);
            heard.AddRange(netcode.Received());
            Assert.IsFalse(heard.Any(packet => packet.Key == "d too soon"), "Full: the peer never let in has not had its time");
            Assert.AreEqual(2, bridge.PeersForTesting);

            yield return RealSeconds(1f);
            SendThrough(relays[3], relays[0].Address, "d later");
            yield return Heard(netcode, "d later", heard);
            Assert.AreEqual(2, bridge.PeersForTesting, "Still at most two");
            Assert.IsTrue(bridge.TryGetPeer(heard.First(packet => packet.Key == "b").Value, out _), "The peer let in kept its place");
            Assert.IsFalse(bridge.TryGetPeer(heard.First(packet => packet.Key == "c").Value, out _), "The one never let in made way");
        }

        // ---- the bridge's life ----

        [UnityTest]
        public IEnumerator Close_ClosesEveryLoopbackSocket_AndHandsArrivalsBackToTheRelay()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            StandInNetcode netcode = Netcode();
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ForTheHost(relays[0], netcode.Port, _logger));
            SendThrough(relays[1], relays[0].Address, "before");
            List<KeyValuePair<string, int>> heard = new List<KeyValuePair<string, int>>();
            yield return Heard(netcode, "before", heard);
            int port = heard[0].Value;

            bridge.Close();
            Assert.AreEqual(0, bridge.PeersForTesting);
            using (Socket again = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                Assert.DoesNotThrow(() => again.Bind(new IPEndPoint(IPAddress.Loopback, port)), "The peer's loopback port was let go");
            SendThrough(relays[1], relays[0].Address, "after");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[0], "after", from);
            Assert.AreEqual(0, netcode.Received().Count, "Nothing more reaches the netcode");
        }

        [UnityTest]
        public IEnumerator ABridgeWhoseRelayStopped_ClosesOnTheNetcodesNextPacket()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ToTheHost(relays[1], relays[0].Address, _logger));
            StandInNetcode netcode = Netcode();
            netcode.SendTo(bridge.LocalPort, "connect");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[0], "connect", from);

            relays[1].Close();
            Assert.IsFalse(bridge.IsClosed, "Precondition: nothing told the bridge yet");
            netcode.SendTo(bridge.LocalPort, "after the relay stopped");
            yield return FlockTestWait.Until(() => bridge.IsClosed, "the bridge closed itself", 3f);
        }

        [UnityTest]
        public IEnumerator APacketLargerThanTheRelayCarries_IsDropped_WithOneWarning()
        {
            List<FlockRelayConnection> relays = new List<FlockRelayConnection>();
            yield return Opened(Host, relays);
            yield return Opened(Player, relays, () => relays[0].Address);
            FlockRelayBridge bridge = Kept(FlockRelayBridge.ToTheHost(relays[1], relays[0].Address, _logger));
            StandInNetcode netcode = Netcode();
            netcode.SendTo(bridge.LocalPort, "connect");
            List<FlockRelayPeer> from = new List<FlockRelayPeer>();
            yield return HeardOnTheRelay(relays[0], "connect", from);

            netcode.SendTo(bridge.LocalPort, new byte[FlockRelayConnection.MostBytesInAPacket + 1]);
            netcode.SendTo(bridge.LocalPort, new byte[FlockRelayConnection.MostBytesInAPacket + 50]);
            netcode.SendTo(bridge.LocalPort, "still carried");
            yield return HeardOnTheRelay(relays[0], "still carried", from);
            Assert.AreEqual(1, _logger.Warnings.Count(warning => warning.Contains("more than the 1400 Flock's relay carries")), "Said once");
            Assert.IsFalse(bridge.IsClosed);
        }
    }
}
