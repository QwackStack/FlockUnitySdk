using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;

namespace Flock.Tests.Support
{
    /// <summary>Storage of the test's own on this machine: takes each request, keeps what arrived, and answers as the test says.</summary>
    public sealed class FlockLocalStorage : IDisposable
    {
        public sealed class Received
        {
            public string Method;
            public string PathAndQuery;
            public string ContentType;
            public System.Collections.Specialized.NameValueCollection Headers;
            public byte[] Body;
        }

        // Another program can take the free port between finding it and listening on it, so a taken port is tried again with another.
        private const int MostTriesForAPort = 5;

        private readonly HttpListener _listener;
        private readonly List<Received> _received = new List<Received>();
        private readonly List<Exception> _serveFailures = new List<Exception>();
        private readonly Dictionary<string, (int Status, string Body)> _answersByPath = new Dictionary<string, (int Status, string Body)>();
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private int _requestsArrived;

        /// <summary>The answer to a request no <see cref="AnswerFor"/> path matches.</summary>
        public (int Status, string Body) Answer = (200, "");
        public volatile bool NeverAnswer;
        public int ReadBytesPerTenthOfASecond;

        /// <summary>The end of a body of known length read at full speed despite <see cref="ReadBytesPerTenthOfASecond"/>; zero reads it all slowly.</summary>
        public long LastBytesReadAtFullSpeed;

        public string Url { get; }

        public FlockLocalStorage()
        {
            for (int attempt = 1; ; attempt++)
            {
                HttpListener listener = new HttpListener();
                string url = $"http://127.0.0.1:{FreePort()}/";
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                    _listener = listener;
                    Url = url;
                    break;
                }
                catch (Exception taken) when ((taken is HttpListenerException || taken is SocketException) && attempt < MostTriesForAPort)
                {
                    CloseQuietly(listener);
                }
            }
            _thread = new Thread(Serve) { IsBackground = true, Name = "Local storage for tests" };
            _thread.Start();
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static void CloseQuietly(HttpListener listener)
        {
            try
            {
                listener.Close();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Answers requests whose path starts with <paramref name="path"/> (no leading slash) this way.</summary>
        public void AnswerFor(string path, int status, string body)
        {
            lock (_answersByPath)
                _answersByPath["/" + path] = (status, body);
        }

        /// <summary>Requests kept whole, each once its body was read.</summary>
        public int Count
        {
            get { lock (_received) return _received.Count; }
        }

        /// <summary>Requests that reached the storage, counted as each arrives, before its body is read or kept.</summary>
        public int RequestsArrived => Volatile.Read(ref _requestsArrived);

        /// <summary>The path and query of every request that arrived, in order.</summary>
        public List<string> Paths()
        {
            lock (_received)
                return _received.ConvertAll(r => r.PathAndQuery);
        }

        public Received Single()
        {
            lock (_received)
            {
                Assert.AreEqual(1, _received.Count, "One request reached the storage" + ServeFailuresSaid());
                return _received[0];
            }
        }

        /// <summary>What went wrong taking requests while the storage kept serving; empty when nothing did.</summary>
        public List<Exception> ServeFailures()
        {
            lock (_serveFailures)
                return new List<Exception>(_serveFailures);
        }

        /// <summary>The serve failures for an assertion's message; empty when there were none.</summary>
        public string ServeFailuresSaid()
        {
            List<Exception> failures = ServeFailures();
            return failures.Count == 0 ? "" : "; taking requests failed: " + string.Join("; ", failures.ConvertAll(failure => failure.GetType().Name + ": " + failure.Message));
        }

        private void Serve()
        {
            while (!_stopped.IsSet)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception failure)
                {
                    // Stopping ends the wait with an exception; any other is kept for the test, and serving goes on while the listener listens.
                    if (_stopped.IsSet)
                        return;
                    lock (_serveFailures)
                        _serveFailures.Add(failure);
                    if (!_listener.IsListening)
                        return;
                    Thread.Sleep(10);
                    continue;
                }
                Interlocked.Increment(ref _requestsArrived);
                // A thread of its own, so a busy thread pool never delays a request's reading.
                new Thread(() => Handle(context)) { IsBackground = true, Name = "Local storage request for tests" }.Start();
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                Received received = new Received
                {
                    Method = context.Request.HttpMethod,
                    PathAndQuery = context.Request.Url.PathAndQuery,
                    ContentType = context.Request.ContentType,
                    Headers = context.Request.Headers
                };
                long length = context.Request.ContentLength64;
                bool lengthKnown = length > 0 && length <= int.MaxValue;
                // Sized up front when the length is known, so a long body is never copied as it grows or once it is read.
                using (MemoryStream body = lengthKnown ? new MemoryStream((int)length) : new MemoryStream())
                {
                    byte[] buffer = new byte[64 * 1024];
                    long readSoFar = 0;
                    int readThisTenth = 0;
                    int read;
                    while ((read = context.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        body.Write(buffer, 0, read);
                        readSoFar += read;
                        readThisTenth += read;
                        bool readSlowly = ReadBytesPerTenthOfASecond > 0 && (!lengthKnown || length - readSoFar > LastBytesReadAtFullSpeed);
                        if (readSlowly && readThisTenth >= ReadBytesPerTenthOfASecond)
                        {
                            readThisTenth = 0;
                            Thread.Sleep(100);
                        }
                    }
                    received.Body = body.Length == body.Capacity ? body.GetBuffer() : body.ToArray();
                }
                lock (_received)
                    _received.Add(received);

                if (NeverAnswer)
                {
                    _stopped.Wait();
                    return;
                }
                (int Status, string Body) reply = AnswerTo(received.PathAndQuery);
                byte[] answer = System.Text.Encoding.UTF8.GetBytes(reply.Body ?? "");
                context.Response.StatusCode = reply.Status;
                context.Response.ContentLength64 = answer.Length;
                context.Response.OutputStream.Write(answer, 0, answer.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // The client gave up or the test ended; nothing to answer.
            }
        }

        private (int Status, string Body) AnswerTo(string pathAndQuery)
        {
            lock (_answersByPath)
            {
                foreach (KeyValuePair<string, (int Status, string Body)> entry in _answersByPath)
                    if (pathAndQuery.StartsWith(entry.Key, StringComparison.Ordinal))
                        return entry.Value;
            }
            return Answer;
        }

        public void Dispose()
        {
            _stopped.Set();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
            }
        }
    }
}
