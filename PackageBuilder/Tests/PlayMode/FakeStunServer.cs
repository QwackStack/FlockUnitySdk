using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Flock.Tests.PlayMode
{
    // A STUN server on this machine's loopback: answers each binding request with the public address a test chooses, or stays silent.
    internal sealed class FakeStunServer : IDisposable
    {
        private const uint MagicCookie = 0x2112A442;

        private readonly UdpClient _socket;
        private readonly Thread _thread;
        private volatile bool _stopped;
        private int _requests;

        // The address this device is seen from; the port the request came from is answered as it is.
        internal volatile string PublicAddress = "203.0.113.7";
        internal volatile bool Silent;

        internal FakeStunServer()
        {
            _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_socket.Client.LocalEndPoint).Port;
            _thread = new Thread(Serve) { IsBackground = true, Name = "Fake STUN server" };
            _thread.Start();
        }

        internal int Port { get; }
        internal string Url => $"stun:127.0.0.1:{Port}";
        internal int Requests => Volatile.Read(ref _requests);

        private void Serve()
        {
            while (!_stopped)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] request;
                try
                {
                    request = _socket.Receive(ref from);
                }
                catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows reports an earlier answer to a socket that had closed; the server goes on.
                    continue;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Interlocked.Increment(ref _requests);
                if (Silent || request.Length < 20)
                    continue;
                byte[] answer = AnswerTo(request, from.Port);
                try
                {
                    _socket.Send(answer, answer.Length, from);
                }
                catch (SocketException)
                {
                }
            }
        }

        // A binding success holding one XOR-MAPPED-ADDRESS for the chosen address and the request's own port.
        private byte[] AnswerTo(byte[] request, int port)
        {
            byte[] answer = new byte[32];
            answer[0] = 0x01;
            answer[1] = 0x01;
            answer[3] = 12;
            Array.Copy(request, 4, answer, 4, 16);
            answer[20] = 0x00;
            answer[21] = 0x20;
            answer[23] = 8;
            answer[25] = 0x01;
            int maskedPort = port ^ (int)(MagicCookie >> 16);
            answer[26] = (byte)(maskedPort >> 8);
            answer[27] = (byte)maskedPort;
            byte[] address = IPAddress.Parse(PublicAddress).GetAddressBytes();
            for (int i = 0; i < 4; i++)
                answer[28 + i] = (byte)(address[i] ^ (byte)(MagicCookie >> (24 - 8 * i)));
            return answer;
        }

        public void Dispose()
        {
            _stopped = true;
            _socket.Close();
            _thread.Join(1000);
        }
    }
}
