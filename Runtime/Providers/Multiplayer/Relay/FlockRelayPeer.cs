using System;
using System.Globalization;

namespace Flock.Providers
{
    /// <summary>An IPv4 address and port on the relay: a player's relay address, or where a packet came from. A value, so handing packets over makes no garbage.</summary>
    internal readonly struct FlockRelayPeer : IEquatable<FlockRelayPeer>
    {
        internal FlockRelayPeer(uint address, int port)
        {
            Address = address;
            Port = port;
        }

        /// <summary>The four address bytes, the first one highest.</summary>
        internal uint Address { get; }

        internal int Port { get; }

        internal bool IsEmpty => Address == 0 && Port == 0;

        /// <summary>Reads "a.b.c.d:port", digits only; false for anything else (a sign, a space, a port out of range).</summary>
        internal static bool TryParse(string text, out FlockRelayPeer peer)
        {
            peer = default;
            if (string.IsNullOrEmpty(text))
                return false;
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(text.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                return false;
            string[] parts = text.Substring(0, colon).Split('.');
            if (parts.Length != 4)
                return false;
            uint address = 0;
            foreach (string part in parts)
            {
                if (part.Length == 0 || part.Length > 3 || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte value))
                    return false;
                address = (address << 8) | value;
            }
            peer = new FlockRelayPeer(address, port);
            return true;
        }

        public bool Equals(FlockRelayPeer other) => Address == other.Address && Port == other.Port;

        public override bool Equals(object other) => other is FlockRelayPeer peer && Equals(peer);

        public override int GetHashCode() => unchecked((int)Address * 397) ^ Port;

        public override string ToString() => $"{Address >> 24}.{(Address >> 16) & 0xFF}.{(Address >> 8) & 0xFF}.{Address & 0xFF}:{Port}";
    }
}
