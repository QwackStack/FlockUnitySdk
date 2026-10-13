using System;
using System.Collections.Generic;

namespace Flock.Providers
{
    /// <summary>One relay server over UDP and the login Flock minted for it, read from Flock's answer of servers to connect through.</summary>
    internal sealed class FlockRelayLogin
    {
        private const int DefaultPort = 3478;

        internal FlockRelayLogin(string host, int port, string username, string password)
        {
            Host = host;
            Port = port;
            Username = username;
            Password = password;
        }

        internal string Host { get; }
        internal int Port { get; }
        internal string Username { get; }
        internal string Password { get; }

        /// <summary>Every relay entry's UDP servers, in Flock's order (more than one while Flock moves between relays); STUN entries and entries without a login are left out.</summary>
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
                    if (TryReadUdpServer(url, out string host, out int port))
                        logins.Add(new FlockRelayLogin(host, port, entry.Username, entry.Credential));
                }
            }
            return logins;
        }

        /// <summary>A "turn:host[:port][?transport=udp]" address; false for TCP, TLS ("turns:"), an IPv6 host or anything unreadable, since this client speaks UDP over IPv4.</summary>
        internal static bool TryReadUdpServer(string url, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
                return false;
            string rest = url.Substring(5);
            int query = rest.IndexOf('?');
            if (query >= 0)
            {
                string transport = null;
                foreach (string part in rest.Substring(query + 1).Split('&'))
                {
                    if (part.StartsWith("transport=", StringComparison.OrdinalIgnoreCase))
                        transport = part.Substring("transport=".Length);
                }
                if (transport != null && !string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase))
                    return false;
                rest = rest.Substring(0, query);
            }
            if (rest.Length == 0 || rest.StartsWith("[", StringComparison.Ordinal))
                return false;
            int colon = rest.LastIndexOf(':');
            if (colon < 0)
            {
                host = rest;
                port = DefaultPort;
                return true;
            }
            if (colon == 0 || !int.TryParse(rest.Substring(colon + 1), out port) || port < 1 || port > 65535)
                return false;
            host = rest.Substring(0, colon);
            return true;
        }

        public override string ToString() => $"{Host}:{Port}";
    }
}
