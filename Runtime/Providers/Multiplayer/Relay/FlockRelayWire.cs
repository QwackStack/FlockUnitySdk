using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Flock.Providers
{
    /// <summary>STUN and TURN messages as Flock's relay speaks them: requests signed with a relay login, answers checked before they are believed, and the four-byte frames a channel carries.</summary>
    internal static class FlockRelayWire
    {
        internal const uint MagicCookie = 0x2112A442;
        internal const int HeaderLength = 20;
        internal const int TransactionLength = 12;
        internal const int ChannelFrameHeaderLength = 4;
        // What a send indication adds to a packet: its header, the peer's address and the data attribute's own header.
        internal const int SendIndicationOverhead = HeaderLength + 12 + 4;

        internal const ushort BindingMethod = 0x001;
        internal const ushort AllocateMethod = 0x003;
        internal const ushort RefreshMethod = 0x004;
        internal const ushort SendMethod = 0x006;
        internal const ushort DataMethod = 0x007;
        internal const ushort CreatePermissionMethod = 0x008;
        internal const ushort ChannelBindMethod = 0x009;

        internal const ushort RequestClass = 0x000;
        internal const ushort IndicationClass = 0x010;
        internal const ushort SuccessClass = 0x100;
        internal const ushort ErrorClass = 0x110;
        private const ushort ClassBits = 0x110;

        internal const ushort UsernameAttribute = 0x0006;
        internal const ushort MessageIntegrityAttribute = 0x0008;
        internal const ushort ErrorCodeAttribute = 0x0009;
        internal const ushort ChannelNumberAttribute = 0x000C;
        internal const ushort LifetimeAttribute = 0x000D;
        internal const ushort XorPeerAddressAttribute = 0x0012;
        internal const ushort DataAttribute = 0x0013;
        internal const ushort RealmAttribute = 0x0014;
        internal const ushort NonceAttribute = 0x0015;
        internal const ushort XorRelayedAddressAttribute = 0x0016;
        internal const ushort RequestedTransportAttribute = 0x0019;
        internal const ushort XorMappedAddressAttribute = 0x0020;
        internal const ushort FingerprintAttribute = 0x8028;

        // Channel numbers a client may bind, as the standard sets them.
        internal const ushort FirstChannel = 0x4000;
        internal const ushort LastChannel = 0x4FFF;

        private const byte UdpTransport = 17;
        private const byte Ipv4Family = 0x01;
        private const int IntegrityLength = 20;
        private const uint FingerprintMask = 0x5354554E;
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static readonly RNGCryptoServiceProvider Random = new RNGCryptoServiceProvider();

        /// <summary>A transaction id no one can guess, so only the relay can answer a request.</summary>
        internal static byte[] NewTransaction()
        {
            byte[] transaction = new byte[TransactionLength];
            lock (Random)
                Random.GetBytes(transaction);
            return transaction;
        }

        /// <summary>A whole message: its attributes, then a signature when a key is given, then a fingerprint.</summary>
        internal static byte[] Write(ushort type, byte[] transaction, IList<KeyValuePair<ushort, byte[]>> attributes, byte[] key)
        {
            List<byte> body = new List<byte>();
            foreach (KeyValuePair<ushort, byte[]> attribute in attributes)
                AddAttribute(body, attribute.Key, attribute.Value);
            if (key != null)
            {
                // The signature covers the header with a length that already counts the signature itself.
                byte[] signed = Join(Header(type, body.Count + 4 + IntegrityLength, transaction), body);
                using (HMACSHA1 signature = new HMACSHA1(key))
                    AddAttribute(body, MessageIntegrityAttribute, signature.ComputeHash(signed));
            }
            byte[] beforeFingerprint = Join(Header(type, body.Count + 8, transaction), body);
            byte[] fingerprint = new byte[4];
            WriteUInt32(fingerprint, 0, Crc32(beforeFingerprint, 0, beforeFingerprint.Length) ^ FingerprintMask);
            AddAttribute(body, FingerprintAttribute, fingerprint);
            return Join(Header(type, body.Count, transaction), body);
        }

        internal static byte[] UInt32Value(uint value)
        {
            byte[] bytes = new byte[4];
            WriteUInt32(bytes, 0, value);
            return bytes;
        }

        internal static byte[] UdpTransportValue() => new byte[] { UdpTransport, 0, 0, 0 };

        internal static byte[] ChannelValue(ushort channel) => new[] { (byte)(channel >> 8), (byte)channel, (byte)0, (byte)0 };

        internal static byte[] AddressValue(FlockRelayPeer peer)
        {
            byte[] value = new byte[8];
            WriteAddress(value, 0, peer);
            return value;
        }

        /// <summary>A send indication carrying <paramref name="count"/> bytes to <paramref name="to"/>, written into <paramref name="into"/> without garbage; its length.</summary>
        internal static int WriteSendIndication(byte[] into, byte[] transaction, FlockRelayPeer to, byte[] data, int offset, int count)
        {
            int padding = (4 - count % 4) % 4;
            WriteUInt16(into, 0, (ushort)(SendMethod | IndicationClass));
            WriteUInt16(into, 2, (ushort)(12 + 4 + count + padding));
            WriteUInt32(into, 4, MagicCookie);
            Buffer.BlockCopy(transaction, 0, into, 8, TransactionLength);
            WriteUInt16(into, 20, XorPeerAddressAttribute);
            WriteUInt16(into, 22, 8);
            WriteAddress(into, 24, to);
            WriteUInt16(into, 32, DataAttribute);
            WriteUInt16(into, 34, (ushort)count);
            Buffer.BlockCopy(data, offset, into, SendIndicationOverhead, count);
            for (int i = 0; i < padding; i++)
                into[SendIndicationOverhead + count + i] = 0;
            return SendIndicationOverhead + count + padding;
        }

        /// <summary>A channel frame carrying <paramref name="count"/> bytes, written into <paramref name="into"/> without garbage; its length.</summary>
        internal static int WriteChannelFrame(byte[] into, ushort channel, byte[] data, int offset, int count)
        {
            WriteUInt16(into, 0, channel);
            WriteUInt16(into, 2, (ushort)count);
            Buffer.BlockCopy(data, offset, into, ChannelFrameHeaderLength, count);
            return ChannelFrameHeaderLength + count;
        }

        /// <summary>A channel frame's number and how many of its bytes are data (they start after its four-byte header); false when the packet is not a whole channel frame.</summary>
        internal static bool TryReadChannelFrame(byte[] packet, int length, out ushort channel, out int dataLength)
        {
            channel = 0;
            dataLength = 0;
            if (length < ChannelFrameHeaderLength || (packet[0] & 0xC0) != 0x40)
                return false;
            channel = ReadUInt16(packet, 0);
            dataLength = ReadUInt16(packet, 2);
            return ChannelFrameHeaderLength + dataLength <= length;
        }

        /// <summary>The type of a STUN message whose header and length hold; false for anything else.</summary>
        internal static bool TryReadType(byte[] packet, int length, out ushort type)
        {
            type = 0;
            if (length < HeaderLength || (packet[0] & 0xC0) != 0 || ReadUInt32(packet, 4) != MagicCookie)
                return false;
            int messageLength = ReadUInt16(packet, 2);
            if (messageLength % 4 != 0 || HeaderLength + messageLength > length)
                return false;
            type = ReadUInt16(packet, 0);
            return true;
        }

        internal static ushort ClassOf(ushort type) => (ushort)(type & ClassBits);

        /// <summary>Who a data indication is from and where its data lies in the packet, read without garbage; false when it is not one.</summary>
        internal static bool TryReadDataIndication(byte[] packet, int length, out FlockRelayPeer from, out int dataOffset, out int dataLength)
        {
            from = default;
            dataOffset = 0;
            dataLength = 0;
            if (!TryReadType(packet, length, out ushort type) || type != (DataMethod | IndicationClass))
                return false;
            bool foundPeer = false;
            bool foundData = false;
            int end = HeaderLength + ReadUInt16(packet, 2);
            int position = HeaderLength;
            while (position + 4 <= end)
            {
                ushort kind = ReadUInt16(packet, position);
                int size = ReadUInt16(packet, position + 2);
                int value = position + 4;
                if (value + size > end)
                    return false;
                if (kind == XorPeerAddressAttribute && !foundPeer)
                    foundPeer = TryReadAddress(packet, value, size, out from);
                else if (kind == DataAttribute && !foundData)
                {
                    foundData = true;
                    dataOffset = value;
                    dataLength = size;
                }
                position = value + size + (4 - size % 4) % 4;
            }
            return foundPeer && foundData;
        }

        /// <summary>An IPv4 XOR address attribute's value at <paramref name="at"/>; false for any other family or size.</summary>
        internal static bool TryReadAddress(byte[] packet, int at, int size, out FlockRelayPeer peer)
        {
            peer = default;
            if (size != 8 || packet[at + 1] != Ipv4Family)
                return false;
            int port = ReadUInt16(packet, at + 2) ^ (int)(MagicCookie >> 16);
            peer = new FlockRelayPeer(ReadUInt32(packet, at + 4) ^ MagicCookie, port);
            return true;
        }

        /// <summary>A whole STUN message, its attributes kept by kind (the first of each), with where its signature and fingerprint start; null when the packet is not one.</summary>
        internal static Message Read(byte[] packet, int length)
        {
            if (!TryReadType(packet, length, out ushort type))
                return null;
            Message message = new Message(type, Slice(packet, 8, TransactionLength));
            int end = HeaderLength + ReadUInt16(packet, 2);
            int position = HeaderLength;
            while (position + 4 <= end)
            {
                ushort kind = ReadUInt16(packet, position);
                int size = ReadUInt16(packet, position + 2);
                int value = position + 4;
                if (value + size > end)
                    return null;
                if (kind == FingerprintAttribute)
                {
                    message.FingerprintAt = position;
                    break;
                }
                // Only the fingerprint may follow a signature.
                if (message.IntegrityAt < 0)
                {
                    if (kind == MessageIntegrityAttribute)
                        message.IntegrityAt = position;
                    if (!message.Attributes.ContainsKey(kind))
                        message.Attributes[kind] = Slice(packet, value, size);
                }
                position = value + size + (4 - size % 4) % 4;
            }
            message.Bytes = Slice(packet, 0, end);
            return message;
        }

        /// <summary>True when the message's signature was made with <paramref name="key"/>; false when it has none.</summary>
        internal static bool SignatureHolds(Message message, byte[] key)
        {
            if (message.IntegrityAt < 0 || key == null || message.IntegrityAt + 4 + IntegrityLength > message.Bytes.Length)
                return false;
            byte[] signed = Slice(message.Bytes, 0, message.IntegrityAt);
            WriteUInt16(signed, 2, (ushort)(message.IntegrityAt - HeaderLength + 4 + IntegrityLength));
            byte[] expected;
            using (HMACSHA1 signature = new HMACSHA1(key))
                expected = signature.ComputeHash(signed);
            for (int i = 0; i < IntegrityLength; i++)
            {
                if (message.Bytes[message.IntegrityAt + 4 + i] != expected[i])
                    return false;
            }
            return true;
        }

        /// <summary>The key a relay login signs with: MD5 of "username:realm:password", as the standard's long-term logins have it.</summary>
        internal static byte[] LoginKey(string username, string realm, string password)
        {
            using (MD5 md5 = new MD5CryptoServiceProvider())
                return md5.ComputeHash(Encoding.UTF8.GetBytes(username + ":" + realm + ":" + password));
        }

        /// <summary>An error answer's code (such as 401 or 438) and its words; 0 and empty when the value is too short.</summary>
        internal static int ErrorCodeOf(byte[] value, out string reason)
        {
            reason = string.Empty;
            if (value == null || value.Length < 4)
                return 0;
            reason = Encoding.UTF8.GetString(value, 4, value.Length - 4);
            return (value[2] & 0x7) * 100 + value[3];
        }

        internal static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++)
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        internal static ushort ReadUInt16(byte[] data, int at) => (ushort)((data[at] << 8) | data[at + 1]);

        internal static uint ReadUInt32(byte[] data, int at) => ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

        internal static void WriteUInt16(byte[] data, int at, ushort value)
        {
            data[at] = (byte)(value >> 8);
            data[at + 1] = (byte)value;
        }

        internal static void WriteUInt32(byte[] data, int at, uint value)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }

        private static void WriteAddress(byte[] into, int at, FlockRelayPeer peer)
        {
            into[at] = 0;
            into[at + 1] = Ipv4Family;
            WriteUInt16(into, at + 2, (ushort)(peer.Port ^ (int)(MagicCookie >> 16)));
            WriteUInt32(into, at + 4, peer.Address ^ MagicCookie);
        }

        private static byte[] Header(ushort type, int bodyLength, byte[] transaction)
        {
            byte[] header = new byte[HeaderLength];
            WriteUInt16(header, 0, type);
            WriteUInt16(header, 2, (ushort)bodyLength);
            WriteUInt32(header, 4, MagicCookie);
            Buffer.BlockCopy(transaction, 0, header, 8, TransactionLength);
            return header;
        }

        private static void AddAttribute(List<byte> body, ushort kind, byte[] value)
        {
            body.Add((byte)(kind >> 8));
            body.Add((byte)kind);
            body.Add((byte)(value.Length >> 8));
            body.Add((byte)value.Length);
            body.AddRange(value);
            for (int i = 0; i < (4 - value.Length % 4) % 4; i++)
                body.Add(0);
        }

        private static byte[] Join(byte[] header, List<byte> body)
        {
            byte[] whole = new byte[header.Length + body.Count];
            Buffer.BlockCopy(header, 0, whole, 0, header.Length);
            Buffer.BlockCopy(body.ToArray(), 0, whole, header.Length, body.Count);
            return whole;
        }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            byte[] slice = new byte[count];
            Buffer.BlockCopy(data, offset, slice, 0, count);
            return slice;
        }

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }

        /// <summary>A STUN message as read: its type, transaction, first attribute of each kind, and where its signature and fingerprint start (-1 when absent).</summary>
        internal sealed class Message
        {
            internal Message(ushort type, byte[] transaction)
            {
                Type = type;
                Transaction = transaction;
            }

            internal ushort Type { get; }
            internal byte[] Transaction { get; }
            internal Dictionary<ushort, byte[]> Attributes { get; } = new Dictionary<ushort, byte[]>();
            internal int IntegrityAt { get; set; } = -1;
            internal int FingerprintAt { get; set; } = -1;
            internal byte[] Bytes { get; set; }

            internal bool TryGetAddress(ushort kind, out FlockRelayPeer peer)
            {
                peer = default;
                return Attributes.TryGetValue(kind, out byte[] value) && TryReadAddress(value, 0, value.Length, out peer);
            }

            internal string Text(ushort kind) => Attributes.TryGetValue(kind, out byte[] value) ? Encoding.UTF8.GetString(value) : null;
        }
    }
}
