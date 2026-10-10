using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>One address on Flock's relay for one session, kept alive on a thread of its own; it hands raw packets over without waiting on the relay or making garbage.</summary>
    // The connection's life and its thread; requests in FlockRelayRequests, renewals in FlockRelayRenewals, packets in FlockRelayPackets.
    internal sealed class FlockRelayConnection
    {
        /// <summary>The most bytes one packet may carry, the most Unity Transport sends in one.</summary>
        internal const int MostBytesInAPacket = FlockRelayPackets.MostBytesInAPacket;
        private const int LongestPollMicroseconds = 20000;

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
        private readonly FlockRelaySocket _socket;
        private readonly FlockRelayRequests _requests;
        private readonly FlockRelayPackets _packets;
        private readonly FlockRelayRenewals _renewals;
        private readonly Thread _thread;
        private readonly TaskCompletionSource<bool> _opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _state = StateOpening;
        private int _stopping;
        // Set once a stop has sent what it sends; only then may the relay thread close the socket.
        private int _stopSettled;
        private volatile string _failureReason;
        private FlockRelayPeer _address;
        // When the socket closes whatever the release's answer.
        private long _closeSocketAt;

        // Handed from the game's threads to the relay thread; under _gate.
        private readonly object _gate = new object();
        private readonly List<FlockRelayRequest> _handedOver = new List<FlockRelayRequest>();
        private readonly List<TaskCompletionSource<bool>> _callersWaiting = new List<TaskCompletionSource<bool>>();

        // The relay thread's own.
        private byte[] _received;

        private FlockRelayConnection(FlockRelayLogin login, IPEndPoint server, Timing timing, IFlockLogger logger)
        {
            _login = login;
            _timing = timing;
            _logger = logger;
            _socket = new FlockRelaySocket(server);
            _requests = new FlockRelayRequests(login, _socket, timing);
            _packets = new FlockRelayPackets(_socket);
            _renewals = new FlockRelayRenewals(_requests, _packets, timing, logger, Lose);
            _handedOver.Add(new FlockRelayRequest(FlockRelayWire.AllocateMethod, Reserved,
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
        internal long PacketsDropped => _packets.PacketsDropped;

        /// <summary>Realms and nonces the relay handed over to sign with: its login challenge, then each stale nonce answered.</summary>
        internal int NoncesTakenForTesting => _requests.NoncesTaken;

        /// <summary>The relay server this address is on, as "name:port" from the login Flock minted; players reserve on the server the host uses.</summary>
        internal string Server => _login.ToString();

        internal int LocalPortForTesting => _socket.LocalPort;

        internal bool HandsArrivalsOverForTesting => _packets.HandsArrivalsOver;

        /// <summary>Whether the block arrived packets wait in has been made, which happens when the first one is kept.</summary>
        internal bool KeepsPacketsForTesting => _packets.KeepsPacketsForTesting;

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
            if (_packets.IsOpenedTo(hostAddress.Address))
                return Task.CompletedTask;
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint ip = hostAddress.Address;
            FlockRelayRequest request = new FlockRelayRequest(FlockRelayWire.CreatePermissionMethod, (code, words, answer) => Opened(ip, code, words, done),
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
        internal bool Send(FlockRelayPeer to, byte[] data, int offset, int count) => IsOpen && _packets.Send(to, data, offset, count);

        /// <summary>Takes the oldest packet that arrived, copied into <paramref name="into"/> (at least <see cref="MostBytesInAPacket"/> bytes); false when none is waiting.</summary>
        internal bool TryReceive(byte[] into, out int count, out FlockRelayPeer from) => _packets.TryReceive(into, out count, out from);

        /// <summary>Packets to and from <paramref name="peer"/> ride on a relay channel (4 bytes each rather than 36), renewed until closed. Asked for the host by a player, and by the host for each player it let in.</summary>
        internal void KeepChannelTo(FlockRelayPeer peer) => _packets.WantChannel(peer);

        /// <summary>Hands every packet that arrives to <paramref name="receiver"/> on the relay thread, which must not wait; false when another receiver has them.</summary>
        internal bool HandArrivalsTo(Action<FlockRelayPeer, byte[], int, int> receiver) => _packets.HandArrivalsTo(receiver);

        /// <summary>Keeps arrivals for <see cref="TryReceive"/> again, if <paramref name="receiver"/> is the one they are handed to.</summary>
        internal void StopHandingArrivalsTo(Action<FlockRelayPeer, byte[], int, int> receiver) => _packets.StopHandingArrivalsTo(receiver);

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
                        if (Volatile.Read(ref _stopSettled) != 0 && (!_requests.ReleaseWaitingForAnswer || FlockRelayClock.Now() >= Interlocked.Read(ref _closeSocketAt)))
                            return;
                        if (_socket.WaitToRead(LongestPollMicroseconds))
                            ReadEverythingWaiting();
                        continue;
                    }
                    TakeHandedOver();
                    if (_socket.WaitToRead(LongestPollMicroseconds))
                        ReadEverythingWaiting();
                    long now = FlockRelayClock.Now();
                    _requests.SendAgainOrGiveUp(now);
                    _renewals.RenewDue(now);
                    _requests.KeepAlive(now);
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
            List<FlockRelayRequest> requests = null;
            lock (_gate)
            {
                if (_handedOver.Count > 0)
                {
                    requests = new List<FlockRelayRequest>(_handedOver);
                    _handedOver.Clear();
                }
            }
            // A new peer gets the next channel; its renewal, due at once, binds it.
            while (_packets.TryGiveNextChannel(out FlockRelayPeer peer, out ushort channel))
                _renewals.BindChannel(peer, channel, FlockRelayClock.Now());
            if (requests == null)
                return;
            foreach (FlockRelayRequest request in requests)
                _requests.Send(request);
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
                if (!_packets.TryTake(_received, length))
                    _requests.Take(_received, length);
            }
            while (_socket.Available > 0);
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
                _renewals.KeepReservation(answer, FlockRelayClock.Now());
                if (Interlocked.CompareExchange(ref _state, StateOpen, StateOpening) == StateOpening)
                    _opened.TrySetResult(true);
                return;
            }
            if (code == FlockRelayRequests.NoAnswer)
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
                if (_packets.AddOpening(ip))
                    _renewals.KeepOpening(ip, FlockRelayClock.Now());
                done.TrySetResult(true);
                return;
            }
            string host = new FlockRelayPeer(ip, 0).Ip;
            if (code == FlockRelayRequests.NoAnswer)
                done.TrySetException(new FlockRelayException(FlockRelayFailure.Unreachable, $"The relay did not answer the opening to {host} within {_timing.AnswerWait.TotalSeconds:0.#} s."));
            else if (code == 403)
                done.TrySetException(new FlockRelayException(FlockRelayFailure.OpenRefused, $"The relay refused to open to {host} ({code} {words})."));
            else
                done.TrySetException(new FlockRelayException(FlockRelayFailure.Refused, $"The relay refused the opening to {host} ({code} {words})."));
        }

        // ---- stopping ----

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
            if (_requests.ReservationAsked)
            {
                Interlocked.Exchange(ref _closeSocketAt, FlockRelayClock.Now() + FlockRelayClock.Ticks(_timing.ReleaseAnswerWait));
                _requests.Release();
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
    }
}
