using System;
using System.Collections.Generic;
using System.Net;

namespace Flock.Providers
{
    /// <summary>How a relay connection reaches its server: UDP, or TCP or TLS for a network that blocks UDP to the relay.</summary>
    internal enum FlockRelayTransport
    {
        Udp,
        Tcp,
        Tls,
    }

    /// <summary>One way in to a relay server (its address and transport) and the login Flock minted for it, read from Flock's answer of servers to connect through.</summary>
    internal sealed class FlockRelayLogin
    {
        private const int DefaultPort = 3478;
        private const int DefaultTlsPort = 5349;

        internal FlockRelayLogin(string host, int port, string username, string password, FlockRelayTransport transport = FlockRelayTransport.Udp, bool overIpv6 = false)
        {
            Host = host;
            Port = port;
            Username = username;
            Password = password;
            Transport = transport;
            OverIpv6 = overIpv6;
        }

        internal string Host { get; }
        internal int Port { get; }
        internal string Username { get; }
        internal string Password { get; }
        internal FlockRelayTransport Transport { get; }

        /// <summary>True when the way reaches the server at its IPv6 address, for a network with IPv6 alone; the relay still gives an IPv4 relay address.</summary>
        internal bool OverIpv6 { get; }

        /// <summary>The way in as a message names it, such as "turn.example.com:5349 over TLS" or "turn.example.com:3478 over UDP on IPv6".</summary>
        internal string Way => $"{this} over {Transport.ToString().ToUpperInvariant()}{(OverIpv6 ? " on IPv6" : "")}";

        /// <summary>Every relay entry's ways in, in Flock's order (more than one server while Flock moves between relays); STUN entries, entries without a login and ways this client cannot use are left out.</summary>
        internal static List<FlockRelayLogin> InAnswer(RelayCredentialsRecord answer)
        {
            List<FlockRelayLogin> logins = new List<FlockRelayLogin>();
            if (answer?.IceServers == null)
                return logins;
            foreach (IceServerRecord entry in answer.IceServers)
            {
                if (entry?.Urls == null || string.IsNullOrEmpty(entry.Username) || string.IsNullOrEmpty(entry.Credential))
                    continue;
                foreach (string url in entry.Urls)
                {
                    if (TryReadServer(url, out string host, out int port, out FlockRelayTransport transport))
                        logins.Add(new FlockRelayLogin(host, port, entry.Username, entry.Credential, transport));
                }
            }
            return logins;
        }

        // UDP never holds a packet back for a lost one, so it comes first on either family: a phone hotspot dropped UDP over IPv4 alone and
        // carried it over IPv6 (measured 2026-10-25). TCP and TLS are for a network that blocks UDP; IPv6 for one that has IPv6 alone.
        private static readonly (FlockRelayTransport Transport, bool OverIpv6)[] TheOrder =
        {
            (FlockRelayTransport.Udp, false), (FlockRelayTransport.Udp, true),
            (FlockRelayTransport.Tcp, false), (FlockRelayTransport.Tls, false),
            (FlockRelayTransport.Tcp, true), (FlockRelayTransport.Tls, true),
        };

        /// <summary><paramref name="logins"/> as they are tried: UDP over IPv4, UDP over IPv6, then TCP and TLS over IPv4, then over IPv6, each in the order given; a server given as an IPv4 address has no IPv6 way.</summary>
        internal static List<FlockRelayLogin> InTheOrderTried(IReadOnlyList<FlockRelayLogin> logins)
        {
            List<FlockRelayLogin> ordered = new List<FlockRelayLogin>(logins.Count * 2);
            foreach ((FlockRelayTransport transport, bool overIpv6) in TheOrder)
            {
                foreach (FlockRelayLogin login in logins)
                {
                    if (login.Transport != transport)
                        continue;
                    if (!overIpv6)
                        ordered.Add(login);
                    else if (!IPAddress.TryParse(login.Host, out _))
                        ordered.Add(new FlockRelayLogin(login.Host, login.Port, login.Username, login.Password, login.Transport, true));
                }
            }
            return ordered;
        }

        /// <summary>A "turn:host[:port][?transport=udp|tcp]" or "turns:host[:port][?transport=tcp]" address (TLS); false for DTLS ("turns:" over UDP), an IPv6 host or anything unreadable, since this client speaks UDP, TCP and TLS over IPv4.</summary>
        internal static bool TryReadServer(string url, out string host, out int port, out FlockRelayTransport transport)
        {
            host = null;
            port = 0;
            transport = FlockRelayTransport.Udp;
            if (string.IsNullOrEmpty(url))
                return false;
            bool overTls;
            string rest;
            if (url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
            {
                overTls = true;
                rest = url.Substring(6);
            }
            else if (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            {
                overTls = false;
                rest = url.Substring(5);
            }
            else
                return false;
            string asked = null;
            int query = rest.IndexOf('?');
            if (query >= 0)
            {
                foreach (string part in rest.Substring(query + 1).Split('&'))
                {
                    if (part.StartsWith("transport=", StringComparison.OrdinalIgnoreCase))
                        asked = part.Substring("transport=".Length);
                }
                rest = rest.Substring(0, query);
            }
            if (overTls)
            {
                if (asked != null && !string.Equals(asked, "tcp", StringComparison.OrdinalIgnoreCase))
                    return false;
                transport = FlockRelayTransport.Tls;
            }
            else if (asked == null || string.Equals(asked, "udp", StringComparison.OrdinalIgnoreCase))
                transport = FlockRelayTransport.Udp;
            else if (string.Equals(asked, "tcp", StringComparison.OrdinalIgnoreCase))
                transport = FlockRelayTransport.Tcp;
            else
                return false;
            if (rest.Length == 0 || rest.StartsWith("[", StringComparison.Ordinal))
                return false;
            int colon = rest.LastIndexOf(':');
            if (colon < 0)
            {
                host = rest;
                port = overTls ? DefaultTlsPort : DefaultPort;
                return true;
            }
            if (colon == 0 || !int.TryParse(rest.Substring(colon + 1), out port) || port < 1 || port > 65535)
                return false;
            host = rest.Substring(0, colon);
            return true;
        }

        /// <summary>Whether this way leads to <paramref name="server"/> ("name:port", as a host publishes the server it uses): the same name, over any port and transport, since every way in to one server reaches the same relay.</summary>
        internal bool IsOn(string server) => SameServer(ToString(), server);

        /// <summary>Whether two "name:port" texts name one relay server: the same name, whatever the port.</summary>
        internal static bool SameServer(string one, string other)
            => one != null && other != null && string.Equals(NameOf(one), NameOf(other), StringComparison.OrdinalIgnoreCase);

        private static string NameOf(string server)
        {
            int colon = server.LastIndexOf(':');
            return colon > 0 && int.TryParse(server.Substring(colon + 1), out _) ? server.Substring(0, colon) : server;
        }

        public override string ToString() => $"{Host}:{Port}";
    }
}
