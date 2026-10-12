using System;
using System.Net;
using System.Net.Sockets;

namespace Flock.Providers
{
    /// <summary>Opens the UDP sockets that reach servers and other players: dual-stack (IPv6 carrying IPv4 too) where the platform allows, plain IPv4 where it does not. On a phone on mobile data, IPv4 sockets never heard Cloudflare's STUN server while dual-stack ones always did (measured 2026-10). Addresses stay IPv4 everywhere else: one is sent to in its IPv6 form only on a dual-stack socket, and what is heard is read back as IPv4.</summary>
    internal static class FlockUdpSockets
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
            if (!_dualStackRefused)
            {
                Socket dualStack = null;
                try
                {
                    if (DualStackRefusedForTesting)
                        throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                    dualStack = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    dualStack.DualMode = true;
                }
                catch (Exception refused) when (IsRefusal(refused))
                {
                    // No IPv6 here, or no IPv4 through it: plain IPv4 sockets, as before, for the rest of the launch.
                    dualStack?.Close();
                    dualStack = null;
                    _dualStackRefused = true;
                }
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
            }
            return OpenIpv4();
        }

        /// <summary>A socket from <see cref="Open"/> connected to <paramref name="server"/>; a dual-stack one that cannot connect to it is replaced by a plain IPv4 one.</summary>
        internal static Socket OpenConnectedTo(IPEndPoint server)
        {
            Socket socket = Open();
            try
            {
                if (DualStackConnectRefusedForTesting && socket.AddressFamily == AddressFamily.InterNetworkV6)
                    throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                socket.Connect(Toward(socket, server));
                return socket;
            }
            catch (SocketException) when (socket.AddressFamily == AddressFamily.InterNetworkV6)
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
