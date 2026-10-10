using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Flock.Providers
{
    /// <summary>The UDP socket to one relay server: connected to it, never holding a sender, and remembering when it last sent.</summary>
    internal sealed class FlockRelaySocket
    {
        private readonly Socket _socket;
        private long _lastSentAt;

        internal FlockRelaySocket(IPEndPoint server)
        {
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
            _lastSentAt = FlockRelayClock.Now();
        }

        /// <summary>When something last went out, as a <see cref="FlockRelayClock"/> reading.</summary>
        internal long LastSentAt => Interlocked.Read(ref _lastSentAt);

        internal bool BlocksForTesting => _socket.Blocking;

        /// <summary>The port this socket sends from, which the relay sees.</summary>
        internal int LocalPort => ((IPEndPoint)_socket.LocalEndPoint).Port;

        internal int Available => _socket.Available;

        /// <summary>Sends one packet, from any thread; false when the socket refused it or is closed, which the answer wait judges.</summary>
        internal bool TrySend(byte[] bytes, int offset, int count)
        {
            try
            {
                _socket.Send(bytes, offset, count, SocketFlags.None);
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            Interlocked.Exchange(ref _lastSentAt, FlockRelayClock.Now());
            return true;
        }

        /// <summary>Waits up to <paramref name="microseconds"/> for something to read; true when something is there.</summary>
        internal bool WaitToRead(int microseconds) => _socket.Poll(microseconds, SelectMode.SelectRead);

        internal int Receive(byte[] into) => _socket.Receive(into);

        internal void Close() => _socket.Close();
    }
}
