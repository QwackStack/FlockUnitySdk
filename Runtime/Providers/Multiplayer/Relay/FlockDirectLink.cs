using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>Tries to reach one relayed peer directly, from a UDP socket of its own: learns the address the relay server or a STUN server Flock lists sees it at, offers that and its LAN address through the relay, sends probes at the peer's addresses while the peer sends at its own, and once a probe is answered carries the netcode's packets directly. Stays on the relay for good when no answer comes, and goes back to it when the direct path goes quiet.</summary>
    internal sealed class FlockDirectLink
    {
        internal sealed class Timing
        {
            internal TimeSpan Tick = TimeSpan.FromMilliseconds(50);
            internal TimeSpan AskAddressEvery = TimeSpan.FromMilliseconds(500);
            internal int AddressAsks = 3;
            internal TimeSpan OfferEvery = TimeSpan.FromSeconds(1);
            internal TimeSpan OfferFor = TimeSpan.FromSeconds(20);
            internal TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(200);
            internal TimeSpan ProbeFor = TimeSpan.FromSeconds(6);
            internal TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(1);
            internal TimeSpan QuietFor = TimeSpan.FromSeconds(4);
        }

        /// <summary>Stands in for every new link's timing, so a test can run in seconds.</summary>
        internal static Timing TimingForTesting;

        /// <summary>The next link made offers these addresses instead of its own, as an end whose router gives each destination its own port would.</summary>
        internal static List<IPEndPoint> NextLinkOffersForTesting;

        private enum Stage
        {
            LearningItsAddress,
            Offering,
            Probing,
            Direct,
            OnTheRelay,
            Closed,
        }

        private const int LargestDatagram = 65536;
        private const int ThreadStackBytes = 256 * 1024;
        // The peer's offered addresses and the ones its probes came from.
        private const int MostAddressesProbed = FlockDirectLinkWire.MostAddressesOffered + 2;

        private readonly Timing _timing;
        private readonly IPEndPoint _relayServer;
        // Where the socket's own address is asked: the STUN servers Flock lists, then the relay server. A relay server may answer only
        // some of a device's ports (Cloudflare's answered about one in three, measured), and asking again from the same port changes nothing.
        private readonly List<IPEndPoint> _addressServers;
        private readonly Action<byte[]> _sendThroughTheRelay;
        private readonly Action<byte[], int, int> _handToTheNetcode;
        private readonly IFlockLogger _logger;
        private readonly string _peerName;
        private readonly object _gate = new object();
        private readonly byte[] _ourKey = new byte[FlockDirectLinkWire.KeyLength];
        private readonly byte[] _addressRequest;
        private readonly byte[] _addressTransaction = new byte[12];
        private readonly Socket _socket;
        private readonly int _localPort;
        private readonly IPAddress _lanAddress;
        private readonly Thread _thread;
        // Everywhere this link sent, for a test that has it filter as a home router does.
        private readonly HashSet<IPEndPoint> _sentTo = new HashSet<IPEndPoint>();
        private readonly List<IPEndPoint> _offersForTesting;

        private Stage _stage = Stage.LearningItsAddress;
        private IPEndPoint _publicAddress;
        private List<IPEndPoint> _ourAddresses;
        private byte[] _theirKey;
        private List<IPEndPoint> _theirAddresses;
        // Where a probe or answer with both keys came from: its netcode packets are taken; the answered one carries this link's own.
        private IPEndPoint _heardAt;
        private IPEndPoint _direct;
        private bool _offerNow;
        private int _addressAsks;
        private long _nextAddressAskAt;
        private long _nextOfferAt;
        private long _offerUntil;
        private long _nextProbeAt;
        private long _probeUntil;
        private long _nextKeepAliveAt;
        private long _lastHeardAt;

        /// <summary>Starts at once: asks <paramref name="stunServers"/> and the relay server at <paramref name="relayServer"/> for this socket's address, then offers through <paramref name="sendThroughTheRelay"/>.</summary>
        internal FlockDirectLink(IPEndPoint relayServer, IReadOnlyList<IPEndPoint> stunServers, Action<byte[]> sendThroughTheRelay, Action<byte[], int, int> handToTheNetcode, IFlockLogger logger, string peerName)
        {
            _timing = TimingForTesting ?? new Timing();
            _offersForTesting = Interlocked.Exchange(ref NextLinkOffersForTesting, null);
            _relayServer = relayServer;
            _addressServers = new List<IPEndPoint>(stunServers ?? new IPEndPoint[0]);
            // A relay server reached over IPv6 would answer with this socket's IPv6 address, which an offer cannot carry.
            if (relayServer.AddressFamily == AddressFamily.InterNetwork && !_addressServers.Contains(relayServer))
                _addressServers.Add(relayServer);
            _sendThroughTheRelay = sendThroughTheRelay;
            _handToTheNetcode = handToTheNetcode;
            _logger = logger;
            _peerName = peerName;
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(_ourKey);
                random.GetBytes(_addressTransaction);
            }
            _addressRequest = FlockDirectAddresses.StunRequest(_addressTransaction);
            Socket socket = FlockSockets.Open();
            try
            {
                socket.ReceiveTimeout = Math.Max(1, (int)_timing.Tick.TotalMilliseconds);
            }
            catch
            {
                socket.Close();
                throw;
            }
            _socket = socket;
            _localPort = ((IPEndPoint)socket.LocalEndPoint).Port;
            string lan = FlockDirectAddresses.LanAddressToward(relayServer.Address);
            _lanAddress = lan != null && IPAddress.TryParse(lan, out IPAddress parsed) ? parsed : null;
            _nextAddressAskAt = FlockRelayClock.Now();
            _thread = new Thread(Run, ThreadStackBytes) { IsBackground = true, Name = "Flock direct link" };
            _thread.Start();
        }

        /// <summary>True while the netcode's packets to the peer go directly.</summary>
        internal bool IsDirect
        {
            get
            {
                lock (_gate)
                    return _stage == Stage.Direct;
            }
        }

        /// <summary>True once the link has given up or gone back to the relay, or was closed: nothing more goes directly.</summary>
        internal bool HasStopped
        {
            get
            {
                lock (_gate)
                    return _stage >= Stage.OnTheRelay;
            }
        }

        internal int LocalPortForTesting => _localPort;

        internal AddressFamily FamilyForTesting => _socket.AddressFamily;

        /// <summary>The address a server said it sees this link's socket at; null before an answer.</summary>
        internal IPEndPoint PublicAddressForTesting
        {
            get
            {
                lock (_gate)
                    return _publicAddress;
            }
        }

        /// <summary>True while probes go out: both offers are known and no answer has come yet.</summary>
        internal bool IsProbingForTesting
        {
            get
            {
                lock (_gate)
                    return _stage == Stage.Probing;
            }
        }

        /// <summary>The addresses the peer offered and those its probes came from; null before its offer.</summary>
        internal List<IPEndPoint> TheirAddressesForTesting
        {
            get
            {
                lock (_gate)
                    return _theirAddresses == null ? null : new List<IPEndPoint>(_theirAddresses);
            }
        }

        /// <summary>Drops everything that reaches the socket, as a path that died would.</summary>
        internal volatile bool HearsNothingForTesting;

        /// <summary>Drops the peer's answers to this link's probes, as a path that works one way only would.</summary>
        internal volatile bool DropsAnswersForTesting;

        /// <summary>Drops what comes from an address this link never sent to, as a home router does.</summary>
        internal volatile bool FiltersLikeARouterForTesting;

        /// <summary>The peer's offer, heard through the relay: its key, the key it holds for this link (null for none) and its addresses. An offer without this link's key asks for this link's offer again (the last may have been lost).</summary>
        internal void HeardOffer(byte[] theirKey, byte[] keyTheyHold, List<IPEndPoint> theirAddresses)
        {
            lock (_gate)
            {
                if (_stage >= Stage.OnTheRelay)
                    return;
                if (_theirKey == null)
                {
                    _theirKey = theirKey;
                    _theirAddresses = theirAddresses;
                    if (_stage == Stage.Offering)
                        StartProbing(FlockRelayClock.Now());
                }
                else if (!SameKey(_theirKey, theirKey))
                    return;
                // Answered only while the peer lacks this link's key, so two ends never bounce offers at each other.
                _offerNow = _stage != Stage.LearningItsAddress && (keyTheyHold == null || !SameKey(keyTheyHold, _ourKey));
            }
        }

        /// <summary>The peer said, through the relay, that it went back to it: this link does too.</summary>
        internal void HeardBackToTheRelay(byte[] packet, int offset, int count)
        {
            lock (_gate)
            {
                if (_stage >= Stage.OnTheRelay || !FlockDirectLinkWire.IsBetween(packet, offset, count, _ourKey, _theirKey))
                    return;
                bool wasDirect = _stage == Stage.Direct;
                Stop(Stage.OnTheRelay);
                if (wasDirect)
                    _logger.LogInfo($"{_peerName} went back to Flock's relay, so packets to it do too.");
            }
        }

        /// <summary>Sends a netcode packet directly; false when the link is not direct (or the send failed), so it goes through the relay.</summary>
        internal bool TrySend(byte[] data, int count)
        {
            lock (_gate)
            {
                if (_stage != Stage.Direct)
                    return false;
            }
            try
            {
                _socket.Send(data, 0, count, SocketFlags.None);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        internal void Close()
        {
            lock (_gate)
                Stop(Stage.Closed);
        }

        // The link's own thread: hears the socket, a tick at most apart, and keeps the timers.
        private void Run()
        {
            byte[] buffer = new byte[LargestDatagram];
            while (true)
            {
                bool connected;
                lock (_gate)
                {
                    if (_stage >= Stage.OnTheRelay)
                        return;
                    connected = _direct != null;
                }
                try
                {
                    if (connected)
                    {
                        int count = _socket.Receive(buffer);
                        IPEndPoint direct;
                        lock (_gate)
                            direct = _direct;
                        Heard(buffer, count, direct);
                    }
                    else
                    {
                        EndPoint from = FlockSockets.AnySender(_socket);
                        int count = _socket.ReceiveFrom(buffer, ref from);
                        Heard(buffer, count, FlockSockets.AsHeard(from));
                    }
                }
                catch (SocketException)
                {
                    // Nothing within a tick, or Windows reporting an earlier send to a closed port; the timers run either way.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Tick(FlockRelayClock.Now());
            }
        }

        private void Heard(byte[] buffer, int count, IPEndPoint from)
        {
            if (from == null || HearsNothingForTesting)
                return;
            if (FiltersLikeARouterForTesting)
            {
                lock (_gate)
                {
                    if (!_sentTo.Contains(from))
                        return;
                }
            }
            if (FlockDirectLinkWire.IsMessage(buffer, 0, count))
            {
                HeardMessage(buffer, count, from);
                return;
            }
            if (_addressServers.Contains(from))
            {
                IPEndPoint seenAt = FlockDirectAddresses.PublicEndpointInStunAnswer(buffer, count, _addressTransaction);
                if (seenAt != null)
                {
                    lock (_gate)
                    {
                        if (_stage == Stage.LearningItsAddress)
                        {
                            _publicAddress = seenAt;
                            AddressesKnown(FlockRelayClock.Now());
                        }
                    }
                }
                return;
            }
            bool fromThePeer;
            lock (_gate)
            {
                fromThePeer = from.Equals(_direct) || from.Equals(_heardAt);
                if (fromThePeer && from.Equals(_direct))
                    _lastHeardAt = FlockRelayClock.Now();
            }
            if (fromThePeer)
                _handToTheNetcode(buffer, 0, count);
        }

        private void HeardMessage(byte[] buffer, int count, IPEndPoint from)
        {
            byte kind = FlockDirectLinkWire.KindOf(buffer, 0);
            lock (_gate)
            {
                if (_stage >= Stage.OnTheRelay || !FlockDirectLinkWire.IsBetween(buffer, 0, count, _ourKey, _theirKey))
                    return;
                long now = FlockRelayClock.Now();
                if (kind == FlockDirectLinkWire.Probe)
                {
                    _heardAt = from;
                    SendTo(FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Answer, _theirKey, _ourKey), from);
                    // A router that gives each destination its own port sends the peer's probes from an address it never offered: that one is probed too.
                    if (_stage == Stage.Probing && !_theirAddresses.Contains(from) && _theirAddresses.Count < MostAddressesProbed)
                    {
                        _theirAddresses.Add(from);
                        SendTo(FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, _theirKey, _ourKey), from);
                    }
                }
                else if (kind == FlockDirectLinkWire.Answer)
                {
                    if (DropsAnswersForTesting || _stage != Stage.Probing)
                        return;
                    // An answer proves both ways: this link's probe reached the peer, and the peer's answer came back.
                    try
                    {
                        _socket.Connect(FlockSockets.Toward(_socket, from));
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    _direct = from;
                    _heardAt = from;
                    _stage = Stage.Direct;
                    _lastHeardAt = now;
                    _nextKeepAliveAt = now;
                    // Only the keep-alive and the quiet check are due from here, so the thread wakes less often.
                    _socket.ReceiveTimeout = Math.Max(1, (int)Math.Max(_timing.Tick.TotalMilliseconds, _timing.KeepAliveEvery.TotalMilliseconds / 4));
                    _logger.LogInfo($"Packets to {_peerName} now go directly; Flock's relay stays ready in case the direct path goes quiet.");
                }
                else if (kind == FlockDirectLinkWire.KeepAlive && from.Equals(_direct))
                    _lastHeardAt = now;
            }
        }

        // Under _gate, on the link's thread.
        private void Tick(long now)
        {
            lock (_gate)
            {
                switch (_stage)
                {
                    case Stage.LearningItsAddress:
                        if (now < _nextAddressAskAt)
                            break;
                        if (_addressAsks >= _timing.AddressAsks)
                        {
                            // No server said where it sees this socket: the LAN address alone is offered.
                            AddressesKnown(now);
                            break;
                        }
                        _addressAsks++;
                        _nextAddressAskAt = now + FlockRelayClock.Ticks(_timing.AskAddressEvery);
                        foreach (IPEndPoint server in _addressServers)
                            SendTo(_addressRequest, server);
                        break;
                    case Stage.Offering:
                        OfferWhenDue(now);
                        if (_stage == Stage.Offering && now >= _offerUntil)
                            GiveUp("the other end never offered its addresses");
                        break;
                    case Stage.Probing:
                        OfferWhenDue(now);
                        if (now >= _nextProbeAt)
                        {
                            _nextProbeAt = now + FlockRelayClock.Ticks(_timing.ProbeEvery);
                            byte[] probe = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.Probe, _theirKey, _ourKey);
                            foreach (IPEndPoint address in _theirAddresses)
                                SendTo(probe, address);
                        }
                        if (now >= _probeUntil)
                            GiveUp("no probe was answered");
                        break;
                    case Stage.Direct:
                        if (now - _lastHeardAt >= FlockRelayClock.Ticks(_timing.QuietFor))
                        {
                            Stop(Stage.OnTheRelay);
                            _sendThroughTheRelay(FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.BackToTheRelay, _theirKey, _ourKey));
                            _logger.LogInfo($"The direct path to {_peerName} went quiet, so packets to it go through Flock's relay again.");
                            break;
                        }
                        if (now >= _nextKeepAliveAt)
                        {
                            _nextKeepAliveAt = now + FlockRelayClock.Ticks(_timing.KeepAliveEvery);
                            byte[] keepAlive = FlockDirectLinkWire.WriteBetween(FlockDirectLinkWire.KeepAlive, _theirKey, _ourKey);
                            try
                            {
                                _socket.Send(keepAlive, 0, keepAlive.Length, SocketFlags.None);
                            }
                            catch (SocketException)
                            {
                                // The quiet check judges a path that stopped carrying.
                            }
                        }
                        break;
                }
            }
        }

        // Under _gate: this socket's addresses are as known as they will be, so they are offered.
        private void AddressesKnown(long now)
        {
            _ourAddresses = new List<IPEndPoint>();
            if (_publicAddress != null && !_publicAddress.Address.Equals(IPAddress.Any))
                _ourAddresses.Add(_publicAddress);
            IPEndPoint lan = _lanAddress == null ? null : new IPEndPoint(_lanAddress, _localPort);
            if (lan != null && !lan.Equals(_publicAddress))
                _ourAddresses.Add(lan);
            if (_offersForTesting != null)
                _ourAddresses = new List<IPEndPoint>(_offersForTesting);
            if (_ourAddresses.Count == 0)
            {
                GiveUp("this device's address could not be found");
                return;
            }
            _offerUntil = now + FlockRelayClock.Ticks(_timing.OfferFor);
            _nextOfferAt = now;
            _stage = Stage.Offering;
            if (_theirKey != null)
                StartProbing(now);
        }

        // Under _gate.
        private void StartProbing(long now)
        {
            _stage = Stage.Probing;
            _offerNow = true;
            _nextProbeAt = now;
            _probeUntil = now + FlockRelayClock.Ticks(_timing.ProbeFor);
        }

        // Under _gate: offers again while the peer's offer has not come, and once at once when the peer asked by offering.
        private void OfferWhenDue(long now)
        {
            bool due = _offerNow || (_theirKey == null && now >= _nextOfferAt);
            if (!due)
                return;
            _offerNow = false;
            _nextOfferAt = now + FlockRelayClock.Ticks(_timing.OfferEvery);
            _sendThroughTheRelay(FlockDirectLinkWire.WriteOffer(_ourKey, _theirKey, _ourAddresses));
        }

        // Under _gate.
        private void GiveUp(string why)
        {
            Stop(Stage.OnTheRelay);
            _logger.LogInfo($"{_peerName} could not be reached directly ({why}), so packets to it keep going through Flock's relay.");
        }

        // Under _gate: nothing more goes directly, and the socket is let go so its thread ends.
        private void Stop(Stage stage)
        {
            if (_stage >= Stage.OnTheRelay && stage != Stage.Closed)
                return;
            if (_stage == Stage.Closed)
                return;
            _stage = stage;
            _socket.Close();
        }

        // Under _gate. Once direct the socket is connected to the peer, which Windows lets send only without an address, and only there.
        private void SendTo(byte[] bytes, IPEndPoint to)
        {
            _sentTo.Add(to);
            try
            {
                if (_direct == null)
                    _socket.SendTo(bytes, FlockSockets.Toward(_socket, to));
                else if (to.Equals(_direct))
                    _socket.Send(bytes, 0, bytes.Length, SocketFlags.None);
            }
            catch (SocketException)
            {
                // A probe that could not go out; the next one may.
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile.
            }
        }

        private static bool SameKey(byte[] a, byte[] b)
        {
            for (int i = 0; i < FlockDirectLinkWire.KeyLength; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }
}
