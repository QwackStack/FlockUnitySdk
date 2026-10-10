using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>This device's addresses for a direct connection: its LAN address, from the route toward the internet, and its public address, from one request to the STUN server Flock lists.</summary>
    internal sealed class FlockDirectAddresses
    {
        // A binding request answers in about 100 ms; a server silent this long is taken as out of reach.
        internal static readonly TimeSpan DefaultStunWait = TimeSpan.FromSeconds(2);
        private const int DefaultStunPort = 3478;
        private const uint StunMagicCookie = FlockRelayWire.MagicCookie;
        private const ushort BindingRequest = FlockRelayWire.BindingMethod | FlockRelayWire.RequestClass;
        private const ushort BindingSuccess = FlockRelayWire.BindingMethod | FlockRelayWire.SuccessClass;
        private const ushort MappedAddress = 0x0001;
        private const ushort XorMappedAddress = FlockRelayWire.XorMappedAddressAttribute;
        // Where the LAN address is routed toward when no STUN server is known: an address on the internet, sent nothing.
        private static readonly IPAddress InternetAddress = new IPAddress(new byte[] { 8, 8, 8, 8 });

        private readonly FlockClient _client;
        private readonly FlockMultiplayerSessionRequests _requests;
        // The STUN servers Flock lists, read once a launch; null until a read succeeds.
        private List<StunServer> _stunServers;
        private TimeSpan _stunWait = DefaultStunWait;
        private bool _toldPublicAddressUnknown;
        private Func<string, Task<IPAddress[]>> _lookUpForTesting;

        internal FlockDirectAddresses(FlockClient client, FlockMultiplayerSessionRequests requests)
        {
            _client = client;
            _requests = requests;
        }

        internal void SetStunWaitForTesting(TimeSpan wait) => _stunWait = wait;

        // Stands in for the operating system's name lookup, so a test can hold one that never answers.
        internal void SetNameLookUpForTesting(Func<string, Task<IPAddress[]>> lookUp) => _lookUpForTesting = lookUp;

        /// <summary>The LAN address and the public address, each null when it cannot be found; acts for <paramref name="signInNumber"/> the first time it reads the STUN servers.</summary>
        internal async Task<DeviceAddresses> FindAsync(int signInNumber, CancellationToken cancellationToken)
        {
            // A web player has no sockets: nothing to ask, no route to read.
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return new DeviceAddresses(null, null);
            List<StunServer> servers = await StunServersAsync(signInNumber, cancellationToken);
            IPAddress firstServer = null;
            string publicAddress = null;
            foreach (StunServer server in servers)
            {
                // A name lookup takes no token and can hang for as long as the network's resolver does, so it gets the STUN wait too.
                IPAddress serverAddress = await FlockNameLookup.FindIpv4Async(server.Host, _stunWait, _lookUpForTesting, cancellationToken);
                if (serverAddress == null)
                    continue;
                firstServer = firstServer ?? serverAddress;
                publicAddress = await AskPublicAddressAsync(new IPEndPoint(serverAddress, server.Port), cancellationToken);
                if (publicAddress != null)
                    break;
            }
            if (publicAddress == null && !_toldPublicAddressUnknown)
            {
                _toldPublicAddressUnknown = true;
                _client.Logger.LogWarning("This device's public address could not be found (no STUN server answered), so a direct connection offers its LAN address alone, which only players on the same network can reach.");
            }
            return new DeviceAddresses(LanAddressToward(firstServer ?? InternetAddress), publicAddress);
        }

        /// <summary>This device's public address, or null when no STUN server tells it.</summary>
        internal async Task<string> FindPublicAddressAsync(int signInNumber, CancellationToken cancellationToken)
        {
            DeviceAddresses found = await FindAsync(signInNumber, cancellationToken);
            return found.Public;
        }

        private async Task<List<StunServer>> StunServersAsync(int signInNumber, CancellationToken cancellationToken)
        {
            if (_stunServers != null)
                return _stunServers;
            RelayCredentialsRecord servers;
            try
            {
                servers = await _requests.RelayCredentialsAsync(signInNumber, cancellationToken);
            }
            catch (FlockException failure)
            {
                _client.Logger.LogWarning($"The STUN server could not be read from Flock: {failure.Message}. This device's public address stays unknown this time.");
                return new List<StunServer>();
            }
            List<StunServer> found = new List<StunServer>();
            foreach (IceServerRecord entry in servers.IceServers)
            {
                if (entry?.Urls == null)
                    continue;
                foreach (string url in entry.Urls)
                {
                    StunServer server = StunServer.Parse(url);
                    if (server != null)
                        found.Add(server);
                }
            }
            _stunServers = found;
            return found;
        }

        // One binding request; its answer names the address and port the server saw this device's request come from.
        private async Task<string> AskPublicAddressAsync(IPEndPoint server, CancellationToken cancellationToken)
        {
            byte[] transaction = new byte[12];
            Array.Copy(Guid.NewGuid().ToByteArray(), transaction, 12);
            byte[] request = new byte[20];
            FlockRelayWire.WriteUInt16(request, 0, BindingRequest);
            FlockRelayWire.WriteUInt32(request, 4, StunMagicCookie);
            Array.Copy(transaction, 0, request, 8, 12);

            using (UdpClient udp = new UdpClient(AddressFamily.InterNetwork))
            using (CancellationTokenSource stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    await udp.SendAsync(request, request.Length, server);
                }
                catch (SocketException)
                {
                    return null;
                }
                Task<UdpReceiveResult> receiving = udp.ReceiveAsync();
                // Closing the socket ends a receive nobody waits for any more; its failure is read here, not reported as unobserved.
                FlockMultiplayerSessions.LetRun(receiving);
                Task timeUp = FlockWaiting.DelayAsync(_stunWait, stopWaiting.Token);
                Task first = await Task.WhenAny(receiving, timeUp);
                stopWaiting.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (first != receiving || receiving.IsFaulted)
                    return null;
                return PublicAddressInStunAnswer(receiving.Result.Buffer, transaction);
            }
        }

        // The XOR-MAPPED-ADDRESS (or the older MAPPED-ADDRESS) of a binding success that answers this transaction; null for anything else.
        internal static string PublicAddressInStunAnswer(byte[] answer, byte[] transaction)
        {
            if (answer == null || answer.Length < 20 || FlockRelayWire.ReadUInt16(answer, 0) != BindingSuccess || FlockRelayWire.ReadUInt32(answer, 4) != StunMagicCookie)
                return null;
            for (int i = 0; i < 12; i++)
            {
                if (answer[8 + i] != transaction[i])
                    return null;
            }
            int end = Math.Min(answer.Length, 20 + FlockRelayWire.ReadUInt16(answer, 2));
            string plain = null;
            int position = 20;
            while (position + 4 <= end)
            {
                ushort kind = FlockRelayWire.ReadUInt16(answer, position);
                int length = FlockRelayWire.ReadUInt16(answer, position + 2);
                int value = position + 4;
                if (value + length > end)
                    break;
                // Family 0x01 is IPv4: one reserved byte, the family, the port, then four address bytes.
                if (length >= 8 && answer[value + 1] == 0x01)
                {
                    if (kind == XorMappedAddress)
                    {
                        uint masked = FlockRelayWire.ReadUInt32(answer, value + 4) ^ StunMagicCookie;
                        return new IPAddress(new[] { (byte)(masked >> 24), (byte)(masked >> 16), (byte)(masked >> 8), (byte)masked }).ToString();
                    }
                    if (kind == MappedAddress && plain == null)
                        plain = new IPAddress(new[] { answer[value + 4], answer[value + 5], answer[value + 6], answer[value + 7] }).ToString();
                }
                position = value + length + ((4 - length % 4) % 4);
            }
            return plain;
        }

        // The address this device sends from toward the internet; a UDP socket picks its route on Connect and sends nothing.
        private static string LanAddressToward(IPAddress target)
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(target, DefaultStunPort));
                    IPEndPoint local = socket.LocalEndPoint as IPEndPoint;
                    if (local == null || local.Address.Equals(IPAddress.Any))
                        return null;
                    return local.Address.ToString();
                }
            }
            catch (SocketException)
            {
                return null;
            }
        }

        internal sealed class DeviceAddresses
        {
            internal DeviceAddresses(string lan, string publicAddress)
            {
                Lan = lan;
                Public = publicAddress;
            }

            internal string Lan { get; }
            internal string Public { get; }
        }

        // A "stun:host:port" entry; TLS ("stuns:") and IPv6 hosts are left out, since the request is plain UDP over IPv4.
        private sealed class StunServer
        {
            private StunServer(string host, int port)
            {
                Host = host;
                Port = port;
            }

            internal string Host { get; }
            internal int Port { get; }

            internal static StunServer Parse(string url)
            {
                if (string.IsNullOrEmpty(url) || !url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                    return null;
                string rest = url.Substring(5);
                int query = rest.IndexOf('?');
                if (query >= 0)
                    rest = rest.Substring(0, query);
                if (rest.Length == 0 || rest.StartsWith("[", StringComparison.Ordinal))
                    return null;
                int colon = rest.LastIndexOf(':');
                if (colon < 0)
                    return new StunServer(rest, DefaultStunPort);
                if (!int.TryParse(rest.Substring(colon + 1), out int port) || port < 1 || port > 65535 || colon == 0)
                    return null;
                return new StunServer(rest.Substring(0, colon), port);
            }
        }
    }
}
