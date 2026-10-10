using System;
using System.Collections.Generic;
using System.Threading;

namespace Flock.Providers
{
    /// <summary>The packets a relay connection carries: the IPs the relay opened to, the channel bound to each peer, sending, and what arrived waiting for the game.</summary>
    internal sealed class FlockRelayPackets
    {
        /// <summary>The most bytes one packet may carry, the most Unity Transport sends in one.</summary>
        internal const int MostBytesInAPacket = 1400;
        private const int PacketsHeld = 256;
        private const int MostChannels = 64;

        private readonly FlockRelaySocket _socket;

        // Replaced whole when they change, so a thread sending reads them without a lock; only the relay thread replaces them.
        private volatile uint[] _openedTo = new uint[0];
        private volatile Channel[] _channels = new Channel[0];

        // Every peer a channel was ever wanted for, added under _wantedGate from any thread.
        private readonly object _wantedGate = new object();
        private volatile FlockRelayPeer[] _channelsWanted = new FlockRelayPeer[0];

        // The relay thread's own: the peer each channel number went to, and how many went.
        private readonly FlockRelayPeer[] _peerOfChannel = new FlockRelayPeer[MostChannels];
        private int _channelsAsked;

        // Sending, from any thread.
        private readonly object _sendGate = new object();
        private readonly byte[] _sendBuffer = new byte[FlockRelayWire.SendIndicationOverhead + MostBytesInAPacket + 4];
        private readonly byte[] _indicationTransaction = FlockRelayWire.NewTransaction();
        private uint _indicationsSent;

        // Who packets that arrive are handed to on the relay thread (a bridge to the netcode), at most one; null keeps them in the slots below.
        private Action<FlockRelayPeer, byte[], int, int> _receiver;

        // Packets that arrived, waiting for the game: one block of slots, made when the first is kept.
        private readonly object _arrivedGate = new object();
        private byte[] _arrived;
        private readonly int[] _arrivedLengths = new int[PacketsHeld];
        private readonly FlockRelayPeer[] _arrivedFrom = new FlockRelayPeer[PacketsHeld];
        private int _arrivedFirst;
        private int _arrivedCount;
        private long _packetsDropped;

        internal FlockRelayPackets(FlockRelaySocket socket)
        {
            _socket = socket;
        }

        /// <summary>Packets that arrived while <see cref="MostBytesInAPacket"/>-byte room for 256 was full, or that were larger.</summary>
        internal long PacketsDropped => Interlocked.Read(ref _packetsDropped);

        internal bool IsOpenedTo(uint ip)
        {
            uint[] opened = _openedTo;
            for (int i = 0; i < opened.Length; i++)
            {
                if (opened[i] == ip)
                    return true;
            }
            return false;
        }

        /// <summary>Notes an IP the relay opened to; false when it already was. The relay thread's alone.</summary>
        internal bool AddOpening(uint ip)
        {
            if (IsOpenedTo(ip))
                return false;
            uint[] before = _openedTo;
            uint[] after = new uint[before.Length + 1];
            Array.Copy(before, after, before.Length);
            after[before.Length] = ip;
            _openedTo = after;
            return true;
        }

        /// <summary>Sends one packet to <paramref name="to"/>, on its channel once one is bound; false when it is empty, over the most, to an IP never opened, or the socket refused it.</summary>
        internal bool Send(FlockRelayPeer to, byte[] data, int offset, int count)
        {
            if (data == null || count <= 0 || count > MostBytesInAPacket || offset < 0 || offset > data.Length - count)
                return false;
            if (!IsOpenedTo(to.Address))
                return false;
            ushort channel = ChannelFor(to);
            lock (_sendGate)
            {
                int length;
                if (channel != 0)
                    length = FlockRelayWire.WriteChannelFrame(_sendBuffer, channel, data, offset, count);
                else
                {
                    FlockRelayWire.WriteUInt32(_indicationTransaction, 8, ++_indicationsSent);
                    length = FlockRelayWire.WriteSendIndication(_sendBuffer, _indicationTransaction, to, data, offset, count);
                }
                return _socket.TrySend(_sendBuffer, 0, length);
            }
        }

        /// <summary>Hands every packet that arrives to <paramref name="receiver"/>, on the relay thread, instead of keeping it; false when another receiver has them.</summary>
        internal bool HandArrivalsTo(Action<FlockRelayPeer, byte[], int, int> receiver)
            => Interlocked.CompareExchange(ref _receiver, receiver, null) == null;

        /// <summary>Keeps arrivals again, if <paramref name="receiver"/> is the one they are handed to.</summary>
        internal void StopHandingArrivalsTo(Action<FlockRelayPeer, byte[], int, int> receiver)
            => Interlocked.CompareExchange(ref _receiver, null, receiver);

        internal bool HandsArrivalsOver => Volatile.Read(ref _receiver) != null;

        internal bool KeepsPacketsForTesting
        {
            get
            {
                lock (_arrivedGate)
                    return _arrived != null;
            }
        }

        /// <summary>Takes the oldest packet that arrived, copied into <paramref name="into"/> (at least <see cref="MostBytesInAPacket"/> bytes); false when none is waiting.</summary>
        internal bool TryReceive(byte[] into, out int count, out FlockRelayPeer from)
        {
            if (into == null || into.Length < MostBytesInAPacket)
                throw new ArgumentException($"A receive buffer holds at least {MostBytesInAPacket} bytes.", nameof(into));
            lock (_arrivedGate)
            {
                if (_arrivedCount == 0)
                {
                    count = 0;
                    from = default;
                    return false;
                }
                count = _arrivedLengths[_arrivedFirst];
                from = _arrivedFrom[_arrivedFirst];
                Buffer.BlockCopy(_arrived, _arrivedFirst * MostBytesInAPacket, into, 0, count);
                _arrivedFirst = (_arrivedFirst + 1) % PacketsHeld;
                _arrivedCount--;
                return true;
            }
        }

