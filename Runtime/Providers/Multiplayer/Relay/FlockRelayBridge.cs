using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Flock.Exceptions;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>Hands relayed packets to a netcode on this device through its loopback, so the netcode sends and receives as on a direct connection: a player's bridge stands in for the host, a host's for each relayed player.</summary>
    internal sealed class FlockRelayBridge
    {
        /// <summary>The most relayed players a host's bridge forwards for at once, as many as the relay keeps channels for.</summary>
        internal const int MostPlayers = 64;
        // A relayed peer the netcode has not let in within this gives its place back when the next new peer is heard, so strangers on the relay cannot hold every place.
        private static readonly TimeSpan NotLetInWithin = TimeSpan.FromSeconds(30);

        /// <summary>Stands in for <see cref="MostPlayers"/>, so a test can fill a bridge with a few peers.</summary>
        internal static int? MostPlayersForTesting;

        /// <summary>Stands in for how long a peer never let in keeps its place in a full bridge.</summary>
        internal static TimeSpan? NotLetInWithinForTesting;

        private readonly FlockRelayConnection _relay;
        private readonly IFlockLogger _logger;
        private readonly Action<FlockRelayPeer, byte[], int, int> _arrived;
        private readonly object _gate = new object();
        // A host's: one loopback socket a relayed peer, made when the peer is first heard.
        private readonly Dictionary<FlockRelayPeer, FlockRelayLoopback> _peers = new Dictionary<FlockRelayPeer, FlockRelayLoopback>();
        private readonly int _netcodePort;
        // A player's: the one loopback socket the netcode connects to, standing in for the host.
        private readonly FlockRelayLoopback _host;
        // Whether the netcode's packets move to a direct path when the two ends reach each other (FlockRelayUse.WhenNeeded).
        private readonly bool _goesDirect;
        // A player's: its link to the host. A host's: one per player its netcode let in.
        private volatile FlockDirectLink _hostLink;
        private readonly Dictionary<FlockRelayPeer, FlockDirectLink> _links = new Dictionary<FlockRelayPeer, FlockDirectLink>();
        private bool _closed;
        private int _toldTooLarge;

        private FlockRelayBridge(FlockRelayConnection relay, IFlockLogger logger, int netcodePort, FlockRelayPeer host, bool goesDirect)
        {
            _relay = relay;
            _logger = logger;
            _netcodePort = netcodePort;
            _goesDirect = goesDirect;
            _arrived = Arrived;
            if (netcodePort == 0)
                _host = new FlockRelayLoopback(this, host, 0);
        }

        /// <summary>A player's bridge: the netcode connects to 127.0.0.1 on <see cref="LocalPort"/>, and its packets go to <paramref name="host"/>'s relay address; what anyone else sends through the relay is dropped. With <paramref name="goesDirect"/>, the packets move to a direct path once the host and this device reach each other.</summary>
        internal static FlockRelayBridge ToTheHost(FlockRelayConnection relay, FlockRelayPeer host, IFlockLogger logger, bool goesDirect = false)
            => Registered(new FlockRelayBridge(relay, logger, 0, host, goesDirect));

        /// <summary>A host's bridge: each relayed player gets a loopback socket of its own that sends to the netcode listening on <paramref name="netcodePort"/>, up to <see cref="MostPlayers"/>. With <paramref name="goesDirect"/>, a player the netcode let in is offered a direct path.</summary>
        internal static FlockRelayBridge ForTheHost(FlockRelayConnection relay, int netcodePort, IFlockLogger logger, bool goesDirect = false)
        {
            if (netcodePort < 1 || netcodePort > 65535)
                throw new ArgumentOutOfRangeException(nameof(netcodePort));
            return Registered(new FlockRelayBridge(relay, logger, netcodePort, default, goesDirect));
        }

        // One bridge a relay: two netcodes behind one relay address would reach the other end as one and be mixed up.
        private static FlockRelayBridge Registered(FlockRelayBridge bridge)
        {
            if (bridge._relay.HandArrivalsTo(bridge._arrived))
            {
                // The host is the one peer a player's netcode sends to, so it gets a channel at once; a host's peers get one when let in.
                if (bridge._host != null)
                {
                    bridge._relay.KeepChannelTo(bridge._host.Peer);
                    if (bridge._goesDirect)
                        bridge._hostLink = bridge.NewLink(bridge._host, "the host");
                }
                return bridge;
            }
            bridge._host?.Close();
            throw new FlockValidationException("Netcode for GameObjects already runs through this session's relay on another NetworkManager. Shut that one down before starting another.");
        }

        /// <summary>The loopback port a player's netcode connects to.</summary>
        internal int LocalPort => _host?.Port ?? 0;

        internal FlockRelayConnection Relay => _relay;

        internal bool IsClosed
        {
            get
            {
                lock (_gate)
                    return _closed;
            }
        }

        /// <summary>Which relayed player a netcode connection from 127.0.0.1 on <paramref name="loopbackPort"/> is; false when it is none of this host bridge's.</summary>
        internal bool TryGetPeer(int loopbackPort, out FlockRelayPeer peer)
        {
            lock (_gate)
            {
                foreach (KeyValuePair<FlockRelayPeer, FlockRelayLoopback> entry in _peers)
                {
                    if (entry.Value.Port == loopbackPort)
                    {
                        peer = entry.Key;
                        return true;
                    }
                }
            }
            peer = default;
            return false;
        }

        /// <summary>The netcode let <paramref name="peer"/> in: it keeps its place, its packets ride on a relay channel (4 bytes each rather than 36), and it is offered a direct path when the bridge goes direct (the player's offers before this were not taken; the host's own offer asks for one again). A connection let in again gets a new link, since the player's bridge behind it is new.</summary>
        internal void LetIn(FlockRelayPeer peer)
        {
            FlockDirectLink replaced = null;
            lock (_gate)
            {
                if (_closed || !_peers.TryGetValue(peer, out FlockRelayLoopback loopback))
                    return;
                loopback.WasLetIn = true;
                if (_goesDirect)
                {
                    _links.TryGetValue(peer, out replaced);
                    _links.Remove(peer);
                    FlockDirectLink link = NewLink(loopback, "a player");
                    if (link != null)
                        _links.Add(peer, link);
                }
            }
            replaced?.Close();
            _relay.KeepChannelTo(peer);
        }

        /// <summary>Forgets a relayed player that left or was refused, and stops renewing its channel; it gets a new place if it sends again.</summary>
        internal void Forget(FlockRelayPeer peer)
        {
            FlockRelayLoopback loopback;
            FlockDirectLink link;
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer, out loopback))
                    return;
                _peers.Remove(peer);
                if (_links.TryGetValue(peer, out link))
                    _links.Remove(peer);
            }
            link?.Close();
            loopback.Close();
            _relay.LetChannelGo(peer);
        }

        /// <summary>The player's let-in connection left while another of its connections still comes through this address (its game started the netcode again): the address keeps its loopback socket, lets go of its channel and direct link, and waits to be let in again as a newly heard peer does.</summary>
        internal void NoLongerLetIn(FlockRelayPeer peer)
        {
            FlockDirectLink link;
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer, out FlockRelayLoopback loopback))
                    return;
                loopback.WasLetIn = false;
                loopback.WaitingSince = FlockRelayClock.Now();
                if (_links.TryGetValue(peer, out link))
                    _links.Remove(peer);
            }
            link?.Close();
            _relay.LetChannelGo(peer);
        }

        /// <summary>The direct link to <paramref name="peer"/> (the host, on a player's bridge), or null when there is none, whether or not the bridge goes direct.</summary>
        internal FlockDirectLink DirectLinkForTesting(FlockRelayPeer peer)
        {
            if (_host != null)
                return peer.Equals(_host.Peer) ? _hostLink : null;
            lock (_gate)
            {
                _links.TryGetValue(peer, out FlockDirectLink link);
                return link;
            }
        }

        internal int PeersForTesting
        {
            get
            {
                lock (_gate)
                    return _peers.Count;
            }
        }

        /// <summary>The loopback port of each relayed peer a host's bridge forwards for.</summary>
        internal List<int> PeerPortsForTesting
        {
            get
            {
                List<int> ports = new List<int>();
                lock (_gate)
                {
                    foreach (FlockRelayLoopback loopback in _peers.Values)
                        ports.Add(loopback.Port);
                }
                return ports;
            }
        }

        /// <summary>Stops forwarding, closes every loopback socket and stops renewing the channels it kept; the relay stays the session's.</summary>
        internal void Close()
        {
            List<FlockRelayLoopback> closing;
            List<FlockDirectLink> links;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                closing = new List<FlockRelayLoopback>(_peers.Values);
                _peers.Clear();
                links = new List<FlockDirectLink>(_links.Values);
                _links.Clear();
            }
            _relay.StopHandingArrivalsTo(_arrived);
            _hostLink?.Close();
            foreach (FlockDirectLink link in links)
                link.Close();
            if (_host != null)
            {
                _host.Close();
                _relay.LetChannelGo(_host.Peer);
            }
            foreach (FlockRelayLoopback loopback in closing)
            {
                loopback.Close();
                _relay.LetChannelGo(loopback.Peer);
            }
        }

        // On the relay thread: a packet a relayed peer sent, handed to the netcode from that peer's loopback socket.
        private void Arrived(FlockRelayPeer from, byte[] packet, int offset, int count)
        {
            if (FlockDirectLinkWire.IsMessage(packet, offset, count))
            {
                HeardFromTheOtherBridge(from, packet, offset, count);
                return;
            }
            if (_host != null)
            {
                if (from.Equals(_host.Peer))
                    _host.HandToTheNetcode(packet, offset, count);
                return;
            }
            FlockRelayLoopback loopback;
            lock (_gate)
            {
                if (_closed)
                    return;
                if (!_peers.TryGetValue(from, out loopback))
                {
                    ForgetPeersNeverLetIn();
                    if (_peers.Count >= (MostPlayersForTesting ?? MostPlayers))
                        return;
                    try
                    {
                        loopback = new FlockRelayLoopback(this, from, _netcodePort);
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    _peers.Add(from, loopback);
                }
            }
            loopback.HandToTheNetcode(packet, offset, count);
        }

        // Under _gate, as a new peer is first heard: peers the netcode has not let in within their time (refused, or strangers) give their place and socket back.
        private void ForgetPeersNeverLetIn()
        {
            long now = FlockRelayClock.Now();
            long givenTime = FlockRelayClock.Ticks(NotLetInWithinForTesting ?? NotLetInWithin);
            List<FlockRelayPeer> forgotten = null;
            foreach (KeyValuePair<FlockRelayPeer, FlockRelayLoopback> entry in _peers)
            {
                if (entry.Value.WasLetIn || now - entry.Value.WaitingSince < givenTime)
                    continue;
                forgotten = forgotten ?? new List<FlockRelayPeer>();
                forgotten.Add(entry.Key);
            }
            if (forgotten == null)
                return;
            foreach (FlockRelayPeer peer in forgotten)
            {
                _peers[peer].Close();
                _peers.Remove(peer);
            }
        }

        // On the relay thread: an offer, or word of going back to the relay, from the bridge at the other end. A host has links only to players its netcode let in, so nobody else's is taken.
        private void HeardFromTheOtherBridge(FlockRelayPeer from, byte[] packet, int offset, int count)
        {
            if (!_goesDirect)
                return;
            FlockDirectLink link;
            lock (_gate)
            {
                if (_closed)
                    return;
                if (_host != null)
                    link = from.Equals(_host.Peer) ? _hostLink : null;
                else
                    _links.TryGetValue(from, out link);
            }
            if (link == null)
                return;
            byte kind = FlockDirectLinkWire.KindOf(packet, offset);
            if (kind == FlockDirectLinkWire.Offer && FlockDirectLinkWire.TryReadOffer(packet, offset, count, out byte[] theirKey, out byte[] keyTheyHold, out List<IPEndPoint> theirAddresses))
                link.HeardOffer(theirKey, keyTheyHold, theirAddresses);
            else if (kind == FlockDirectLinkWire.BackToTheRelay)
                link.HeardBackToTheRelay(packet, offset, count);
        }

        // A link to the peer a loopback socket stands for, handing what arrives directly to the netcode from that socket; null when no socket could be made.
        private FlockDirectLink NewLink(FlockRelayLoopback loopback, string peerName)
        {
            FlockRelayPeer peer = loopback.Peer;
            try
            {
                return new FlockDirectLink(_relay.ServerEndpoint, bytes => _relay.Send(peer, bytes, 0, bytes.Length), loopback.HandToTheNetcode, _logger, peerName);
            }
            catch (SocketException)
            {
                return null;
            }
        }

        private FlockDirectLink LinkTo(FlockRelayPeer to)
        {
            if (!_goesDirect)
                return null;
            if (_host != null)
                return to.Equals(_host.Peer) ? _hostLink : null;
            lock (_gate)
            {
                _links.TryGetValue(to, out FlockDirectLink link);
                return link;
            }
        }

        // From a loopback socket's thread: what the netcode sent, on directly when the link to the peer is direct, else through the relay. A relay that has stopped (the session ended, or it was lost) closes the bridge.
        internal void SendThroughTheRelay(FlockRelayPeer to, byte[] data, int count)
        {
            if (_relay.HasStopped)
            {
                Close();
                return;
            }
            FlockDirectLink link = LinkTo(to);
            if (link != null && link.TrySend(data, count))
                return;
            if (count > FlockRelayConnection.MostBytesInAPacket && Interlocked.Exchange(ref _toldTooLarge, 1) == 0)
                _logger.LogWarning($"The netcode sent a packet of {count} bytes, more than the {FlockRelayConnection.MostBytesInAPacket} Flock's relay carries; such packets are dropped. Keep Unity Transport's message size at its default.");
            _relay.Send(to, data, 0, count);
        }
    }

    /// <summary>One loopback socket standing in for a relayed peer: what the netcode sends to it goes to the peer through the relay, and what the peer sends reaches the netcode from it.</summary>
    internal sealed class FlockRelayLoopback
    {
        // As much as one datagram can hold, so a larger one is read whole and then refused by the relay rather than cut.
        private const int LargestDatagram = 65536;
        private const int ThreadStackBytes = 256 * 1024;

        private readonly FlockRelayBridge _bridge;
        // A player's socket learns where its netcode sends from with the netcode's first packet; a host's is connected to the netcode's port from the start.
        private readonly bool _learnsTheNetcode;
        private volatile Socket _socket;
        private readonly Thread _thread;
        private readonly byte[] _received = new byte[LargestDatagram];
        private volatile bool _closed;

        internal FlockRelayLoopback(FlockRelayBridge bridge, FlockRelayPeer peer, int netcodePort)
        {
            _bridge = bridge;
            Peer = peer;
            WaitingSince = FlockRelayClock.Now();
            _learnsTheNetcode = netcodePort == 0;
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                if (!_learnsTheNetcode)
                    socket.Connect(new IPEndPoint(IPAddress.Loopback, netcodePort));
            }
            catch
            {
                socket.Close();
                throw;
            }
            _socket = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint).Port;
            _thread = new Thread(Forward, ThreadStackBytes) { IsBackground = true, Name = "Flock relay loopback" };
            _thread.Start();
        }

        internal FlockRelayPeer Peer { get; }
        internal int Port { get; }
        // When it began waiting to be let in: when made, or when no longer let in. Read and written under the bridge's lock.
        internal long WaitingSince;
        // Read and written under the bridge's lock.
        internal bool WasLetIn;

        /// <summary>On the relay thread: hands a packet from the peer to the netcode. A player's socket that has not heard its netcode yet is not connected, so the send fails and the packet is dropped.</summary>
        internal void HandToTheNetcode(byte[] packet, int offset, int count)
        {
            try
            {
                _socket.Send(packet, offset, count, SocketFlags.None);
            }
            catch (SocketException)
            {
                // The netcode is not listening for a moment; the netcode's own resends cover a lost packet.
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile.
            }
        }

        internal void Close()
        {
            _closed = true;
            _socket.Close();
        }

        // The loopback socket's own thread: waits for the netcode's packets and sends each on through the relay.
        private void Forward()
        {
            bool knowsTheNetcode = !_learnsTheNetcode;
            while (!_closed)
            {
                try
                {
                    int count;
                    if (knowsTheNetcode)
                        count = _socket.Receive(_received);
                    else
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Loopback, 0);
                        count = _socket.ReceiveFrom(_received, ref from);
                        _socket.Connect(from);
                        knowsTheNetcode = true;
                    }
                    _bridge.SendThroughTheRelay(Peer, _received, count);
                }
                catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionReset || failure.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    // The netcode's port is closed. A player's netcode that remade its socket (as Unity Transport does after a socket error) sends from a new port, so the socket is made again on this port to hear it.
                    if (_learnsTheNetcode && knowsTheNetcode && !StartOverOnThisPort())
                        return;
                    knowsTheNetcode = !_learnsTheNetcode;
                }
                catch (SocketException failure) when (failure.SocketErrorCode == SocketError.MessageSize)
                {
                    // A datagram larger than any the relay carries; dropped.
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        // A connected socket hears only the port it is connected to, and one cannot be unconnected everywhere, so a new socket takes this port.
        private bool StartOverOnThisPort()
        {
            Socket fresh = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _socket.Close();
                fresh.Bind(new IPEndPoint(IPAddress.Loopback, Port));
            }
            catch (Exception)
            {
                fresh.Close();
                return false;
            }
            _socket = fresh;
            // Closed while the new socket was made: nothing else will close it.
            if (_closed)
            {
                fresh.Close();
                return false;
            }
            return true;
        }
    }
}
