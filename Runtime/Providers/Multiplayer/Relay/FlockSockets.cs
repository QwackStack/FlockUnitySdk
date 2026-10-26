using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Flock.Providers
{
    /// <summary>Opens the sockets that reach servers and other players (UDP, and TCP for a relay reached over TCP or TLS): dual-stack (IPv6 carrying IPv4 too) where the platform allows, plain IPv4 where it does not. On a phone on mobile data, IPv4 sockets never heard Cloudflare's STUN server while dual-stack ones always did (measured 2026-10). Addresses stay IPv4 everywhere else: one is sent to in its IPv6 form only on a dual-stack socket, and what is heard is read back as IPv4.</summary>
    internal static class FlockSockets
    {
        /// <summary>Makes a dual-stack socket fail to be made, as on a platform without IPv6.</summary>
        internal static volatile bool DualStackRefusedForTesting;

        /// <summary>Makes a dual-stack socket's connect to an IPv4 address fail, as on a platform that takes IPv6 sockets but not IPv4 through them.</summary>
        internal static volatile bool DualStackConnectRefusedForTesting;

        // The platform refused a dual-stack socket once: it will again, so later sockets go straight to IPv4.
        private static volatile bool _dualStackRefused;

        internal static bool RemembersARefusalForTesting => _dualStackRefused;

        /// <summary>Clears both stand-ins and a refusal remembered from one.</summary>
        internal static void ResetForTesting()
        {
            DualStackRefusedForTesting = false;
            DualStackConnectRefusedForTesting = false;
            _dualStackRefused = false;
        }

        /// <summary>A UDP socket bound to any address and a port of the system's choosing.</summary>
        internal static Socket Open()
        {
            Socket dualStack = NewDualStack(SocketType.Dgram, ProtocolType.Udp);
            if (dualStack != null)
            {
                try
                {
                    dualStack.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    return dualStack;
                }
                catch (SocketException)
                {
                    // A bind that failed this once says nothing of the platform: this socket alone is plain IPv4.
                    dualStack.Close();
                }
            }
            return OpenIpv4();
        }

        /// <summary>A TCP socket connected to <paramref name="server"/> within <paramref name="wait"/>, dual-stack where the platform allows; to an IPv4 server, a dual-stack one that cannot reach it for any reason but the server's own (a refusal, or no answer) is replaced by a plain IPv4 one. Throws a SocketException (TimedOut when nothing answered in time; AddressFamilyNotSupported for an IPv6 server on a platform without IPv6), or OperationCanceledException. Blocks, so it runs off the main thread.</summary>
        internal static Socket OpenStreamConnectedTo(IPEndPoint server, TimeSpan wait, CancellationToken cancellationToken)
        {
            Socket dualStack = NewDualStack(SocketType.Stream, ProtocolType.Tcp);
            if (dualStack == null && server.AddressFamily != AddressFamily.InterNetwork)
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            if (dualStack != null)
            {
                try
                {
                    if (DualStackConnectRefusedForTesting && server.AddressFamily == AddressFamily.InterNetwork)
                        throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                    Connect(dualStack, Toward(dualStack, server), wait, cancellationToken);
                    return dualStack;
                }
                catch (SocketException refused) when (server.AddressFamily == AddressFamily.InterNetwork && refused.SocketErrorCode != SocketError.TimedOut
                                                      && refused.SocketErrorCode != SocketError.ConnectionRefused)
                {
                    dualStack.Close();
                }
                catch
                {
                    dualStack.Close();
                    throw;
                }
            }
            Socket ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                Connect(ipv4, server, wait, cancellationToken);
            }
            catch
            {
                ipv4.Close();
                throw;
            }
            return ipv4;
        }

        // The caller closes the socket on a throw, which ends a connect still under way.
        private static void Connect(Socket socket, IPEndPoint server, TimeSpan wait, CancellationToken cancellationToken)
        {
            IAsyncResult connecting = socket.BeginConnect(server, null, null);
            int woken = WaitHandle.WaitAny(new[] { connecting.AsyncWaitHandle, cancellationToken.WaitHandle }, wait);
            cancellationToken.ThrowIfCancellationRequested();
            if (woken == WaitHandle.WaitTimeout)
                throw new SocketException((int)SocketError.TimedOut);
            socket.EndConnect(connecting);
        }

        // A dual-stack socket of this kind, or null where the platform refused one; a refusal is kept for the rest of the launch.
        private static Socket NewDualStack(SocketType type, ProtocolType protocol)
        {
            if (_dualStackRefused)
                return null;
            Socket dualStack = null;
            try
            {
                if (DualStackRefusedForTesting)
                    throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                dualStack = new Socket(AddressFamily.InterNetworkV6, type, protocol);
                dualStack.DualMode = true;
                return dualStack;
            }
            catch (Exception refused) when (IsRefusal(refused))
            {
                // No IPv6 here, or no IPv4 through it: plain IPv4 sockets, as before, for the rest of the launch.
                dualStack?.Close();
                _dualStackRefused = true;
                return null;
            }
        }

        /// <summary>A socket from <see cref="Open"/> connected to <paramref name="server"/>; to an IPv4 server, a dual-stack one that cannot connect to it is replaced by a plain IPv4 one. An IPv6 server on a platform without IPv6 is a SocketException (AddressFamilyNotSupported).</summary>
        internal static Socket OpenConnectedTo(IPEndPoint server)
        {
            Socket socket = Open();
            if (server.AddressFamily != AddressFamily.InterNetwork && socket.AddressFamily != AddressFamily.InterNetworkV6)
            {
                socket.Close();
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            }
            try
            {
                if (DualStackConnectRefusedForTesting && socket.AddressFamily == AddressFamily.InterNetworkV6 && server.AddressFamily == AddressFamily.InterNetwork)
                    throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                socket.Connect(Toward(socket, server));
                return socket;
            }
            catch (SocketException) when (socket.AddressFamily == AddressFamily.InterNetworkV6 && server.AddressFamily == AddressFamily.InterNetwork)
            {
                socket.Close();
            }
            catch
            {
                socket.Close();
                throw;
            }
            Socket ipv4 = OpenIpv4();
            try
            {
                ipv4.Connect(server);
            }
            catch
            {
                ipv4.Close();
                throw;
            }
            return ipv4;
        }

        /// <summary>A UdpClient, dual-stack where the platform allows; it binds on its first send.</summary>
        internal static UdpClient OpenClient()
        {
            if (!_dualStackRefused)
            {
                UdpClient dualStack = null;
                try
                {
                    if (DualStackRefusedForTesting)
                        throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                    dualStack = new UdpClient(AddressFamily.InterNetworkV6);
                    dualStack.Client.DualMode = true;
                    return dualStack;
                }
                catch (Exception refused) when (IsRefusal(refused))
                {
                    dualStack?.Close();
                    _dualStackRefused = true;
                }
            }
            return new UdpClient(AddressFamily.InterNetwork);
        }

        /// <summary><paramref name="address"/> as <paramref name="socket"/> sends to it: in its IPv6 form on a dual-stack socket.</summary>
        internal static IPEndPoint Toward(Socket socket, IPEndPoint address)
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && address.AddressFamily == AddressFamily.InterNetwork)
                return new IPEndPoint(address.Address.MapToIPv6(), address.Port);
            return address;
        }

        /// <summary>Where something was heard from, read as IPv4 when a dual-stack socket heard an IPv4 sender.</summary>
        internal static IPEndPoint AsHeard(EndPoint heard)
        {
            IPEndPoint from = (IPEndPoint)heard;
            if (from.AddressFamily == AddressFamily.InterNetworkV6 && from.Address.IsIPv4MappedToIPv6)
                return new IPEndPoint(from.Address.MapToIPv4(), from.Port);
            return from;
        }

        /// <summary>The "any sender" a receive on <paramref name="socket"/> fills in.</summary>
        internal static EndPoint AnySender(Socket socket) => new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        private static Socket OpenIpv4()
        {
            Socket ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                ipv4.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            catch
            {
                ipv4.Close();
                throw;
            }
            return ipv4;
        }

        // A platform without IPv6 throws a SocketException; one that cannot carry IPv4 through it, NotSupportedException (PlatformNotSupportedException among them).
        private static bool IsRefusal(Exception failure) => failure is SocketException || failure is NotSupportedException;
    }
}
