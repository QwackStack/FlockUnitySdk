using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using Flock.Logging;

namespace Flock.Providers
{
    /// <summary>A TCP or TLS stream to one relay server, for a network that blocks UDP to it. A reading thread keeps what arrives and whole messages are cut from it; a sending thread writes what was handed over, so a send never waits on the network, and a full queue drops a whole packet as a UDP socket's full buffer does. Nothing is made per packet.</summary>
    internal sealed class FlockRelayStreamSocket : FlockRelaySocket
    {
        // Room for what arrived and is not read yet, and for what waits to be written: each holds well over a hundred full packets.
        private const int ArrivedRoom = 256 * 1024;
        private const int SendingRoom = 256 * 1024;
        private const int ChunkBytes = 16 * 1024;
        private const int ThreadStackBytes = 256 * 1024;

        /// <summary>Checks the relay's certificate in place of the platform's own checks; null (always, outside tests) leaves them to the platform.</summary>
        internal static RemoteCertificateValidationCallback CertificateCheckForTesting;

        private readonly Socket _socket;
        private readonly Stream _stream;
        private readonly int _localPort;
        private readonly AddressFamily _family;

        private readonly object _arrivedGate = new object();
        private readonly byte[] _arrived = new byte[ArrivedRoom];
        private int _arrivedFirst;
        private int _arrivedCount;

        private readonly object _sendingGate = new object();
        private readonly byte[] _sending = new byte[SendingRoom];
        private int _sendingFirst;
        private int _sendingCount;

        private string _endedBecause;
        private int _closed;

        private FlockRelayStreamSocket(Socket socket, Stream stream)
        {
            _socket = socket;
            _stream = stream;
            _localPort = ((IPEndPoint)socket.LocalEndPoint).Port;
            _family = socket.AddressFamily;
            new Thread(Read, ThreadStackBytes) { IsBackground = true, Name = ReadingThreadName }.Start();
            new Thread(Write, ThreadStackBytes) { IsBackground = true, Name = SendingThreadName }.Start();
        }

        internal const string ReadingThreadName = "Flock relay stream reading";
        internal const string SendingThreadName = "Flock relay stream sending";

        /// <summary>Connects to <paramref name="server"/> and, over TLS, checks the relay's certificate is trusted and names <paramref name="host"/>, all within <paramref name="wait"/> each; throws a SocketException, an IOException, an AuthenticationException or OperationCanceledException naming why. Blocks, so it runs off the main thread.</summary>
        internal static FlockRelayStreamSocket Open(IPEndPoint server, string host, bool overTls, TimeSpan wait, CancellationToken cancellationToken)
        {
            Socket socket = FlockSockets.OpenStreamConnectedTo(server, wait, cancellationToken);
            try
            {
                // A game's small packets go at once rather than waiting to be joined.
                socket.NoDelay = true;
                Stream stream = new NetworkStream(socket, true);
                if (overTls)
                {
                    SslStream tls = new SslStream(stream, false, CertificateCheckForTesting);
                    // The handshake is bounded by the socket's own waits, and cancelling closes the socket under it.
                    socket.ReceiveTimeout = (int)wait.TotalMilliseconds;
                    socket.SendTimeout = (int)wait.TotalMilliseconds;
                    using (cancellationToken.Register(socket.Close))
                        tls.AuthenticateAsClient(host);
                    cancellationToken.ThrowIfCancellationRequested();
                    socket.ReceiveTimeout = 0;
                    socket.SendTimeout = 0;
                    stream = tls;
                }
                return new FlockRelayStreamSocket(socket, stream);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                socket.Close();
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        internal override bool LosesPackets => false;

        internal override string EndedBecause => Volatile.Read(ref _endedBecause);

        internal override int LocalPort => _localPort;

        internal override AddressFamily FamilyForTesting => _family;

        internal override bool HasMoreWaiting
        {
            get
            {
                lock (_arrivedGate)
                    return WholeMessageLength() != 0;
            }
        }

        internal override bool TrySend(byte[] bytes, int offset, int count)
        {
            lock (_sendingGate)
            {
                // Whole or not at all: part of a message would leave every later one misread.
                if (Volatile.Read(ref _closed) != 0 || EndedBecause != null || SendingRoom - _sendingCount < count)
                    return false;
                Put(_sending, _sendingFirst, ref _sendingCount, bytes, offset, count);
                Monitor.Pulse(_sendingGate);
                return true;
            }
        }

        internal override bool WaitToRead(int microseconds)
        {
            lock (_arrivedGate)
            {
                if (WholeMessageLength() != 0)
                    return true;
                Monitor.Wait(_arrivedGate, Math.Max(1, microseconds / 1000));
                return WholeMessageLength() != 0;
            }
        }

        internal override int Receive(byte[] into)
        {
            lock (_arrivedGate)
            {
                int length = WholeMessageLength();
                if (length == 0)
                    return 0;
                if (length < 0)
                {
                    // Not a relay message: nothing after it can be told apart, so the stream is done with.
                    _arrivedCount = 0;
                    End("the relay sent something that is not a relay message");
                    return 0;
                }
                if (length > into.Length)
                {
                    Skip(length);
                    return 0;
                }
                Take(_arrived, ref _arrivedFirst, ref _arrivedCount, into, length);
                Monitor.PulseAll(_arrivedGate);
                return length;
            }
        }

        internal override void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
                return;
            lock (_arrivedGate)
                Monitor.PulseAll(_arrivedGate);
            lock (_sendingGate)
                Monitor.PulseAll(_sendingGate);
            // Closing the socket ends a read or write under way on the other threads; the relay ends the reservation with the connection.
            _socket.Close();
            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // Already broken.
            }
        }