        /// <summary>Keeps a packet a peer sent through the relay, a channel frame or a data indication; false when it is neither, so it is the relay's own message. The relay thread's alone.</summary>
        internal bool TryTake(byte[] packet, int length)
        {
            if (FlockRelayWire.TryReadChannelFrame(packet, length, out ushort channel, out int dataLength))
            {
                int index = channel - FlockRelayWire.FirstChannel;
                if (index >= 0 && index < MostChannels && !_peerOfChannel[index].IsEmpty)
                    Arrive(_peerOfChannel[index], packet, FlockRelayWire.ChannelFrameHeaderLength, dataLength);
                return true;
            }
            if (FlockRelayWire.TryReadDataIndication(packet, length, out FlockRelayPeer from, out int dataOffset, out int dataCount))
            {
                Arrive(from, packet, dataOffset, dataCount);
                return true;
            }
            return false;
        }

        /// <summary>Gives the next peer a channel was wanted for its number, once each; false when there is none new. The relay thread's alone.</summary>
        internal bool TryGiveNextChannel(out FlockRelayPeer peer, out ushort channel)
        {
            FlockRelayPeer[] wanted = _channelsWanted;
            if (_channelsAsked >= wanted.Length)
            {
                peer = default;
                channel = 0;
                return false;
            }
            peer = wanted[_channelsAsked];
            _peerOfChannel[_channelsAsked] = peer;
            channel = (ushort)(FlockRelayWire.FirstChannel + _channelsAsked);
            _channelsAsked++;
            return true;
        }

        /// <summary>Packets to <paramref name="peer"/> go on <paramref name="number"/> from now. The relay thread's alone.</summary>
        internal void ChannelBound(FlockRelayPeer peer, ushort number)
        {
            Channel[] before = _channels;
            Channel[] after = new Channel[before.Length + 1];
            Array.Copy(before, after, before.Length);
            after[before.Length] = new Channel(peer, number);
            _channels = after;
        }

        /// <summary>The relay let <paramref name="peer"/>'s channel go: packets to it go as send indications. The relay thread's alone.</summary>
        internal void ChannelLetGo(FlockRelayPeer peer)
        {
            Channel[] before = _channels;
            List<Channel> kept = new List<Channel>(before.Length);
            foreach (Channel channel in before)
            {
                if (!channel.Peer.Equals(peer))
                    kept.Add(channel);
            }
            _channels = kept.ToArray();
        }

        private ushort ChannelFor(FlockRelayPeer peer)
        {
            Channel[] channels = _channels;
            for (int i = 0; i < channels.Length; i++)
            {
                if (channels[i].Peer.Equals(peer))
                    return channels[i].Number;
            }
            return 0;
        }

        /// <summary>Binds a channel to <paramref name="peer"/> on the relay thread's next turn, once each; past the most channels a relay connection keeps, packets go as send indications. Only a peer known to be a player is given one, so a stranger on the relay cannot take them all.</summary>
        internal void WantChannel(FlockRelayPeer peer)
        {
            if (HasWanted(_channelsWanted, peer))
                return;
            lock (_wantedGate)
            {
                FlockRelayPeer[] before = _channelsWanted;
                if (HasWanted(before, peer))
                    return;
                FlockRelayPeer[] after = new FlockRelayPeer[before.Length + 1];
                Array.Copy(before, after, before.Length);
                after[before.Length] = peer;
                _channelsWanted = after;
            }
        }

        // True as well once the most channels were wanted, so nothing more is asked.
        private static bool HasWanted(FlockRelayPeer[] wanted, FlockRelayPeer peer)
        {
            if (wanted.Length >= MostChannels)
                return true;
            for (int i = 0; i < wanted.Length; i++)
            {
                if (wanted[i].Equals(peer))
                    return true;
            }
            return false;
        }

        private void Arrive(FlockRelayPeer from, byte[] packet, int offset, int count)
        {
            if (count > MostBytesInAPacket)
            {
                Interlocked.Increment(ref _packetsDropped);
                return;
            }
            Action<FlockRelayPeer, byte[], int, int> receiver = Volatile.Read(ref _receiver);
            if (receiver != null)
            {
                // Nothing a receiver does may stop the relay thread.
                try
                {
                    receiver(from, packet, offset, count);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _packetsDropped);
                }
                return;
            }
            lock (_arrivedGate)
            {
                if (_arrivedCount == PacketsHeld)
                {
                    Interlocked.Increment(ref _packetsDropped);
                    return;
                }
                // Made on the relay thread when the first packet is kept, so a relay whose packets are all handed over has none.
                if (_arrived == null)
                    _arrived = new byte[PacketsHeld * MostBytesInAPacket];
                int slot = (_arrivedFirst + _arrivedCount) % PacketsHeld;
                Buffer.BlockCopy(packet, offset, _arrived, slot * MostBytesInAPacket, count);
                _arrivedLengths[slot] = count;
                _arrivedFrom[slot] = from;
                _arrivedCount++;
            }
        }

        private readonly struct Channel
        {
            internal Channel(FlockRelayPeer peer, ushort number)
            {
                Peer = peer;
                Number = number;
            }

            internal FlockRelayPeer Peer { get; }
            internal ushort Number { get; }
        }
    }
}
