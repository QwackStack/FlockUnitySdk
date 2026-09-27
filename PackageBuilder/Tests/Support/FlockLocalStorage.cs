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

        private readonly HttpListener _listener = new HttpListener();
        private readonly List<Received> _received = new List<Received>();
        private readonly Dictionary<string, (int Status, string Body)> _answersByPath = new Dictionary<string, (int Status, string Body)>();
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly Thread _thread;

        /// <summary>The answer to a request no <see cref="AnswerFor"/> path matches.</summary>
        public (int Status, string Body) Answer = (200, "");
        public volatile bool NeverAnswer;
        public int ReadBytesPerTenthOfASecond;

        public string Url { get; }

        public FlockLocalStorage()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true, Name = "Local storage for tests" };
            _thread.Start();
        }

        /// <summary>Answers requests whose path starts with <paramref name="path"/> (no leading slash) this way.</summary>
        public void AnswerFor(string path, int status, string body)
        {
            lock (_answersByPath)
                _answersByPath["/" + path] = (status, body);
        }

        public int Count
        {
            get { lock (_received) return _received.Count; }
        }

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
                Assert.AreEqual(1, _received.Count, "One request reached the storage");
                return _received[0];
            }
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
                catch (Exception)
                {
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
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
                using (MemoryStream body = new MemoryStream())
                {
                    byte[] buffer = new byte[64 * 1024];
                    int readThisTenth = 0;
                    int read;
                    while ((read = context.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        body.Write(buffer, 0, read);
                        readThisTenth += read;
                        if (ReadBytesPerTenthOfASecond > 0 && readThisTenth >= ReadBytesPerTenthOfASecond)
                        {
                            readThisTenth = 0;
                            Thread.Sleep(100);
                        }
                    }
                    received.Body = body.ToArray();
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