        // ---- the reading thread ----

        private void Read()
        {
            byte[] chunk = new byte[ChunkBytes];
            try
            {
                while (true)
                {
                    int count = _stream.Read(chunk, 0, chunk.Length);
                    if (count <= 0)
                    {
                        End("the relay closed the connection");
                        return;
                    }
                    lock (_arrivedGate)
                    {
                        // A reader behind by the whole room holds the stream back, as TCP holds back its sender; nothing is dropped.
                        while (ArrivedRoom - _arrivedCount < count && Volatile.Read(ref _closed) == 0)
                            Monitor.Wait(_arrivedGate, 100);
                        if (Volatile.Read(ref _closed) != 0)
                            return;
                        Put(_arrived, _arrivedFirst, ref _arrivedCount, chunk, 0, count);
                        Monitor.PulseAll(_arrivedGate);
                    }
                }
            }
            catch (Exception failure)
            {
                End($"the connection to the relay broke ({failure.GetType().Name}: {FlockExceptionText.OneLineMessageOf(failure)})");
            }
        }

        // ---- the sending thread ----

        private void Write()
        {
            byte[] chunk = new byte[ChunkBytes];
            try
            {
                while (true)
                {
                    int count;
                    lock (_sendingGate)
                    {
                        while (_sendingCount == 0 && Volatile.Read(ref _closed) == 0 && EndedBecause == null)
                            Monitor.Wait(_sendingGate);
                        if (_sendingCount == 0 || EndedBecause != null)
                            return;
                        count = Math.Min(_sendingCount, chunk.Length);
                        Take(_sending, ref _sendingFirst, ref _sendingCount, chunk, count);
                    }
                    _stream.Write(chunk, 0, count);
                }
            }
            catch (Exception failure)
            {
                End($"the connection to the relay broke ({failure.GetType().Name}: {FlockExceptionText.OneLineMessageOf(failure)})");
            }
        }

        // The first reason a stream ended is kept; a stream this side closed has not ended for the relay's reasons.
        private void End(string reason)
        {
            if (Volatile.Read(ref _closed) != 0)
                return;
            Interlocked.CompareExchange(ref _endedBecause, reason, null);
            lock (_arrivedGate)
                Monitor.PulseAll(_arrivedGate);
            lock (_sendingGate)
                Monitor.PulseAll(_sendingGate);
        }

        // ---- whole messages in what arrived; under _arrivedGate ----

        // A channel frame is its 4-byte header and data padded to 4 bytes; a STUN message its 20-byte header and length. 0 while the
        // first one has not all arrived; -1 when the stream holds neither.
        private int WholeMessageLength()
        {
            if (_arrivedCount < 4)
                return 0;
            int first = At(0);
            int length = (At(2) << 8) | At(3);
            int whole;
            if ((first & 0xC0) == 0x40)
                whole = FlockRelayWire.ChannelFrameHeaderLength + length + (4 - length % 4) % 4;
            else if ((first & 0xC0) == 0)
                whole = FlockRelayWire.HeaderLength + length;
            else
                return -1;
            return _arrivedCount >= whole ? whole : 0;
        }

        private byte At(int index) => _arrived[(_arrivedFirst + index) % ArrivedRoom];

        // A message larger than the reader's room is passed over.
        private void Skip(int length)
        {
            _arrivedFirst = (_arrivedFirst + length) % ArrivedRoom;
            _arrivedCount -= length;
            Monitor.PulseAll(_arrivedGate);
        }

        private static void Put(byte[] ring, int first, ref int count, byte[] from, int offset, int length)
        {
            int at = (first + count) % ring.Length;
            int toEnd = Math.Min(length, ring.Length - at);
            Buffer.BlockCopy(from, offset, ring, at, toEnd);
            Buffer.BlockCopy(from, offset + toEnd, ring, 0, length - toEnd);
            count += length;
        }

        private static void Take(byte[] ring, ref int first, ref int count, byte[] into, int length)
        {
            int toEnd = Math.Min(length, ring.Length - first);
            Buffer.BlockCopy(ring, first, into, 0, toEnd);
            Buffer.BlockCopy(ring, 0, into, toEnd, length - toEnd);
            first = (first + length) % ring.Length;
            count -= length;
        }
    }
}
