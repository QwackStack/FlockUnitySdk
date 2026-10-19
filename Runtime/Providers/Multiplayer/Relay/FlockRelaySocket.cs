using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Flock.Providers
{
    /// <summary>The UDP socket to one relay server (dual-stack where the platform allows): connected to it, and never holding a sender.</summary>
    internal sealed class FlockRelaySocket
    {
        private readonly Socket _socket;

        internal FlockRelaySocket(IPEndPoint server)
        {
            // Connected, so only the relay's own packets are read, and sends need no address of their own.
            _socket = FlockUdpSockets.OpenConnectedTo(server);
            try
            {
                // A full send buffer drops a packet rather than holding the game's thread.
                _socket.Blocking = false;
            }
            catch
            {
                _socket.Close();
                throw;
            }
        }

        /// <summary>Takes a socket already connected to the relay server, such as one the server was heard to answer.</summary>
        internal FlockRelaySocket(Socket connected)
        {
            _socket = connected;
            _socket.Blocking = false;
        }

        /// <summary>A socket connected to <paramref name="server"/> that the server answered: a binding request goes out from <paramref name="socketsAtOnce"/> sockets at once, round after round, and the first one answered is kept. Some relay servers answer only some of the ports a device sends from (Cloudflare's, measured from two networks in 2026-10: about one in three, each port every time or never). Null when none was answered; blocks, so it runs off the main thread.</summary>
        internal static Socket FindOneTheServerAnswers(IPEndPoint server, int socketsAtOnce, int rounds, TimeSpan roundWait, CancellationToken cancellationToken)
        {
            byte[] received = new byte[2048];
            for (int round = 0; round < rounds; round++)
            {
                List<Socket> trying = new List<Socket>();
                List<byte[]> transactions = new List<byte[]>();
                Socket kept = null;
                try
                {
                    for (int i = 0; i < socketsAtOnce; i++)
                    {
                        Socket socket;
                        try
                        {
                            socket = FlockUdpSockets.OpenConnectedTo(server);
                        }
                        catch (SocketException)
                        {
                            // A socket that cannot be made or aimed is left out (it closed itself); with none, the caller's own socket says why.
                            continue;
                        }
                        trying.Add(socket);
                        transactions.Add(FlockRelayWire.NewTransaction());
                    }
                    if (trying.Count == 0)
                        return null;
                    long started = FlockRelayClock.Now();
                    long sendAgainAt = started + FlockRelayClock.Ticks(roundWait) / 2;
                    long until = started + FlockRelayClock.Ticks(roundWait);
                    SendBindings(trying, transactions);
                    bool sentAgain = false;
                    while (FlockRelayClock.Now() < until)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!sentAgain && FlockRelayClock.Now() >= sendAgainAt)
                        {
                            // A request or its answer may be lost on the way.
                            SendBindings(trying, transactions);
                            sentAgain = true;
                        }
                        List<Socket> readable = new List<Socket>(trying);
                        try
                        {
                            Socket.Select(readable, null, null, PollMicroseconds);
                        }
                        catch (SocketException)
                        {
                            // The sockets cannot be waited on: no search, and the caller reserves from a new socket as before.
                            return null;
                        }
                        foreach (Socket socket in readable)
                        {
                            try
                            {
                                // A connected socket hears the server alone, so anything that arrives is its answer.
                                socket.Receive(received);
                            }
                            catch (SocketException)
                            {
                                // An earlier send met a closed port, or the answer was larger than any binding's.
                                continue;
                            }
                            kept = socket;
                            return socket;
                        }
                    }
                }
                finally
                {
                    foreach (Socket socket in trying)
                    {
                        if (socket != kept)
                            socket.Close();
                    }
                }
            }
            return null;
        }

        private const int PollMicroseconds = 20000;

        private static void SendBindings(List<Socket> sockets, List<byte[]> transactions)
        {
            for (int i = 0; i < sockets.Count; i++)
            {
                byte[] request = FlockDirectAddresses.StunRequest(transactions[i]);
                try
                {
                    sockets[i].Send(request, 0, request.Length, SocketFlags.None);
                }
                catch (SocketException)
                {
                    // That socket is not heard; the others may be.
                }
            }
        }

        internal bool BlocksForTesting => _socket.Blocking;

        internal AddressFamily FamilyForTesting => _socket.AddressFamily;

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
            return true;
        }

        /// <summary>Waits up to <paramref name="microseconds"/> for something to read; true when something is there.</summary>
        internal bool WaitToRead(int microseconds) => _socket.Poll(microseconds, SelectMode.SelectRead);

        internal int Receive(byte[] into) => _socket.Receive(into);

        internal void Close() => _socket.Close();
    }
}
