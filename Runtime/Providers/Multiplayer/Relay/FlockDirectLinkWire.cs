using System;
using System.Collections.Generic;
using System.Net;

namespace Flock.Providers
{
    /// <summary>The messages two bridges send each other to open a direct path: an offer of addresses through the relay, then probes, answers and keep-alives on the direct path, and word that one end went back to the relay. Each starts with a byte and letters no netcode packet starts with.</summary>
    internal static class FlockDirectLinkWire
    {
        // Unity Transport starts its packets with a type of 0 to 6 (1.x), or 1 to 5 or the letters "UTP" (2.x); 0xFA starts none of them.
        internal const byte Marker = 0xFA;
        // Unity Transport 1.x reads the next byte as flags and logs an error when its pipeline flag (0x02) is set: "L" leaves it clear, so a netcode an earlier SDK's bridge hands a message to drops it silently.
        internal const byte FirstLetter = (byte)'L';
        internal const byte Version = 1;
        internal const byte Offer = 1;
        internal const byte Probe = 2;
        internal const byte Answer = 3;
        internal const byte KeepAlive = 4;
        internal const byte BackToTheRelay = 5;
        internal const int KeyLength = 8;
        internal const int MostAddressesOffered = 4;
        // The marker, "LNK", the version and the kind.
        private const int HeaderLength = 6;
        private const int AddressLength = 6;
        // An offer's two keys, then how many addresses follow.
        private const int OfferAddressesAt = HeaderLength + 2 * KeyLength + 1;

        /// <summary>True for a message of the bridges' own, which never reaches the netcode.</summary>
        internal static bool IsMessage(byte[] packet, int offset, int count)
            => count >= HeaderLength && packet[offset] == Marker && packet[offset + 1] == FirstLetter && packet[offset + 2] == (byte)'N' && packet[offset + 3] == (byte)'K'
               && packet[offset + 4] == Version;

        internal static byte KindOf(byte[] packet, int offset) => packet[offset + 5];

        /// <summary>An offer: the sender's key, the receiver's key as the sender holds it (zeros before it has heard one), and up to <see cref="MostAddressesOffered"/> addresses the sender may be reached at.</summary>
        internal static byte[] WriteOffer(byte[] sendersKey, byte[] receiversKey, IReadOnlyList<IPEndPoint> addresses)
        {
            int count = Math.Min(addresses.Count, MostAddressesOffered);
            byte[] message = new byte[OfferAddressesAt + count * AddressLength];
            WriteHeader(message, Offer);
            Array.Copy(sendersKey, 0, message, HeaderLength, KeyLength);
            if (receiversKey != null)
                Array.Copy(receiversKey, 0, message, HeaderLength + KeyLength, KeyLength);
            message[OfferAddressesAt - 1] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                int at = OfferAddressesAt + i * AddressLength;
                byte[] address = addresses[i].Address.GetAddressBytes();
                Array.Copy(address, 0, message, at, 4);
                FlockRelayWire.WriteUInt16(message, at + 4, (ushort)addresses[i].Port);
            }
            return message;
        }

        /// <summary>Reads an offer; false for a short or malformed one. <paramref name="receiversKey"/> is null when the sender had heard no key of the receiver's.</summary>
        internal static bool TryReadOffer(byte[] packet, int offset, int count, out byte[] sendersKey, out byte[] receiversKey, out List<IPEndPoint> addresses)
        {
            sendersKey = null;
            receiversKey = null;
            addresses = null;
            if (!IsMessage(packet, offset, count) || KindOf(packet, offset) != Offer || count < OfferAddressesAt)
                return false;
            int offered = packet[offset + OfferAddressesAt - 1];
            if (offered < 1 || offered > MostAddressesOffered || count < OfferAddressesAt + offered * AddressLength)
                return false;
            sendersKey = new byte[KeyLength];
            Array.Copy(packet, offset + HeaderLength, sendersKey, 0, KeyLength);
            for (int i = 0; i < KeyLength; i++)
            {
                if (packet[offset + HeaderLength + KeyLength + i] != 0)
                {
                    receiversKey = new byte[KeyLength];
                    Array.Copy(packet, offset + HeaderLength + KeyLength, receiversKey, 0, KeyLength);
                    break;
                }
            }
            addresses = new List<IPEndPoint>(offered);
            for (int i = 0; i < offered; i++)
            {
                int at = offset + OfferAddressesAt + i * AddressLength;
                int port = FlockRelayWire.ReadUInt16(packet, at + 4);
                if (port == 0)
                    return false;
                addresses.Add(new IPEndPoint(new IPAddress(new[] { packet[at], packet[at + 1], packet[at + 2], packet[at + 3] }), port));
            }
            return true;
        }

        /// <summary>A probe, answer, keep-alive or back-to-the-relay message: the receiver's key, then the sender's.</summary>
        internal static byte[] WriteBetween(byte kind, byte[] receiversKey, byte[] sendersKey)
        {
            byte[] message = new byte[HeaderLength + 2 * KeyLength];
            WriteHeader(message, kind);
            Array.Copy(receiversKey, 0, message, HeaderLength, KeyLength);
            Array.Copy(sendersKey, 0, message, HeaderLength + KeyLength, KeyLength);
            return message;
        }

        /// <summary>True when a probe, answer, keep-alive or back-to-the-relay message names both keys of this pair: <paramref name="receiversKey"/> first.</summary>
        internal static bool IsBetween(byte[] packet, int offset, int count, byte[] receiversKey, byte[] sendersKey)
        {
            if (!IsMessage(packet, offset, count) || count < HeaderLength + 2 * KeyLength || receiversKey == null || sendersKey == null)
                return false;
            for (int i = 0; i < KeyLength; i++)
            {
                if (packet[offset + HeaderLength + i] != receiversKey[i] || packet[offset + HeaderLength + KeyLength + i] != sendersKey[i])
                    return false;
            }
            return true;
        }

        private static void WriteHeader(byte[] message, byte kind)
        {
            message[0] = Marker;
            message[1] = FirstLetter;
            message[2] = (byte)'N';
            message[3] = (byte)'K';
            message[4] = Version;
            message[5] = kind;
        }
    }
}
