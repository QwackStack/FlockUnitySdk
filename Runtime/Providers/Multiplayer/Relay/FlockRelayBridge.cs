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
        private bool _closed;
        private int _toldTooLarge;

        private FlockRelayBridge(FlockRelayConnection relay, IFlockLogger logger, int netcodePort, FlockRelayPeer host)
        {
            _relay = relay;
            _logger = logger;
            _netcodePort = netcodePort;
            _arrived = Arrived;
            if (netcodePort == 0)
                _host = new FlockRelayLoopback(this, host, 0);
        }

        /// <summary>A player's bridge: the netcode connects to 127.0.0.1 on <see cref="LocalPort"/>, and its packets go to <paramref name="host"/>'s relay address; what anyone else sends through the relay is dropped.</summary>
        internal static FlockRelayBridge ToTheHost(FlockRelayConnection relay, FlockRelayPeer host, IFlockLogger logger)
            => Registered(new FlockRelayBridge(relay, logger, 0, host));

        /// <summary>A host's bridge: each relayed player gets a loopback socket of its own that sends to the netcode listening on <paramref name="netcodePort"/>, up to <see cref="MostPlayers"/>.</summary>
        internal static FlockRelayBridge ForTheHost(FlockRelayConnection relay, int netcodePort, IFlockLogger logger)
        {
            if (netcodePort < 1 || netcodePort > 65535)
                throw new ArgumentOutOfRangeException(nameof(netcodePort));
            return Registered(new FlockRelayBridge(relay, logger, netcodePort, default));
        }

        // One bridge a relay: two netcodes behind one relay address would reach the other end as one and be mixed up.
        private static FlockRelayBridge Registered(FlockRelayBridge bridge)
        {
            if (bridge._relay.HandArrivalsTo(bridge._arrived))
                return bridge;
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

        /// <summary>The netcode let <paramref name="peer"/> in: it keeps its place, and its packets ride on a relay channel (4 bytes each rather than 36).</summary>
        internal void LetIn(FlockRelayPeer peer)
        {
            lock (_gate)
            {
                if (_closed || !_peers.TryGetValue(peer, out FlockRelayLoopback loopback))
                    return;
                loopback.WasLetIn = true;
            }
            _relay.KeepChannelTo(peer);
        }

        /// <summary>Forgets a relayed player that left or was refused; it gets a new place if it sends again.</summary>
        internal void Forget(FlockRelayPeer peer)
        {
            FlockRelayLoopback loopback;
            lock (_gate)
            {
                if (!_peers.TryGetValue(peer, out loopback))
                    return;
                _peers.Remove(peer);
            }
            loopback.Close();
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

        /// <summary>Stops forwarding and closes every loopback socket; the relay stays the session's.</summary>
        internal void Close()
        {
            List<FlockRelayLoopback> closing;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                closing = new List<FlockRelayLoopback>(_peers.Values);
                _peers.Clear();
            }
            _relay.StopHandingArrivalsTo(_arrived);
            _host?.Close();
            foreach (FlockRelayLoopback loopback in closing)
                loopback.Close();
        }

        // On the relay thread: a packet a relayed peer sent, handed to the netcode from that peer's loopback socket.
        private void Arrived(FlockRelayPeer from, byte[] packet, int offset, int count)
        {
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
                if (entry.Value.WasLetIn || now - entry.Value.MadeAt < givenTime)
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

        // From a loopback socket's thread: what the netcode sent, on through the relay. A relay that has stopped (the session ended, or it was lost) closes the bridge.
        internal void SendThroughTheRelay(FlockRelayPeer to, byte[] data, int count)
        {
            if (_relay.HasStopped)
            {
                Close();
                return;
            }
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
            MadeAt = FlockRelayClock.Now();
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
        internal long MadeAt { get; }
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
