using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Flock.Providers
{
    /// <summary>The relay's requests and their answers: signed once the relay names a realm and nonce, sent again while unanswered, given up on after the answer wait, and the release when the connection stops.</summary>
    internal sealed class FlockRelayRequests
    {
        /// <summary>The code a request is done with when the relay never answered it.</summary>
        internal const int NoAnswer = -1;
        // A refusal that names no code is still a refusal, never the success 0 stands for.
        private const int RefusalWithNoCode = -2;
        // A request is sent again 0.5 s, 1.5 s and 3.5 s after it first went, then given up on once the answer wait is over.
        private const int MostSends = 4;
        private static readonly TimeSpan FirstResend = TimeSpan.FromMilliseconds(500);

        private readonly FlockRelayLogin _login;
        private readonly FlockRelaySocket _socket;
        private readonly FlockRelayConnection.Timing _timing;

        // What requests are signed with; the thread that stops the connection reads it too, so all under _signingGate.
        private readonly object _signingGate = new object();
        private string _realm;
        private string _nonce;
        private byte[] _key;
        private int _noncesTaken;
        private volatile bool _reservationAsked;

        // The relay thread's own.
        private readonly List<FlockRelayRequest> _waiting = new List<FlockRelayRequest>();

        // The release on its way; the relay thread sends it again once on a stale nonce.
        private volatile byte[] _releaseTransaction;
        private bool _releaseSentAgain;

        internal FlockRelayRequests(FlockRelayLogin login, FlockRelaySocket socket, FlockRelayConnection.Timing timing)
        {
            _login = login;
            _socket = socket;
            _timing = timing;
        }

        /// <summary>Realms and nonces the relay handed over to sign with: its login challenge, then each stale nonce answered.</summary>
        internal int NoncesTaken => Volatile.Read(ref _noncesTaken);

        /// <summary>True once a signed reservation went out, so there may be an address to give back.</summary>
        internal bool ReservationAsked => _reservationAsked;

        /// <summary>True while the release went out and its answer has not come.</summary>
        internal bool ReleaseWaitingForAnswer => _releaseTransaction != null;

        /// <summary>Sends a request with a new transaction: first, and again once a realm or nonce is taken. The relay thread's alone.</summary>
        internal void Send(FlockRelayRequest request)
        {
            List<KeyValuePair<ushort, byte[]>> attributes = new List<KeyValuePair<ushort, byte[]>>(request.Attributes);
            byte[] key = null;
            lock (_signingGate)
            {
                if (_nonce != null)
                {
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.UsernameAttribute, Encoding.UTF8.GetBytes(_login.Username)));
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.RealmAttribute, Encoding.UTF8.GetBytes(_realm)));
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.NonceAttribute, Encoding.UTF8.GetBytes(_nonce)));
                    key = _key;
                }
            }
            request.SignedWith = key;
            request.Transaction = FlockRelayWire.NewTransaction();
            request.Bytes = FlockRelayWire.Write((ushort)(request.Method | FlockRelayWire.RequestClass), request.Transaction, attributes, key);
            if (key != null && request.Method == FlockRelayWire.AllocateMethod)
                _reservationAsked = true;
            long now = FlockRelayClock.Now();
            request.FirstSentAt = now;
            request.Sends = 1;
            request.NextSendAt = now + FlockRelayClock.Ticks(FirstResend);
            SendBytes(request.Bytes);
            if (!_waiting.Contains(request))
                _waiting.Add(request);
        }

        /// <summary>An answer may be lost on the way: the same bytes again, under the same transaction, so the relay knows it for a repeat; given up on after the answer wait. The relay thread's alone.</summary>
        internal void SendAgainOrGiveUp(long now)
        {
            for (int i = _waiting.Count - 1; i >= 0 && i < _waiting.Count; i--)
            {
                FlockRelayRequest request = _waiting[i];
                if (now - request.FirstSentAt >= FlockRelayClock.Ticks(_timing.AnswerWait))
                {
                    _waiting.RemoveAt(i);
                    request.Done(NoAnswer, "no answer", null);
                }
                else if (request.Sends < MostSends && now >= request.NextSendAt)
                {
                    SendBytes(request.Bytes);
                    request.NextSendAt = now + FlockRelayClock.Ticks(FirstResend) * (1L << request.Sends);
                    request.Sends++;
                }
            }
        }

        /// <summary>Something sent at least this often keeps the way to the relay open through a router or Docker's port forwarding. The relay thread's alone.</summary>
        internal void KeepAlive(long now)
        {
            if (now - _socket.LastSentAt < FlockRelayClock.Ticks(_timing.KeepAliveAfter))
                return;
            SendBytes(FlockRelayWire.Write((ushort)(FlockRelayWire.BindingMethod | FlockRelayWire.RequestClass), FlockRelayWire.NewTransaction(),
                new List<KeyValuePair<ushort, byte[]>>(), null));
        }

        /// <summary>Takes one of the relay's own messages: the release's answer, or an answer to a request waiting. The relay thread's alone.</summary>
        internal void Take(byte[] packet, int length)
        {
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
            FlockRelayRequest request = WaitingFor(message.Transaction);
            if (request == null)
                return;
            // Only an answer the relay made is believed: a success to a signed request must be signed with the login, and so must any answer that carries a signature.
            byte[] key = request.SignedWith;
            if (key != null && (message.IntegrityAt >= 0 || answerClass == FlockRelayWire.SuccessClass) && !FlockRelayWire.SignatureHolds(message, key))
                return;
            Answer(request, message, answerClass);
        }

        /// <summary>Gives the address back, sent from the calling thread so it goes out even while the game quits; asked only once a signed reservation went out.</summary>
        internal void Release() => _releaseTransaction = SendRelease();

        private void Answer(FlockRelayRequest request, FlockRelayWire.Message message, ushort answerClass)
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
                Send(request);
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
            lock (_signingGate)
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

        private FlockRelayRequest WaitingFor(byte[] transaction)
        {
            foreach (FlockRelayRequest request in _waiting)
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

        private void SendBytes(byte[] bytes) => _socket.TrySend(bytes, 0, bytes.Length);

        // A renewal asking for 0 s gives the address back at once; one with no address to give back is refused, which harms nothing. Its transaction, or null when it could not go.
        private byte[] SendRelease()
        {
            List<KeyValuePair<ushort, byte[]>> attributes = new List<KeyValuePair<ushort, byte[]>>
            {
                new KeyValuePair<ushort, byte[]>(FlockRelayWire.LifetimeAttribute, FlockRelayWire.UInt32Value(0)),
            };
            byte[] key;
            byte[] transaction = FlockRelayWire.NewTransaction();
            byte[] release;
            try
            {
                // Asked only once a signed reservation went out, so the realm and nonce are known.
                lock (_signingGate)
                {
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.UsernameAttribute, Encoding.UTF8.GetBytes(_login.Username)));
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.RealmAttribute, Encoding.UTF8.GetBytes(_realm)));
                    attributes.Add(new KeyValuePair<ushort, byte[]>(FlockRelayWire.NonceAttribute, Encoding.UTF8.GetBytes(_nonce)));
                    key = _key;
                }
                release = FlockRelayWire.Write((ushort)(FlockRelayWire.RefreshMethod | FlockRelayWire.RequestClass), transaction, attributes, key);
            }
            catch (Exception)
            {
                // Best effort: an address not given back lapses on the relay within its lifetime.
                return null;
            }
            return _socket.TrySend(release, 0, release.Length) ? transaction : null;
        }
    }

    /// <summary>A request waiting for its answer: what it asks, the bytes last sent, and what to do with the answer on the relay thread.</summary>
    internal sealed class FlockRelayRequest
    {
        internal FlockRelayRequest(ushort method, Action<int, string, FlockRelayWire.Message> done, params KeyValuePair<ushort, byte[]>[] attributes)
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
