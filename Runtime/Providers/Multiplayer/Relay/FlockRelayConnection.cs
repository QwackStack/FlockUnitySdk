using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>One address on Flock's relay for one session, kept alive on a thread of its own; it hands raw packets over without waiting on the relay or making garbage.</summary>
    internal sealed class FlockRelayConnection
    {
        /// <summary>The most bytes one packet may carry, the most Unity Transport sends in one.</summary>
        internal const int MostBytesInAPacket = 1400;
        private const int PacketsHeld = 256;
        private const int MostChannels = 64;
        private const uint ReservationSecondsAsked = 600;
        // A request is sent again 0.5 s, 1.5 s and 3.5 s after it first went, then given up on once the answer wait is over.
        private const int MostSends = 4;
        private static readonly TimeSpan FirstResend = TimeSpan.FromMilliseconds(500);
        private const int LongestPollMicroseconds = 20000;
        private const int NoAnswer = -1;
        // A refusal that names no code is still a refusal, never the success 0 stands for.
        private const int RefusalWithNoCode = -2;

        private const int StateOpening = 0;
        private const int StateOpen = 1;
        private const int StateClosed = 2;
        private const int StateFailed = 3;

        /// <summary>How long the relay's steps may take and how often it is renewed; a test shortens them.</summary>
        internal sealed class Timing
        {
            internal TimeSpan NameLookUpWait = TimeSpan.FromSeconds(5);
            internal TimeSpan AnswerWait = TimeSpan.FromSeconds(5);
            // The reservation is renewed at half the time the relay grants, and at least this often.
            internal TimeSpan LongestBetweenReservationRenewals = TimeSpan.FromMinutes(4);
            internal TimeSpan RenewOpeningsEvery = TimeSpan.FromMinutes(4);
            internal TimeSpan RenewChannelsEvery = TimeSpan.FromMinutes(8);
            internal TimeSpan RetryRenewalAfter = TimeSpan.FromSeconds(15);
            internal TimeSpan KeepAliveAfter = TimeSpan.FromSeconds(15);
            // How long the relay keeps an opening and a channel without a renewal, as the standard sets them.
            internal TimeSpan OpeningLasts = TimeSpan.FromMinutes(5);
            internal TimeSpan ChannelLasts = TimeSpan.FromMinutes(10);
            // How long a closed connection waits for the release's answer, to send it again when the relay's nonce went stale.
            internal TimeSpan ReleaseAnswerWait = TimeSpan.FromSeconds(1);
            internal Func<string, Task<IPAddress[]>> NameLookUpForTesting;
        }

        private readonly FlockRelayLogin _login;
        private readonly Timing _timing;
        private readonly IFlockLogger _logger;
        private readonly Socket _socket;
        private readonly Thread _thread;
        private readonly TaskCompletionSource<bool> _opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _state = StateOpening;
        private int _stopping;
        // Set once a stop has sent what it sends; only then may the relay thread close the socket.
        private int _stopSettled;
        private volatile string _failureReason;
        private volatile bool _reservationAsked;
        private FlockRelayPeer _address;
        private int _noncesTaken;

        // The release on its way and when the socket closes whatever its answer; the relay thread sends it again once on a stale nonce.
        private volatile byte[] _releaseTransaction;
        private long _closeSocketAt;
        private bool _releaseSentAgain;

        // Handed from the game's threads to the relay thread, and what requests are signed with; all under _gate.
        private readonly object _gate = new object();
        private readonly List<Request> _handedOver = new List<Request>();
        private readonly List<TaskCompletionSource<bool>> _callersWaiting = new List<TaskCompletionSource<bool>>();
        private string _realm;
        private string _nonce;
        private byte[] _key;

        // Replaced whole when they change, so a thread sending reads them without a lock.
        private volatile uint[] _openedTo = new uint[0];
        private volatile Channel[] _channels = new Channel[0];
        // Every peer a channel was ever wanted for, added under _gate.
        private volatile FlockRelayPeer[] _channelsWanted = new FlockRelayPeer[0];

        // The relay thread's own.
        private readonly List<Request> _waiting = new List<Request>();
        private readonly List<Renewal> _renewals = new List<Renewal>();
        private byte[] _received;
        private readonly FlockRelayPeer[] _peerOfChannel = new FlockRelayPeer[MostChannels];
        private int _channelsAsked;
        private bool _toldChannelRefused;
        private long _lastSentAt;

        // Sending, from any thread.
        private readonly object _sendGate = new object();
        private readonly byte[] _sendBuffer = new byte[FlockRelayWire.SendIndicationOverhead + MostBytesInAPacket + 4];
        private readonly byte[] _indicationTransaction = FlockRelayWire.NewTransaction();
        private uint _indicationsSent;

        // Packets that arrived, waiting for the game: one block of slots, made on the relay thread once an address is reserved.
        private readonly object _arrivedGate = new object();
        private byte[] _arrived;
        private readonly int[] _arrivedLengths = new int[PacketsHeld];
        private readonly FlockRelayPeer[] _arrivedFrom = new FlockRelayPeer[PacketsHeld];
        private int _arrivedFirst;
        private int _arrivedCount;
        private long _packetsDropped;

        private FlockRelayConnection(FlockRelayLogin login, IPEndPoint server, Timing timing, IFlockLogger logger)
        {
            _login = login;
            _timing = timing;
            _logger = logger;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                // Connected, so only the relay's own packets are read, and sends need no address of their own.
                _socket.Connect(server);
                // A full send buffer drops a packet rather than holding the game's thread.
                _socket.Blocking = false;
            }
            catch
            {
                _socket.Close();
                throw;
            }
            _lastSentAt = Now();
            _handedOver.Add(new Request(FlockRelayWire.AllocateMethod, Reserved,
                new KeyValuePair<ushort, byte[]>(FlockRelayWire.RequestedTransportAttribute, FlockRelayWire.UdpTransportValue())));
            _thread = new Thread(Run) { IsBackground = true, Name = RelayThreadName };
            _thread.Start();
        }

        internal const string RelayThreadName = "Flock relay";

        /// <summary>This player's address on the relay; the host publishes it, and players send to it.</summary>
        internal FlockRelayPeer Address => _address;

        internal bool IsOpen => Volatile.Read(ref _state) == StateOpen;

        /// <summary>True once closed or failed; nothing more is sent.</summary>
        internal bool HasStopped => Volatile.Read(ref _state) >= StateClosed;

        /// <summary>One of <see cref="FlockRelayFailure"/>'s reasons once the relay failed or was lost; null while it works and after a plain close.</summary>
        internal string FailureReason => _failureReason;

        /// <summary>Packets that arrived while <see cref="MostBytesInAPacket"/>-byte room for 256 was full, or that were larger.</summary>
        internal long PacketsDropped => Interlocked.Read(ref _packetsDropped);

        /// <summary>Realms and nonces the relay handed over to sign with: its login challenge, then each stale nonce answered.</summary>
        internal int NoncesTakenForTesting => Volatile.Read(ref _noncesTaken);

        /// <summary>Reserves an address on the relay with this login; fails with a <see cref="FlockRelayException"/> naming why. Cancelling closes it.</summary>
        internal static async Task<FlockRelayConnection> OpenAsync(FlockRelayLogin login, Timing timing, IFlockLogger logger, CancellationToken cancellationToken)
        {
            IPAddress server = await FlockNameLookup.FindIpv4Async(login.Host, timing.NameLookUpWait, timing.NameLookUpForTesting, cancellationToken);
            if (server == null)
                throw new FlockRelayException(FlockRelayFailure.Unreachable, $"The relay server's name {login.Host} could not be looked up within {timing.NameLookUpWait.TotalSeconds:0.#} s.");
            FlockRelayConnection connection;
            try
            {
                connection = new FlockRelayConnection(login, new IPEndPoint(server, login.Port), timing, logger);
            }
            catch (SocketException failure)
            {
                throw new FlockRelayException(FlockRelayFailure.Unreachable, $"No way to send to the relay at {login}: {failure.SocketErrorCode}.", failure);
            }
            using (cancellationToken.Register(connection.Close))
                await connection._opened.Task;
            if (cancellationToken.IsCancellationRequested)
            {
                connection.Close();
                throw new OperationCanceledException(cancellationToken);
            }
            return connection;
        }

        /// <summary>Lets packets in from, and out to, every relay address sharing the IP of the host's relay address. The host and every player call it with the host's address; the relay renews it until closed.</summary>
        internal Task OpenToHostAsync(FlockRelayPeer hostAddress, CancellationToken cancellationToken = default)
        {
            if (!IsOpen)
                return Task.FromException(NotOpen());
            if (IsOpenedTo(hostAddress.Address))
                return Task.CompletedTask;
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint ip = hostAddress.Address;
            Request request = new Request(FlockRelayWire.CreatePermissionMethod, (code, words, answer) => Opened(ip, code, words, done),
                new KeyValuePair<ushort, byte[]>(FlockRelayWire.XorPeerAddressAttribute, FlockRelayWire.AddressValue(new FlockRelayPeer(ip, 0))));
            lock (_gate)
            {
                if (HasStopped)
                    return Task.FromException(NotOpen());
                _callersWaiting.Add(done);
                _handedOver.Add(request);
            }
            return WaitAsync(done.Task, cancellationToken);
        }

        /// <summary>Sends one packet to <paramref name="to"/> through the relay; false when the relay is not open, the packet is empty or over <see cref="MostBytesInAPacket"/> bytes, or <paramref name="to"/>'s IP was never opened.</summary>
        internal bool Send(FlockRelayPeer to, byte[] data, int offset, int count)
        {
            if (!IsOpen || data == null || count <= 0 || count > MostBytesInAPacket || offset < 0 || offset > data.Length - count)
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
                try
                {
                    _socket.Send(_sendBuffer, 0, length, SocketFlags.None);
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
            Interlocked.Exchange(ref _lastSentAt, Now());
            if (channel == 0)
                WantChannel(to);
            return true;
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

        /// <summary>Releases the address on the relay (sent from the calling thread, so it goes out even while the game quits) and stops; a call still waiting ends as cancelled.</summary>
        internal void Close() => Stop(StateClosed, null, null);

        // ---- the relay thread ----

        private void Run()
        {
            try
            {
                _received = new byte[65536];
                while (true)
                {
                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        // Stopped: only the release's answer is waited for, and the socket is this thread's to close.
                        if (Volatile.Read(ref _stopSettled) != 0 && (_releaseTransaction == null || Now() >= Interlocked.Read(ref _closeSocketAt)))
                            return;
                        if (_socket.Poll(LongestPollMicroseconds, SelectMode.SelectRead))
                            ReadEverythingWaiting();
                        continue;
                    }
                    TakeHandedOver();
                    if (_socket.Poll(LongestPollMicroseconds, SelectMode.SelectRead))
                        ReadEverythingWaiting();
                    long now = Now();
                    SendAgainOrGiveUp(now);
                    Renew(now);
                    KeepAlive(now);
                }
            }
            catch (ObjectDisposedException)
            {
                // Closed under it.
            }
            catch (Exception failure)
            {
                if (Volatile.Read(ref _stopping) == 0)
                    Lose($"the relay connection stopped working ({failure.GetType().Name}: {FlockExceptionText.MessageOf(failure)})");
            }
            finally
            {
                _socket.Close();
            }
        }

        private void TakeHandedOver()
        {
            List<Request> requests = null;
            lock (_gate)
            {
                if (_handedOver.Count > 0)
                {
                    requests = new List<Request>(_handedOver);
                    _handedOver.Clear();
                }
            }
            FlockRelayPeer[] wanted = _channelsWanted;
            // A new peer gets the next channel; its renewal, due at once, binds it.
            while (_channelsAsked < wanted.Length)
            {
                FlockRelayPeer peer = wanted[_channelsAsked];
                _peerOfChannel[_channelsAsked] = peer;
                _renewals.Add(new Renewal(RenewalKind.Channel) { Peer = peer, Channel = (ushort)(FlockRelayWire.FirstChannel + _channelsAsked), RenewAt = Now() });
                _channelsAsked++;
            }
            if (requests == null)
                return;
            foreach (Request request in requests)
                SendFirst(request);
        }

        private void ReadEverythingWaiting()
        {
            do
            {
                int length;
                try
                {
                    length = _socket.Receive(_received);
                }
                catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionReset || failure.SocketErrorCode == SocketError.ConnectionRefused
                                                      || failure.SocketErrorCode == SocketError.WouldBlock)
                {
                    // An earlier packet met a closed port, or nothing was there after all; whether the relay answers is judged by the answer wait alone.
                    return;
                }
                Take(_received, length);
            }
            while (_socket.Available > 0);
        }

        private void Take(byte[] packet, int length)
        {
            if (FlockRelayWire.TryReadChannelFrame(packet, length, out ushort channel, out int dataLength))
            {
                int index = channel - FlockRelayWire.FirstChannel;
                if (index >= 0 && index < MostChannels && !_peerOfChannel[index].IsEmpty)
                    Arrive(_peerOfChannel[index], packet, FlockRelayWire.ChannelFrameHeaderLength, dataLength);
                return;
            }
            if (FlockRelayWire.TryReadDataIndication(packet, length, out FlockRelayPeer from, out int dataOffset, out int dataCount))
            {
                Arrive(from, packet, dataOffset, dataCount);
                // A peer heard before this end sent to it (the host hearing a new player) gets a channel too.
                if (ChannelFor(from) == 0)
                    WantChannel(from);
                return;
            }
            FlockRelayWire.Message message = FlockRelayWire.Read(packet, length);
            if (message == null)
                return;
            ushort answerClass = FlockRelayWire.ClassOf(message.Type);
            if (answerClass != FlockRelayWire.SuccessClass && answerClass != FlockRelayWire.ErrorClass)
                return;
            byte[] release = _releaseTransaction;
            if (release != null && SameTransaction(release, message.Transaction))
            {
                ReleaseAnswered(message, answerClass);
                return;
            }
            Request request = WaitingFor(message.Transaction);
            if (request == null)
                return;
            // Only an answer the relay made is believed: a success to a signed request must be signed with the login, and so must any answer that carries a signature.
            byte[] key = request.SignedWith;
            if (key != null && (message.IntegrityAt >= 0 || answerClass == FlockRelayWire.SuccessClass) && !FlockRelayWire.SignatureHolds(message, key))
                return;
            Answer(request, message, answerClass);
        }

        private void Answer(Request request, FlockRelayWire.Message message, ushort answerClass)
        {
            _waiting.Remove(request);
            if (answerClass == FlockRelayWire.SuccessClass)
            {
                request.Done(0, string.Empty, message);
                return;
            }
            int code = RefusalCodeOf(message, out string words);
            // The first answer to an unsigned request names the realm and nonce to sign with, and a stale nonce comes with a new one; twice at most.
            bool challenge = (code == 401 && request.SignedWith == null) || code == 438;
            if (challenge && request.NoncesTaken < 2 && TakeNonce(message))
            {
                request.NoncesTaken++;
                SendFirst(request);
                return;
            }
            request.Done(code, words, message);
        }

        // The relay's code for a refusal; one with no code is still a refusal.
        private static int RefusalCodeOf(FlockRelayWire.Message message, out string words)
        {
            message.Attributes.TryGetValue(FlockRelayWire.ErrorCodeAttribute, out byte[] error);
            int code = FlockRelayWire.ErrorCodeOf(error, out words);
            if (code != 0)
                return code;
            words = "a refusal naming no code";
            return RefusalWithNoCode;
        }

        // Signs every later request with the realm and nonce an answer carries; false when it carries none.
        private bool TakeNonce(FlockRelayWire.Message message)
        {
            string realm = message.Text(FlockRelayWire.RealmAttribute);
            string nonce = message.Text(FlockRelayWire.NonceAttribute);
            if (realm == null || nonce == null)
                return false;
            lock (_gate)
            {
                if (!string.Equals(realm, _realm, StringComparison.Ordinal))
                    _key = FlockRelayWire.LoginKey(_login.Username, realm, _login.Password);
                _realm = realm;
                _nonce = nonce;
            }
            Interlocked.Increment(ref _noncesTaken);
            return true;
        }

        // The release's answer: a stale nonce is answered once with the new one, and anything else lets the socket close.
        private void ReleaseAnswered(FlockRelayWire.Message message, ushort answerClass)
        {
            if (answerClass == FlockRelayWire.ErrorClass && !_releaseSentAgain && RefusalCodeOf(message, out _) == 438 && TakeNonce(message))
            {
                _releaseSentAgain = true;
                _releaseTransaction = SendRelease();
                return;
            }
            _releaseTransaction = null;
        }

        private Request WaitingFor(byte[] transaction)
        {
            foreach (Request request in _waiting)
            {
                if (SameTransaction(request.Transaction, transaction))
                    return request;
            }
            return null;
        }

        private static bool SameTransaction(byte[] one, byte[] other)
        {
            for (int i = 0; i < FlockRelayWire.TransactionLength; i++)
            {
                if (one[i] != other[i])
                    return false;
            }
            return true;
        }

        // A request goes out with a new transaction: first, and again once a realm or nonce is taken.
        private void SendFirst(Request request)
        {
            List<KeyValuePair<ushort, byte[]>> attributes = new List<KeyValuePair<ushort, byte[]>>(request.Attributes);
            byte[] key = null;
            lock (_gate)
            {
                if (_nonce != null)
                {
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.UsernameAttribute, System.Text.Encoding.UTF8.GetBytes(_login.Username)));
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.RealmAttribute, System.Text.Encoding.UTF8.GetBytes(_realm)));
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.NonceAttribute, System.Text.Encoding.UTF8.GetBytes(_nonce)));
                    key = _key;
                }
            }
            request.SignedWith = key;
            request.Transaction = FlockRelayWire.NewTransaction();
            request.Bytes = FlockRelayWire.Write((ushort)(request.Method | FlockRelayWire.RequestClass), request.Transaction, attributes, key);
            if (key != null && request.Method == FlockRelayWire.AllocateMethod)
                _reservationAsked = true;
            long now = Now();
            request.FirstSentAt = now;
            request.Sends = 1;
            request.NextSendAt = now + Ticks(FirstResend);
            SendBytes(request.Bytes);
            if (!_waiting.Contains(request))
                _waiting.Add(request);
        }

        // An answer may be lost on the way: the same bytes again, under the same transaction, so the relay knows it for a repeat.
        private void SendAgainOrGiveUp(long now)
        {
            for (int i = _waiting.Count - 1; i >= 0 && i < _waiting.Count; i--)
            {
                Request request = _waiting[i];
                if (now - request.FirstSentAt >= Ticks(_timing.AnswerWait))
                {
                    _waiting.RemoveAt(i);
                    request.Done(NoAnswer, "no answer", null);
                }
                else if (request.Sends < MostSends && now >= request.NextSendAt)
                {
                    SendBytes(request.Bytes);
                    request.NextSendAt = now + Ticks(FirstResend) * (1L << request.Sends);
                    request.Sends++;
                }
            }
        }

        private void Renew(long now)
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
                        Unpublish(renewal.Peer);
                    }
                }
                else if (now > renewal.GoodUntil)
                {
                    Lose(renewal.Kind == RenewalKind.Reservation
                        ? "the relay address was not renewed before the relay let it go"
                        : "the opening to the host's relay address was not renewed before the relay let it go");
                    return;
                }
                if (renewal.Sent || now < renewal.RenewAt)
                    continue;
                renewal.Sent = true;
                SendFirst(RequestFor(renewal));
            }
        }

        private Request RequestFor(Renewal renewal)
        {
            switch (renewal.Kind)
            {
                case RenewalKind.Reservation:
                    return new Request(FlockRelayWire.RefreshMethod, (code, words, answer) => Renewed(renewal, code, words, answer),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.LifetimeAttribute, FlockRelayWire.UInt32Value(ReservationSecondsAsked)));
                case RenewalKind.Opening:
                    return new Request(FlockRelayWire.CreatePermissionMethod, (code, words, answer) => Renewed(renewal, code, words, answer),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.XorPeerAddressAttribute, FlockRelayWire.AddressValue(new FlockRelayPeer(renewal.Address, 0))));
                default:
                    return new Request(FlockRelayWire.ChannelBindMethod, (code, words, answer) => Renewed(renewal, code, words, answer),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.ChannelNumberAttribute, FlockRelayWire.ChannelValue(renewal.Channel)),
                        new KeyValuePair<ushort, byte[]>(FlockRelayWire.XorPeerAddressAttribute, FlockRelayWire.AddressValue(renewal.Peer)));
            }
        }

        private void Renewed(Renewal renewal, int code, string words, FlockRelayWire.Message answer)
        {
            renewal.Sent = false;
            long now = Now();
            if (code == 0)
            {
                if (renewal.Kind == RenewalKind.Reservation)
                    KeepReservation(renewal, answer, now);
                else if (renewal.Kind == RenewalKind.Opening)
                {
                    renewal.GoodUntil = now + Ticks(_timing.OpeningLasts);
                    renewal.RenewAt = now + Ticks(_timing.RenewOpeningsEvery);
                }
                else
                {
                    if (!renewal.Bound)
                    {
                        renewal.Bound = true;
                        Publish(renewal.Peer, renewal.Channel);
                    }
                    renewal.GoodUntil = now + Ticks(_timing.ChannelLasts);
                    renewal.RenewAt = now + Ticks(_timing.RenewChannelsEvery);
                }
                return;
            }
            // No answer: tried again soon; what lapses meanwhile is judged by how long the relay keeps it.
            if (code == NoAnswer)
            {
                renewal.RenewAt = now + Ticks(_timing.RetryRenewalAfter);
                return;
            }
            if (renewal.Kind == RenewalKind.Channel)
            {
                renewal.Refused = true;
                if (renewal.Bound)
                {
                    renewal.Bound = false;
                    Unpublish(renewal.Peer);
                }
                if (!_toldChannelRefused)
                {
                    _toldChannelRefused = true;
                    _logger.LogWarning($"The relay refused a channel to {renewal.Peer} ({code} {words}); packets to it go as send indications, 32 bytes larger each.");
                }
                return;
            }
            Lose(renewal.Kind == RenewalKind.Reservation
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
            renewal.GoodUntil = now + Ticks(lasts);
            renewal.RenewAt = now + Ticks(half < _timing.LongestBetweenReservationRenewals ? half : _timing.LongestBetweenReservationRenewals);
        }

        // Something sent at least this often keeps the way to the relay open through a router or Docker's port forwarding.
        private void KeepAlive(long now)
        {
            if (now - Interlocked.Read(ref _lastSentAt) < Ticks(_timing.KeepAliveAfter))
                return;
            SendBytes(FlockRelayWire.Write((ushort)(FlockRelayWire.BindingMethod | FlockRelayWire.RequestClass), FlockRelayWire.NewTransaction(),
                new List<KeyValuePair<ushort, byte[]>>(), null));
        }

        // ---- answers that change what is open ----

        private void Reserved(int code, string words, FlockRelayWire.Message answer)
        {
            if (code == 0)
            {
                if (!answer.TryGetAddress(FlockRelayWire.XorRelayedAddressAttribute, out FlockRelayPeer address))
                {
                    Fail(FlockRelayFailure.Refused, $"The relay at {_login} answered with no IPv4 relay address.");
                    return;
                }
                _address = address;
                lock (_arrivedGate)
                    _arrived = new byte[PacketsHeld * MostBytesInAPacket];
                Renewal reservation = new Renewal(RenewalKind.Reservation);
                KeepReservation(reservation, answer, Now());
                _renewals.Add(reservation);
                if (Interlocked.CompareExchange(ref _state, StateOpen, StateOpening) == StateOpening)
                    _opened.TrySetResult(true);
                return;
            }
            if (code == NoAnswer)
                Fail(FlockRelayFailure.Unreachable, $"The relay at {_login} did not answer within {_timing.AnswerWait.TotalSeconds:0.#} s.");
            else if (code == 401 || code == 441)
                Fail(FlockRelayFailure.WrongLogin, $"The relay at {_login} refused the login Flock minted ({code} {words}).");
            else if (code == 486 || code == 508)
                Fail(FlockRelayFailure.Full, $"The relay at {_login} has no room for another address ({code} {words}).");
            else
                Fail(FlockRelayFailure.Refused, $"The relay at {_login} refused the reservation ({code} {words}).");
        }

        private void Opened(uint ip, int code, string words, TaskCompletionSource<bool> done)
        {
            lock (_gate)
                _callersWaiting.Remove(done);
            if (code == 0)
            {
                if (!IsOpenedTo(ip))
                {
                    uint[] before = _openedTo;
                    uint[] after = new uint[before.Length + 1];
                    Array.Copy(before, after, before.Length);
                    after[before.Length] = ip;
                    _openedTo = after;
                    long now = Now();
                    _renewals.Add(new Renewal(RenewalKind.Opening)
                    {
                        Address = ip,
                        GoodUntil = now + Ticks(_timing.OpeningLasts),
                        RenewAt = now + Ticks(_timing.RenewOpeningsEvery),
                    });
                }
                done.TrySetResult(true);
                return;
            }
            string host = new FlockRelayPeer(ip, 0).ToString();
            host = host.Substring(0, host.LastIndexOf(':'));
            if (code == NoAnswer)
                done.TrySetException(new FlockRelayException(FlockRelayFailure.Unreachable, $"The relay did not answer the opening to {host} within {_timing.AnswerWait.TotalSeconds:0.#} s."));
            else if (code == 403)
                done.TrySetException(new FlockRelayException(FlockRelayFailure.OpenRefused, $"The relay refused to open to {host} ({code} {words})."));
            else
                done.TrySetException(new FlockRelayException(FlockRelayFailure.Refused, $"The relay refused the opening to {host} ({code} {words})."));
        }

        private void Publish(FlockRelayPeer peer, ushort number)
        {
            Channel[] before = _channels;
            Channel[] after = new Channel[before.Length + 1];
            Array.Copy(before, after, before.Length);
            after[before.Length] = new Channel(peer, number);
            _channels = after;
        }

        private void Unpublish(FlockRelayPeer peer)
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

        // ---- shared by every thread ----

        private bool IsOpenedTo(uint ip)
        {
            uint[] opened = _openedTo;
            for (int i = 0; i < opened.Length; i++)
            {
                if (opened[i] == ip)
                    return true;
            }
            return false;
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

        // Asked once a peer, and looked up without a lock; past the most channels a relay connection keeps, packets go as send indications.
        private void WantChannel(FlockRelayPeer peer)
        {
            if (HasWanted(_channelsWanted, peer))
                return;
            lock (_gate)
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
            lock (_arrivedGate)
            {
                if (_arrived == null || _arrivedCount == PacketsHeld)
                {
                    Interlocked.Increment(ref _packetsDropped);
                    return;
                }
                int slot = (_arrivedFirst + _arrivedCount) % PacketsHeld;
                Buffer.BlockCopy(packet, offset, _arrived, slot * MostBytesInAPacket, count);
                _arrivedLengths[slot] = count;
                _arrivedFrom[slot] = from;
                _arrivedCount++;
            }
        }

        private void SendBytes(byte[] bytes)
        {
            try
            {
                _socket.Send(bytes, 0, bytes.Length, SocketFlags.None);
                Interlocked.Exchange(ref _lastSentAt, Now());
            }
            catch (SocketException)
            {
                // Judged by the answer wait.
            }
            catch (ObjectDisposedException)
            {
                // Closed meanwhile; the relay thread ends at its next turn.
            }
        }

        private void Fail(string reason, string message) => Stop(StateFailed, reason, message);

        private void Lose(string what) => Stop(StateFailed, FlockRelayFailure.Lost, $"The relay connection to {_login} was lost: {what}.");

        private void Stop(int finalState, string reason, string message)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 1)
                return;
            // The reason first, so a reader that sees the state stopped finds it; swapped in one step, so an answer can no longer open it.
            _failureReason = reason;
            bool wasOpen = Interlocked.Exchange(ref _state, finalState) == StateOpen;
            // The release goes out from this thread at once, so it is sent even while the game quits; the relay thread closes the
            // socket once it is answered, or after a moment, sending it again if the relay's nonce went stale.
            if (_reservationAsked)
            {
                Interlocked.Exchange(ref _closeSocketAt, Now() + Ticks(_timing.ReleaseAnswerWait));
                _releaseTransaction = SendRelease();
            }
            Volatile.Write(ref _stopSettled, 1);
            if (reason != null && wasOpen)
                _logger.LogWarning(message);
            FlockRelayException failure = reason == null ? null : new FlockRelayException(reason, message);
            if (failure == null)
                _opened.TrySetCanceled();
            else
                _opened.TrySetException(failure);
            List<TaskCompletionSource<bool>> waiting;
            lock (_gate)
            {
                waiting = new List<TaskCompletionSource<bool>>(_callersWaiting);
                _callersWaiting.Clear();
            }
            foreach (TaskCompletionSource<bool> caller in waiting)
            {
                if (failure == null)
                    caller.TrySetCanceled();
                else
                    caller.TrySetException(failure);
            }
        }

        // A renewal asking for 0 s gives the address back at once; one with no address to give back is refused, which harms nothing. Its transaction, or null when it could not go.
        private byte[] SendRelease()
        {
            List<KeyValuePair<ushort, byte[]>> attributes = new List<KeyValuePair<ushort, byte[]>>
            {
                new KeyValuePair<ushort, byte[]>(FlockRelayWire.LifetimeAttribute, FlockRelayWire.UInt32Value(0)),
            };
            byte[] key;
            // Asked only once a signed reservation went out, so the realm and nonce are known.
            lock (_gate)
            {
                attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.UsernameAttribute, System.Text.Encoding.UTF8.GetBytes(_login.Username)));
                attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.RealmAttribute, System.Text.Encoding.UTF8.GetBytes(_realm)));
                attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.NonceAttribute, System.Text.Encoding.UTF8.GetBytes(_nonce)));
                key = _key;
            }
            try
            {
                byte[] transaction = FlockRelayWire.NewTransaction();
                byte[] release = FlockRelayWire.Write((ushort)(FlockRelayWire.RefreshMethod | FlockRelayWire.RequestClass), transaction, attributes, key);
                _socket.Send(release, 0, release.Length, SocketFlags.None);
                return transaction;
            }
            catch (Exception)
            {
                // Best effort: an address not given back lapses on the relay within its lifetime.
                return null;
            }
        }

        // Only a connection that has stopped is handed back not open: closed by its owner, or failed for a reason.
        private FlockRelayException NotOpen()
            => _failureReason != null
                ? new FlockRelayException(_failureReason, $"The relay connection stopped ({_failureReason}).")
                : new FlockRelayException(FlockRelayFailure.Closed, "The relay connection was closed.");

        // The caller may stop waiting; the request itself goes on, and what it opens is kept.
        private static async Task WaitAsync(Task work, CancellationToken cancellationToken)
        {
            FlockMultiplayerSessions.LetRun(work);
            if (!await FlockWaiting.FinishedBeforeGivenUpAsync(work, cancellationToken))
                throw new OperationCanceledException(cancellationToken);
            await work;
        }

        private static long Now() => Stopwatch.GetTimestamp();

        private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

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

        // A request waiting for its answer: what it asks, the bytes last sent, and what to do with the answer on the relay thread.
        private sealed class Request
        {
            internal Request(ushort method, Action<int, string, FlockRelayWire.Message> done, params KeyValuePair<ushort, byte[]>[] attributes)
            {
                Method = method;
                Done = done;
                Attributes = attributes;
            }

            internal ushort Method { get; }
            internal Action<int, string, FlockRelayWire.Message> Done { get; }
            internal KeyValuePair<ushort, byte[]>[] Attributes { get; }
            internal byte[] SignedWith;
            internal byte[] Transaction;
            internal byte[] Bytes;
            internal long FirstSentAt;
            internal long NextSendAt;
            internal int Sends;
            internal int NoncesTaken;
        }
    }
}
