using System;
using System.Collections.Generic;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>What the relay keeps only while it is renewed (the relay address, each opening, each channel): renewed before the relay lets it go, and the relay lost when one lapses or a renewal is refused. The relay thread's alone.</summary>
    internal sealed class FlockRelayRenewals
    {
        private const uint ReservationSecondsAsked = 600;

        private readonly FlockRelayRequests _requests;
        private readonly FlockRelayPackets _packets;
        private readonly FlockRelayConnection.Timing _timing;
        private readonly IFlockLogger _logger;
        private readonly Action<string> _loseTheRelay;
        private readonly List<Renewal> _renewals = new List<Renewal>();
        private bool _toldChannelRefused;

        internal FlockRelayRenewals(FlockRelayRequests requests, FlockRelayPackets packets, FlockRelayConnection.Timing timing, IFlockLogger logger, Action<string> loseTheRelay)
        {
            _requests = requests;
            _packets = packets;
            _timing = timing;
            _logger = logger;
            _loseTheRelay = loseTheRelay;
        }

        /// <summary>Keeps the relay address the reservation's answer granted, renewed from now on.</summary>
        internal void KeepReservation(FlockRelayWire.Message answer, long now)
        {
            Renewal reservation = new Renewal(RenewalKind.Reservation);
            KeepReservation(reservation, answer, now);
            _renewals.Add(reservation);
        }

        /// <summary>Keeps an opening the relay just granted, renewed from now on.</summary>
        internal void KeepOpening(uint ip, long now)
        {
            _renewals.Add(new Renewal(RenewalKind.Opening)
            {
                Address = ip,
                GoodUntil = now + FlockRelayClock.Ticks(_timing.OpeningLasts),
                RenewAt = now + FlockRelayClock.Ticks(_timing.RenewOpeningsEvery),
            });
        }

        /// <summary>Binds <paramref name="channel"/> to <paramref name="peer"/> now, and renews it from then on.</summary>
        internal void BindChannel(FlockRelayPeer peer, ushort channel, long now)
        {
            _renewals.Add(new Renewal(RenewalKind.Channel) { Peer = peer, Channel = channel, RenewAt = now });
        }

        /// <summary>Sends every renewal that is due, and loses the relay when something it keeps lapsed.</summary>
        internal void RenewDue(long now)
        {
            for (int i = 0; i < _renewals.Count; i++)
            {
                Renewal renewal = _renewals[i];
                if (renewal.Refused)
                    continue;
                if (renewal.Kind == RenewalKind.Channel)
                {
                    if (renewal.Bound && now > renewal.GoodUntil)
                    {
                        // A channel the relay let go: packets to its peer go as send indications until it is bound again.
                        renewal.Bound = false;
                        _packets.ChannelLetGo(renewal.Peer);
                    }
                }
                else if (now > renewal.GoodUntil)
                {
                    _loseTheRelay(renewal.Kind == RenewalKind.Reservation
                        ? "the relay address was not renewed before the relay let it go"
                        : "the opening to the host's relay address was not renewed before the relay let it go");
                    return;
                }
                if (renewal.Sent || now < renewal.RenewAt)
                    continue;
                renewal.Sent = true;
                _requests.Send(RequestFor(renewal));
            }
        }

        private FlockRelayRequest RequestFor(Renewal renewal)
        {
            switch (renewal.Kind)
            {
                case RenewalKind.Reservation:
                    return new FlockRelayRequest(FlockRelayWire.RefreshMethod, (code, words, answer) => Renewed(renewal, code, words, answer),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.LifetimeAttribute, FlockRelayWire.UInt32Value(ReservationSecondsAsked)));
                case RenewalKind.Opening:
                    return new FlockRelayRequest(FlockRelayWire.CreatePermissionMethod, (code, words, answer) => Renewed(renewal, code, words, answer),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.XorPeerAddressAttribute, FlockRelayWire.AddressValue(new FlockRelayPeer(renewal.Address, 0))));
                default:
                    return new FlockRelayRequest(FlockRelayWire.ChannelBindMethod, (code, words, answer) => Renewed(renewal, code, words, answer),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.ChannelNumberAttribute, FlockRelayWire.ChannelValue(renewal.Channel)),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.XorPeerAddressAttribute, FlockRelayWire.AddressValue(renewal.Peer)));
            }
        }

        private void Renewed(Renewal renewal, int code, string words, FlockRelayWire.Message answer)
        {
            renewal.Sent = false;
            long now = FlockRelayClock.Now();
            if (code == 0)
            {
                if (renewal.Kind == RenewalKind.Reservation)
                    KeepReservation(renewal, answer, now);
                else if (renewal.Kind == RenewalKind.Opening)
                {
                    renewal.GoodUntil = now + FlockRelayClock.Ticks(_timing.OpeningLasts);
                    renewal.RenewAt = now + FlockRelayClock.Ticks(_timing.RenewOpeningsEvery);
                }
                else
                {
                    if (!renewal.Bound)
                    {
                        renewal.Bound = true;
                        _packets.ChannelBound(renewal.Peer, renewal.Channel);
                    }
                    renewal.GoodUntil = now + FlockRelayClock.Ticks(_timing.ChannelLasts);
                    renewal.RenewAt = now + FlockRelayClock.Ticks(_timing.RenewChannelsEvery);
                }
                return;
            }
            // No answer: tried again soon; what lapses meanwhile is judged by how long the relay keeps it.
            if (code == FlockRelayRequests.NoAnswer)
            {
                renewal.RenewAt = now + FlockRelayClock.Ticks(_timing.RetryRenewalAfter);
                return;
            }
            if (renewal.Kind == RenewalKind.Channel)
            {
                renewal.Refused = true;
                if (renewal.Bound)
                {
                    renewal.Bound = false;
                    _packets.ChannelLetGo(renewal.Peer);
                }
                if (!_toldChannelRefused)
                {
                    _toldChannelRefused = true;
                    _logger.LogWarning($"The relay refused a channel to {renewal.Peer} ({code} {words}); packets to it go as send indications, 32 bytes larger each.");
                }
                return;
            }
            _loseTheRelay(renewal.Kind == RenewalKind.Reservation
                ? $"the relay refused to renew the relay address ({code} {words})"
                : $"the relay refused to renew the opening to the host's relay address ({code} {words})");
        }

        private void KeepReservation(Renewal renewal, FlockRelayWire.Message answer, long now)
        {
            uint seconds = ReservationSecondsAsked;
            if (answer != null && answer.Attributes.TryGetValue(FlockRelayWire.LifetimeAttribute, out byte[] lifetime) && lifetime.Length == 4)
                seconds = FlockRelayWire.ReadUInt32(lifetime, 0);
            TimeSpan lasts = TimeSpan.FromSeconds(seconds);
            TimeSpan half = TimeSpan.FromTicks(lasts.Ticks / 2);
            renewal.GoodUntil = now + FlockRelayClock.Ticks(lasts);
            renewal.RenewAt = now + FlockRelayClock.Ticks(half < _timing.LongestBetweenReservationRenewals ? half : _timing.LongestBetweenReservationRenewals);
        }

        private enum RenewalKind
        {
            Reservation,
            Opening,
            Channel,
        }

        // Something the relay keeps only as long as it is renewed: the relay address, an opening, or a channel.
        private sealed class Renewal
        {
            internal Renewal(RenewalKind kind)
            {
                Kind = kind;
            }

            internal RenewalKind Kind { get; }
            internal uint Address;
            internal FlockRelayPeer Peer;
            internal ushort Channel;
            internal bool Bound;
            internal bool Refused;
            internal bool Sent;
            internal long RenewAt;
            internal long GoodUntil;
        }
    }
}
