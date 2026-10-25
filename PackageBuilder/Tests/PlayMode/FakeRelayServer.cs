using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace Flock.Tests.PlayMode
{
    // A TURN relay on this machine's loopback, with coturn_p2p's answers as measured on 2026-10-09: an unsigned reservation is
    // challenged (401 with realm and nonce), a wrong password is refused again (401), a second reservation from one socket is 437,
    // a repeat of a request under its transaction is answered again, an opening to a refused address is 403, a channel taken twice
    // or out of range is 400, a release (lifetime 0) ends a reservation and a renewal after it is 437, and data is relayed between
    // its own reservations (channel frames to a peer that bound a channel back, data indications otherwise). Every success to a
    // signed request is signed and every answer fingerprinted. A test chooses silences, refusals, stale nonces and lifetimes, and can
    // make it answer as Cloudflare's relay did on 2026-10-12: an IP of its own for each reservation, and only some client ports answered.
    // Made with streams, it also takes TURN over TCP and over TLS (a certificate for relay.test, localhost and 127.0.0.1 no platform
    // trusts), framed as Cloudflare's relay framed them on 2026-10-25: each message whole, channel frames padded to 4 bytes. A reservation
    // made over a stream ends when the stream does, as coturn does (Cloudflare's not measured). It listens on IPv6 loopback too, on the
    // same ports, where the machine has one (HasIpv6), and gives a client reaching it over IPv6 an IPv4 relay address, as Cloudflare's
    // relay did on 2026-10-25; a test can make it ignore either family, as a network without it would.
    internal sealed class FakeRelayServer : IDisposable
    {
        internal const string Realm = "test-realm";
        internal const string RelayIp = "203.0.113.50";
        private const uint MagicCookie = 0x2112A442;
        private const ushort Allocate = 0x003, Refresh = 0x004, Send = 0x006, Data = 0x007, CreatePermission = 0x008, ChannelBind = 0x009, Binding = 0x001;
        private const ushort Username = 0x0006, MessageIntegrity = 0x0008, ErrorCode = 0x0009, ChannelNumber = 0x000C, Lifetime = 0x000D;
        private const ushort XorPeerAddress = 0x0012, DataValue = 0x0013, RealmAttribute = 0x0014, Nonce = 0x0015, XorRelayedAddress = 0x0016;
        private const ushort XorMappedAddress = 0x0020, Fingerprint = 0x8028;

        private readonly UdpClient _socket;
        private readonly UdpClient _socket6;
        private readonly Thread _thread;
        private readonly Thread _thread6;
        private readonly TcpListener _tcp;
        private readonly TcpListener _tls;
        private readonly TcpListener _tcp6;
        private readonly TcpListener _tls6;
        private readonly X509Certificate2 _certificate;
        private readonly Dictionary<IPEndPoint, StreamClient> _streams = new Dictionary<IPEndPoint, StreamClient>();
        private int _unreadableStreamBytes;
        private int _streamsAccepted;
        private readonly object _lock = new object();
        private readonly List<Reservation> _reservations = new List<Reservation>();
        private readonly List<Seen> _seen = new List<Seen>();
        private volatile bool _stopped;
        private int _nextPort = 50000;
        private int _nonceNumber = 1;

        internal sealed class Seen
        {
            public ushort Method;
            public ushort Class;
            public bool Signed;
            public string Transaction;
            public uint? LifetimeAsked;
            public int ClientPort;
            public bool OverIpv6;
            public DateTime At;
        }

        private sealed class Reservation
        {
            public IPEndPoint Client;
            public string Ip;
            public int RelayedPort;
            public string AllocateTransaction;
            public DateTime ExpiresAt;
            public readonly Dictionary<string, DateTime> Openings = new Dictionary<string, DateTime>();
            public readonly Dictionary<ushort, KeyValuePair<IPEndPoint, DateTime>> Channels = new Dictionary<ushort, KeyValuePair<IPEndPoint, DateTime>>();
        }

        // The password every login is checked against.
        internal volatile string Password = "minted-for-the-test";
        internal volatile bool Silent;
        internal TimeSpan ReservationLifetime = TimeSpan.FromSeconds(600);
        internal TimeSpan OpeningLifetime = TimeSpan.FromSeconds(300);
        internal TimeSpan ChannelLifetime = TimeSpan.FromSeconds(600);
        // An error code to answer every signed reservation with (486 full, 442 and so on), or 0.
        internal volatile int RefuseReservationsWith;
        // Signed requests of these methods are dropped this many more times.
        private readonly Dictionary<ushort, int> _dropsLeft = new Dictionary<ushort, int>();
        // Signed requests answered 438 with a new nonce this many more times; -1 for always.
        private int StaleNonceAnswersLeft;
        // Openings to these IPs are refused with 403.
        internal readonly HashSet<string> RefusedOpenings = new HashSet<string>();
        // Openings are refused with an error that names no code.
        internal volatile bool RefuseOpeningsNamingNoCode;
        // Every answer leaves this much later, as a farther relay's would.
        internal TimeSpan AnswerDelay = TimeSpan.Zero;
        // The next signed reservation is answered first with a success it did not sign, naming this address.
        internal volatile bool ForgeNextReservation;
        // Answers to signed reservations are held while this is set, and sent when it is cleared.
        private readonly List<KeyValuePair<byte[], IPEndPoint>> _heldAnswers = new List<KeyValuePair<byte[], IPEndPoint>>();
        internal volatile bool HoldReservationAnswers;
        // The IPs reservations are given in turn: one, as coturn does, or several, as Cloudflare's relay gives addresses (measured 2026-10-12).
        internal readonly List<string> RelayIps = new List<string> { RelayIp };
        private int _reservationsMade;
        // Answers only the client ports first heard at these places (1 for the first port heard, and so on); every other port is ignored
        // whole, as Cloudflare's relay answered about one port in three, every request or none (measured 2026-10-12). Null answers every port.
        internal HashSet<int> AnswersOnlyPortsHeard;
        private readonly Dictionary<int, int> _portsHeard = new Dictionary<int, int>();
        // Binding requests answered with the address they came from; off for a relay that ignores them.
        internal volatile bool AnswersBindings = true;
        // Client ports whose packets no longer reach the relay, as a router that moved a device to another port leaves it; what the relay sends them still goes.
        private readonly HashSet<int> _portsNoLongerHeard = new HashSet<int>();
        // Nothing sent over UDP reaches the relay, as on a network that blocks UDP to it; streams still do.
        internal volatile bool IgnoresUdp;
        // TCP connections are taken and then never read from or answered (no TLS handshake either), as a network that lets the connection
        // through and nothing after it.
        internal volatile bool StreamsSilent;
        // Streams are not read from while this is set, so what a client sends piles up behind them.
        internal volatile bool PauseStreamReading;
        // Nothing from a client of this family reaches the relay (a stream of it is closed at once), as on a network without it.
        internal volatile bool IgnoresIpv4;
        internal volatile bool IgnoresIpv6;
        // UDP over IPv4 alone never reaches the relay, as through a phone hotspot that carried UDP over IPv6 only (measured 2026-10-25).
        internal volatile bool IgnoresUdpOverIpv4;

        internal FakeRelayServer(bool withStreams = false)
        {
            // One port on both loopbacks, so a login's one port reaches either; another pair is tried when the IPv6 port is taken.
            for (int attempt = 0; ; attempt++)
            {
                _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_socket.Client.LocalEndPoint).Port;
                _socket6 = OnIpv6Loopback(() => new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, Port)), out bool taken);
                if (!taken || attempt == 20)
                    break;
                _socket.Close();
            }
            _thread = new Thread(() => Serve(_socket)) { IsBackground = true, Name = "Fake relay server" };
            _thread.Start();
            if (_socket6 != null)
            {
                _thread6 = new Thread(() => Serve(_socket6)) { IsBackground = true, Name = "Fake relay server on IPv6" };
                _thread6.Start();
            }
            if (!withStreams)
                return;
            _certificate = new X509Certificate2(Convert.FromBase64String(FakeRelayCertificate.Pfx), FakeRelayCertificate.Password, X509KeyStorageFlags.Exportable);
            _tcp = Listen(stream => stream, out _tcp6);
            _tls = Listen(Secured, out _tls6);
            TcpPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            TlsPort = ((IPEndPoint)_tls.LocalEndpoint).Port;
        }

        // True where the machine has an IPv6 loopback the relay listens on too (a CI container may have none).
        internal bool HasIpv6 => _socket6 != null;

        // Made on IPv6 loopback, or null where the machine has none; taken is true when the port was in use, so another may be tried.
        private static T OnIpv6Loopback<T>(Func<T> make, out bool taken) where T : class
        {
            taken = false;
            try
            {
                return make();
            }
            catch (SocketException failure)
            {
                taken = failure.SocketErrorCode == SocketError.AddressAlreadyInUse;
                return null;
            }
        }

        private bool Ignores(IPEndPoint client) => client.AddressFamily == AddressFamily.InterNetworkV6 && !client.Address.IsIPv4MappedToIPv6 ? IgnoresIpv6 : IgnoresIpv4;

        // The address a request came from, for an answer: the client reads none from the relay, so an IPv6 one is left out rather than written.
        private static byte[][] MappedTo(IPEndPoint from)
            => from.AddressFamily == AddressFamily.InterNetwork ? new[] { Attr(XorMappedAddress, Address(from)) } : new byte[0][];

        internal int Port { get; }
        internal string Url => $"turn:127.0.0.1:{Port}?transport=udp";
        internal int TcpPort { get; }
        internal int TlsPort { get; }
        internal string TcpUrl => $"turn:127.0.0.1:{TcpPort}?transport=tcp";
        // The certificate names relay.test, so a client checks it against that name; the test looks relay.test up as 127.0.0.1.
        internal string TlsUrl => $"turns:relay.test:{TlsPort}?transport=tcp";
        internal string UdpUrlNamed(string host) => $"turn:{host}:{Port}?transport=udp";
        internal string TcpUrlNamed(string host) => $"turn:{host}:{TcpPort}?transport=tcp";

        // Stream connections open now, and bytes a client sent that were no TURN message (a channel frame sent without its padding misreads the next).
        internal int StreamConnections
        {
            get { lock (_lock) return _streams.Count; }
        }

        internal int UnreadableStreamBytes => Volatile.Read(ref _unreadableStreamBytes);

        // TCP connections the relay took, on either stream port, however they ended.
        internal int StreamsAccepted => Volatile.Read(ref _streamsAccepted);

        // The relay closes every stream, as one that restarted or dropped its connections would; their reservations end with them.
        internal void CloseEveryStream()
        {
            List<StreamClient> open;
            lock (_lock)
                open = _streams.Values.ToList();
            foreach (StreamClient client in open)
                client.Close();
        }

        // The relay resets every stream (no orderly close), so a client's read fails with the platform's own words for it.
        internal void ResetEveryStream()
        {
            List<StreamClient> open;
            lock (_lock)
                open = _streams.Values.ToList();
            foreach (StreamClient client in open)
            {
                // The socket itself, not the stream over it, which would close in order first.
                client.Tcp.Client.LingerState = new LingerOption(true, 0);
                client.Tcp.Client.Close();
            }
        }

        private sealed class StreamClient
        {
            internal TcpClient Tcp;
            internal Stream Stream;
            internal IPEndPoint From;
            internal readonly object WriteGate = new object();

            internal void Close()
            {
                try
                {
                    Tcp.Close();
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }
        }

        private Stream Secured(Stream stream)
        {
            SslStream tls = new SslStream(stream, false);
            tls.AuthenticateAsServer(_certificate, false, SslProtocols.Tls12, false);
            return tls;
        }

        // A listener on IPv4 loopback, and one on the same port on IPv6 loopback where the machine has it.
        private TcpListener Listen(Func<Stream, Stream> secure, out TcpListener onIpv6)
        {
            TcpListener listener = null;
            onIpv6 = null;
            for (int attempt = 0; attempt <= 20; attempt++)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                onIpv6 = OnIpv6Loopback(() =>
                {
                    TcpListener six = new TcpListener(IPAddress.IPv6Loopback, port);
                    six.Start();
                    return six;
                }, out bool taken);
                if (!taken || !HasIpv6)
                    break;
                listener.Stop();
            }
            TcpListener four = listener;
            new Thread(() => Accept(four, secure)) { IsBackground = true, Name = "Fake relay server streams" }.Start();
            if (onIpv6 != null)
            {
                TcpListener six = onIpv6;
                new Thread(() => Accept(six, secure)) { IsBackground = true, Name = "Fake relay server streams on IPv6" }.Start();
            }
            return listener;
        }

        private void Accept(TcpListener listener, Func<Stream, Stream> secure)
        {
            while (!_stopped)
            {
                TcpClient tcp;
                try
                {
                    tcp = listener.AcceptTcpClient();
                    Interlocked.Increment(ref _streamsAccepted);
                }
                catch (Exception)
                {
                    return;
                }
                new Thread(() => ServeStream(tcp, secure)) { IsBackground = true, Name = "Fake relay server stream" }.Start();
            }
        }

        private void ServeStream(TcpClient tcp, Func<Stream, Stream> secure)
        {
            StreamClient client = new StreamClient { Tcp = tcp, From = (IPEndPoint)tcp.Client.RemoteEndPoint };
            try
            {
                if (Ignores(client.From))
                    return;
                tcp.NoDelay = true;
                if (StreamsSilent)
                {
                    while (!_stopped && tcp.Connected)
                        Thread.Sleep(50);
                    return;
                }
                client.Stream = secure(tcp.GetStream());
                lock (_lock)
                    _streams[client.From] = client;
                byte[] held = new byte[0];
                byte[] chunk = new byte[16384];
                while (!_stopped)
                {
                    while (PauseStreamReading && !_stopped)
                        Thread.Sleep(10);
                    int count = client.Stream.Read(chunk, 0, chunk.Length);
                    if (count <= 0)
                        return;
                    held = held.Concat(chunk.Take(count)).ToArray();
                    while (held.Length >= 4)
                    {
                        int length = (held[2] << 8) | held[3];
                        int whole;
                        if ((held[0] & 0xC0) == 0x40)
                            whole = 4 + length + (4 - length % 4) % 4;
                        else if ((held[0] & 0xC0) == 0 && Read32(held, 4) == MagicCookie)
                            whole = 20 + length;
                        else
                        {
                            Interlocked.Add(ref _unreadableStreamBytes, held.Length);
                            return;
                        }
                        if (held.Length < whole)
                            break;
                        byte[] message = held.Take(whole).ToArray();
                        held = held.Skip(whole).ToArray();
                        try
                        {
                            Handle(message, client.From);
                        }
                        catch (Exception failure)
                        {
                            UnityEngine.Debug.LogError("Fake relay server: " + failure);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // The client went, or the relay closed it.
            }
            finally
            {
                lock (_lock)
                {
                    _streams.Remove(client.From);
                    _reservations.RemoveAll(each => each.Client.Equals(client.From));
                }
                client.Close();
            }
        }

        internal void DropNext(ushort method, int times)
        {
            lock (_lock)
                _dropsLeft[method] = times;
        }

        // Nothing more from this client port reaches the relay; packets for it still go out.
        internal void StopHearing(int clientPort)
        {
            lock (_lock)
                _portsNoLongerHeard.Add(clientPort);
        }

        // The next signed requests are answered 438 with a new nonce; -1 answers every one so.
        internal void AnswerStaleNonce(int times)
        {
            lock (_lock)
                StaleNonceAnswersLeft = times;
        }

        internal static ushort MethodAllocate => Allocate;
        internal static ushort MethodRefresh => Refresh;
        internal static ushort MethodCreatePermission => CreatePermission;
        internal static ushort MethodChannelBind => ChannelBind;
        internal static ushort MethodBinding => Binding;
        internal static ushort MethodSend => Send;

        // Everything a client sent, in the order it arrived: requests, indications and channel frames.
        internal List<Seen> AllSeen()
        {
            lock (_lock)
                return _seen.ToList();
        }

        internal List<Seen> SeenRequests(ushort method)
        {
            lock (_lock)
                return _seen.Where(seen => seen.Method == method && seen.Class == 0x000).ToList();
        }

        internal int Count(ushort method, ushort messageClass)
        {
            lock (_lock)
                return _seen.Count(seen => seen.Method == method && seen.Class == messageClass);
        }

        internal int ChannelFramesFrom(int clientPort)
        {
            lock (_lock)
                return _seen.Count(seen => seen.Method == 0xFFFF && seen.ClientPort == clientPort);
        }

        // Everything one relay client sent on to someone through the relay: channel frames and send indications.
        internal int PassedOnFrom(int clientPort)
        {
            lock (_lock)
                return _seen.Count(seen => seen.ClientPort == clientPort && (seen.Method == 0xFFFF || (seen.Method == Send && seen.Class == 0x010)));
        }

        internal int Reservations
        {
            get { lock (_lock) return _reservations.Count(each => each.ExpiresAt > DateTime.UtcNow); }
        }

        internal int Releases
        {
            get { lock (_lock) return _seen.Count(seen => seen.Method == Refresh && seen.Class == 0x000 && seen.Signed && seen.LifetimeAsked == 0); }
        }

        // Forgets every reservation, as a relay that restarted would: the next renewal is 437.
        internal void ForgetEveryReservation()
        {
            lock (_lock)
                _reservations.Clear();
        }

        internal void ReleaseHeldAnswers()
        {
            HoldReservationAnswers = false;
            List<KeyValuePair<byte[], IPEndPoint>> held;
            lock (_lock)
            {
                held = _heldAnswers.ToList();
                _heldAnswers.Clear();
            }
            foreach (KeyValuePair<byte[], IPEndPoint> answer in held)
                SendTo(answer.Key, answer.Value);
        }

        private string CurrentNonce => "nonce-" + _nonceNumber;

        private void Serve(UdpClient socket)
        {
            while (!_stopped)
            {
                IPEndPoint from = new IPEndPoint(socket.Client.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                byte[] packet;
                try
                {
                    packet = socket.Receive(ref from);
                    if (IgnoresUdp || (IgnoresUdpOverIpv4 && socket == _socket))
                        continue;
                }
                catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                try
                {
                    Handle(packet, from);
                }
                catch (Exception failure)
                {
                    UnityEngine.Debug.LogError("Fake relay server: " + failure);
                }
            }
        }

        private void Handle(byte[] packet, IPEndPoint from)
        {
            if (Ignores(from))
                return;
            bool overIpv6 = from.AddressFamily == AddressFamily.InterNetworkV6;
            lock (_lock)
            {
                if (_portsNoLongerHeard.Contains(from.Port))
                    return;
                if (!_portsHeard.ContainsKey(from.Port))
                    _portsHeard[from.Port] = _portsHeard.Count + 1;
                if (AnswersOnlyPortsHeard != null && !AnswersOnlyPortsHeard.Contains(_portsHeard[from.Port]))
                    return;
            }
            if (packet.Length >= 4 && (packet[0] & 0xC0) == 0x40)
            {
                ushort channel = (ushort)((packet[0] << 8) | packet[1]);
                int length = (packet[2] << 8) | packet[3];
                lock (_lock)
                {
                    _seen.Add(new Seen { Method = 0xFFFF, ClientPort = from.Port, OverIpv6 = overIpv6, At = DateTime.UtcNow });
                    if (Silent)
                        return;
                    Reservation sender = ReservationOf(from);
                    if (sender == null || !sender.Channels.TryGetValue(channel, out KeyValuePair<IPEndPoint, DateTime> bound) || bound.Value < DateTime.UtcNow)
                        return;
                    Relay(sender, bound.Key, packet.Skip(4).Take(length).ToArray());
                }
                return;
            }
            if (packet.Length < 20 || Read32(packet, 4) != MagicCookie)
                return;
            ushort type = (ushort)((packet[0] << 8) | packet[1]);
            ushort method = (ushort)(type & 0x000F);
            ushort messageClass = (ushort)(type & 0x0110);
            byte[] transaction = packet.Skip(8).Take(12).ToArray();
            Dictionary<ushort, byte[]> attributes = Attributes(packet, out int integrityAt);
            bool signed = attributes.ContainsKey(Username) && integrityAt >= 0;
            uint? lifetimeAsked = attributes.TryGetValue(Lifetime, out byte[] lifetime) ? Read32(lifetime, 0) : (uint?)null;
            lock (_lock)
                _seen.Add(new Seen { Method = method, Class = messageClass, Signed = signed, Transaction = Hex(transaction), LifetimeAsked = lifetimeAsked, ClientPort = from.Port, OverIpv6 = overIpv6, At = DateTime.UtcNow });
            if (Silent)
                return;

            if (messageClass == 0x010)
            {
                if (method == Send)
                    Relayed(from, attributes);
                return;
            }
            if (messageClass != 0x000)
                return;
            if (method == Binding)
            {
                if (AnswersBindings)
                    SendTo(Answer(type, transaction, 0x0100, MappedTo(from), null), from);
                return;
            }
            lock (_lock)
            {
                if (signed && _dropsLeft.TryGetValue(method, out int drops) && drops > 0)
                {
                    _dropsLeft[method] = drops - 1;
                    return;
                }
            }
            if (!signed)
            {
                SendTo(Error(type, transaction, 401, "Unauthorized", withChallenge: true), from);
                return;
            }
            string username = Encoding.UTF8.GetString(attributes[Username]);
            byte[] key = Key(username, Password);
            if (!SignatureHolds(packet, integrityAt, key))
            {
                SendTo(Error(type, transaction, 401, "Unauthorized", withChallenge: true), from);
                return;
            }
            lock (_lock)
            {
                if (StaleNonceAnswersLeft != 0)
                {
                    if (StaleNonceAnswersLeft > 0)
                        StaleNonceAnswersLeft--;
                    _nonceNumber++;
                    SendTo(Error(type, transaction, 438, "Stale Nonce", withChallenge: true), from);
                    return;
                }
                if (attributes.TryGetValue(Nonce, out byte[] nonce) && Encoding.UTF8.GetString(nonce) != CurrentNonce)
                {
                    SendTo(Error(type, transaction, 438, "Stale Nonce", withChallenge: true), from);
                    return;
                }
            }
            byte[] answer = AnswerTo(method, type, transaction, attributes, from, key);
            if (answer == null)
                return;
            if (method == Allocate && HoldReservationAnswers)
            {
                lock (_lock)
                    _heldAnswers.Add(new KeyValuePair<byte[], IPEndPoint>(answer, from));
                return;
            }
            SendTo(answer, from);
        }

        private byte[] AnswerTo(ushort method, ushort type, byte[] transaction, Dictionary<ushort, byte[]> attributes, IPEndPoint from, byte[] key)
        {
            lock (_lock)
            {
                Reservation reservation = ReservationOf(from);
                string id = Hex(transaction);
                switch (method)
                {
                    case Allocate:
                    {
                        if (RefuseReservationsWith != 0)
                            return Error(type, transaction, RefuseReservationsWith, "Refused", false, key);
                        if (reservation != null && reservation.AllocateTransaction != id)
                            return Error(type, transaction, 437, "Allocation Mismatch", false, key);
                        if (reservation == null)
                        {
                            string ip = RelayIps[_reservationsMade++ % RelayIps.Count];
                            reservation = new Reservation { Client = from, Ip = ip, RelayedPort = _nextPort++, AllocateTransaction = id, ExpiresAt = DateTime.UtcNow + ReservationLifetime };
                            _reservations.Add(reservation);
                        }
                        if (ForgeNextReservation)
                        {
                            ForgeNextReservation = false;
                            byte[] forged = Answer(type, transaction, 0x0100, new[] { Attr(XorRelayedAddress, Address(new IPEndPoint(IPAddress.Parse("198.51.100.66"), 4444))) }, Key("someone-else", "forged"));
                            SendTo(forged, from);
                        }
                        return Answer(type, transaction, 0x0100, new[] { Attr(XorRelayedAddress, Address(new IPEndPoint(IPAddress.Parse(reservation.Ip), reservation.RelayedPort))) }
                            .Concat(MappedTo(from))
                            .Concat(new[] { Attr(Lifetime, Be32((uint)ReservationLifetime.TotalSeconds)) }).ToArray(), key);
                    }
                    case Refresh:
                    {
                        if (reservation == null)
                            return Error(type, transaction, 437, "Allocation Mismatch", false, key);
                        uint asked = attributes.TryGetValue(Lifetime, out byte[] value) ? Read32(value, 0) : 600;
                        if (asked == 0)
                        {
                            _reservations.Remove(reservation);
                            return Answer(type, transaction, 0x0100, new[] { Attr(Lifetime, Be32(0)) }, key);
                        }
                        reservation.ExpiresAt = DateTime.UtcNow + ReservationLifetime;
                        return Answer(type, transaction, 0x0100, new[] { Attr(Lifetime, Be32((uint)ReservationLifetime.TotalSeconds)) }, key);
                    }
                    case CreatePermission:
                    {
                        if (reservation == null)
                            return Error(type, transaction, 437, "Allocation Mismatch", false, key);
                        IPEndPoint peer = ReadAddress(attributes[XorPeerAddress]);
                        if (RefuseOpeningsNamingNoCode)
                            return Answer(type, transaction, 0x0110, new byte[0][], key);
                        if (RefusedOpenings.Contains(peer.Address.ToString()))
                            return Error(type, transaction, 403, "Forbidden IP", false, key);
                        reservation.Openings[peer.Address.ToString()] = DateTime.UtcNow + OpeningLifetime;
                        return Answer(type, transaction, 0x0100, new byte[0][], key);
                    }
                    case ChannelBind:
                    {
                        if (reservation == null)
                            return Error(type, transaction, 437, "Allocation Mismatch", false, key);
                        ushort number = (ushort)((attributes[ChannelNumber][0] << 8) | attributes[ChannelNumber][1]);
                        IPEndPoint peer = ReadAddress(attributes[XorPeerAddress]);
                        if (number < 0x4000 || number > 0x4FFF)
                            return Error(type, transaction, 400, "Bad Request", false, key);
                        foreach (KeyValuePair<ushort, KeyValuePair<IPEndPoint, DateTime>> channel in reservation.Channels)
                        {
                            bool sameNumber = channel.Key == number;
                            bool samePeer = channel.Value.Key.Equals(peer);
                            if (sameNumber != samePeer)
                                return Error(type, transaction, 400, "Bad Request", false, key);
                        }
                        reservation.Channels[number] = new KeyValuePair<IPEndPoint, DateTime>(peer, DateTime.UtcNow + ChannelLifetime);
                        reservation.Openings[peer.Address.ToString()] = DateTime.UtcNow + OpeningLifetime;
                        return Answer(type, transaction, 0x0100, new byte[0][], key);
                    }
                }
                return Error(type, transaction, 400, "Bad Request", false, key);
            }
        }

        private void Relayed(IPEndPoint from, Dictionary<ushort, byte[]> attributes)
        {
            if (!attributes.TryGetValue(XorPeerAddress, out byte[] peerValue) || !attributes.TryGetValue(DataValue, out byte[] data))
                return;
            lock (_lock)
            {
                Reservation sender = ReservationOf(from);
                if (sender != null)
                    Relay(sender, ReadAddress(peerValue), data);
            }
        }

        // Data from one reservation to another: the sender needs an opening to the peer's IP, the receiver one to the sender's.
        private void Relay(Reservation sender, IPEndPoint peer, byte[] data)
        {
            DateTime now = DateTime.UtcNow;
            if (sender.ExpiresAt < now || !sender.Openings.TryGetValue(peer.Address.ToString(), out DateTime open) || open < now)
                return;
            Reservation receiver = _reservations.FirstOrDefault(each => each.Ip == peer.Address.ToString() && each.RelayedPort == peer.Port && each.ExpiresAt > now);
            if (receiver == null || !receiver.Openings.TryGetValue(sender.Ip, out DateTime receiverOpen) || receiverOpen < now)
                return;
            IPEndPoint senderAddress = new IPEndPoint(IPAddress.Parse(sender.Ip), sender.RelayedPort);
            foreach (KeyValuePair<ushort, KeyValuePair<IPEndPoint, DateTime>> channel in receiver.Channels)
            {
                if (channel.Value.Key.Equals(senderAddress) && channel.Value.Value > now)
                {
                    byte[] frame = new byte[4 + data.Length + (4 - data.Length % 4) % 4];
                    frame[0] = (byte)(channel.Key >> 8);
                    frame[1] = (byte)channel.Key;
                    frame[2] = (byte)(data.Length >> 8);
                    frame[3] = (byte)data.Length;
                    Array.Copy(data, 0, frame, 4, data.Length);
                    SendTo(frame, receiver.Client);
                    return;
                }
            }
            byte[] indication = Answer((ushort)(Data | 0x0010), NewTransaction(), 0x0010, new[] { Attr(XorPeerAddress, Address(senderAddress)), Attr(DataValue, data) }, null, fingerprint: false);
            SendTo(indication, receiver.Client);
        }

        private Reservation ReservationOf(IPEndPoint client)
            => _reservations.FirstOrDefault(each => each.Client.Equals(client) && each.ExpiresAt > DateTime.UtcNow);

        private byte[] Error(ushort type, byte[] transaction, int code, string words, bool withChallenge, byte[] key = null)
        {
            List<byte[]> attributes = new List<byte[]>();
            byte[] value = new byte[4 + Encoding.UTF8.GetByteCount(words)];
            value[2] = (byte)(code / 100);
            value[3] = (byte)(code % 100);
            Encoding.UTF8.GetBytes(words, 0, words.Length, value, 4);
            attributes.Add(Attr(ErrorCode, value));
            if (withChallenge || code == 438)
            {
                attributes.Add(Attr(RealmAttribute, Encoding.UTF8.GetBytes(Realm)));
                attributes.Add(Attr(Nonce, Encoding.UTF8.GetBytes(CurrentNonce)));
            }
            return Answer(type, transaction, 0x0110, attributes.ToArray(), withChallenge ? null : key);
        }

        // An answer of the given class to a request's type, its attributes, signed when a key is given, and fingerprinted.
        private static byte[] Answer(ushort requestType, byte[] transaction, ushort answerClass, byte[][] attributes, byte[] key, bool fingerprint = true)
        {
            ushort type = (ushort)((requestType & ~0x0110) | answerClass);
            List<byte> body = new List<byte>();
            foreach (byte[] attribute in attributes)
                body.AddRange(attribute);
            if (key != null)
            {
                byte[] header = Header(type, body.Count + 24, transaction);
                using (HMACSHA1 hmac = new HMACSHA1(key))
                    body.AddRange(Attr(MessageIntegrity, hmac.ComputeHash(header.Concat(body).ToArray())));
            }
            if (fingerprint)
            {
                byte[] header = Header(type, body.Count + 8, transaction);
                uint crc = Crc(header.Concat(body).ToArray()) ^ 0x5354554E;
                body.AddRange(Attr(Fingerprint, Be32(crc)));
            }
            return Header(type, body.Count, transaction).Concat(body).ToArray();
        }

        private static byte[] Header(ushort type, int length, byte[] transaction)
        {
            byte[] header = new byte[20];
            header[0] = (byte)(type >> 8);
            header[1] = (byte)type;
            header[2] = (byte)(length >> 8);
            header[3] = (byte)length;
            Array.Copy(Be32(MagicCookie), 0, header, 4, 4);
            Array.Copy(transaction, 0, header, 8, 12);
            return header;
        }

        private static byte[] Attr(ushort kind, byte[] value)
        {
            byte[] attribute = new byte[4 + value.Length + (4 - value.Length % 4) % 4];
            attribute[0] = (byte)(kind >> 8);
            attribute[1] = (byte)kind;
            attribute[2] = (byte)(value.Length >> 8);
            attribute[3] = (byte)value.Length;
            Array.Copy(value, 0, attribute, 4, value.Length);
            return attribute;
        }

        private static Dictionary<ushort, byte[]> Attributes(byte[] packet, out int integrityAt)
        {
            Dictionary<ushort, byte[]> attributes = new Dictionary<ushort, byte[]>();
            integrityAt = -1;
            int end = Math.Min(packet.Length, 20 + ((packet[2] << 8) | packet[3]));
            int position = 20;
            while (position + 4 <= end)
            {
                ushort kind = (ushort)((packet[position] << 8) | packet[position + 1]);
                int size = (packet[position + 2] << 8) | packet[position + 3];
                if (kind == MessageIntegrity)
                    integrityAt = position;
                if (!attributes.ContainsKey(kind))
                    attributes[kind] = packet.Skip(position + 4).Take(size).ToArray();
                position += 4 + size + (4 - size % 4) % 4;
            }
            return attributes;
        }

        private static bool SignatureHolds(byte[] packet, int integrityAt, byte[] key)
        {
            byte[] signed = packet.Take(integrityAt).ToArray();
            int length = integrityAt - 20 + 24;
            signed[2] = (byte)(length >> 8);
            signed[3] = (byte)length;
            using (HMACSHA1 hmac = new HMACSHA1(key))
                return hmac.ComputeHash(signed).SequenceEqual(packet.Skip(integrityAt + 4).Take(20));
        }

        private static byte[] Key(string username, string password)
        {
            using (MD5 md5 = MD5.Create())
                return md5.ComputeHash(Encoding.UTF8.GetBytes(username + ":" + Realm + ":" + password));
        }

        private static byte[] Address(IPEndPoint endPoint)
        {
            byte[] value = new byte[8];
            value[1] = 0x01;
            int port = endPoint.Port ^ (int)(MagicCookie >> 16);
            value[2] = (byte)(port >> 8);
            value[3] = (byte)port;
            byte[] address = endPoint.Address.GetAddressBytes();
            byte[] cookie = Be32(MagicCookie);
            for (int i = 0; i < 4; i++)
                value[4 + i] = (byte)(address[i] ^ cookie[i]);
            return value;
        }

        private static IPEndPoint ReadAddress(byte[] value)
        {
            int port = ((value[2] << 8) | value[3]) ^ (int)(MagicCookie >> 16);
            byte[] cookie = Be32(MagicCookie);
            byte[] address = new byte[4];
            for (int i = 0; i < 4; i++)
                address[i] = (byte)(value[4 + i] ^ cookie[i]);
            return new IPEndPoint(new IPAddress(address), port);
        }

        private static byte[] Be32(uint value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        private static uint Read32(byte[] data, int at) => ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

        private static byte[] NewTransaction() => Guid.NewGuid().ToByteArray().Take(12).ToArray();

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);

        private static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }

        private void SendTo(byte[] bytes, IPEndPoint to)
        {
            TimeSpan delay = AnswerDelay;
            if (delay > TimeSpan.Zero)
            {
                System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ => SendNow(bytes, to));
                return;
            }
            SendNow(bytes, to);
        }

        private void SendNow(byte[] bytes, IPEndPoint to)
        {
            StreamClient client;
            lock (_lock)
                _streams.TryGetValue(to, out client);
            if (client != null)
            {
                try
                {
                    lock (client.WriteGate)
                        client.Stream.Write(bytes, 0, bytes.Length);
                }
                catch (Exception)
                {
                    // A client already gone.
                }
                return;
            }
            if (IgnoresUdp)
                return;
            try
            {
                (to.AddressFamily == AddressFamily.InterNetworkV6 ? _socket6 : _socket).Send(bytes, bytes.Length, to);
            }
            catch (Exception)
            {
                // A client already gone.
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _socket.Close();
            _socket6?.Close();
            _tcp?.Stop();
            _tls?.Stop();
            _tcp6?.Stop();
            _tls6?.Stop();
            CloseEveryStream();
            _thread.Join(1000);
            _thread6?.Join(1000);
        }
    }
}
