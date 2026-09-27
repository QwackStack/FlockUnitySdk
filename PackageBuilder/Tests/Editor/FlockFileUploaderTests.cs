using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Flock.Tests
{
    /// <summary>The platform's file upload on the wire, against storage of the test's own on this machine: what is sent, and when it gives up.</summary>
    public class FlockFileUploaderTests
    {
        private string _folder;
        private LocalStorage _storage;

        [SetUp]
        public void SetUp()
        {
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                Assert.Ignore("This project refuses plain http, which the storage stand-in on this machine answers on.");
            _folder = Path.Combine(Path.GetTempPath(), "flock_upload_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _storage = new LocalStorage();
        }

        [TearDown]
        public void TearDown()
        {
            _storage?.Dispose();
            FlockHttpClient.UseFileUploader(null);
            if (_folder != null && Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private string MakeFile(int bytes)
        {
            string path = Path.Combine(_folder, "recording-" + Guid.NewGuid().ToString("N") + ".webm");
            byte[] content = new byte[bytes];
            for (int i = 0; i < bytes; i++)
                content[i] = (byte)(i * 7 + 3);
            File.WriteAllBytes(path, content);
            return path;
        }

        // UnityWebRequest moves only while the editor ticks, so a test waits in frames.
        private static IEnumerator Finish(Task<FlockFileUploadOutcome> upload, float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!upload.IsCompleted && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(upload.IsCompleted, $"The upload ended within {seconds} s");
        }

        [UnityTest]
        public IEnumerator TheFileIsPutWithItsContentTypeAndNoOtherHeaderOfTheSdks()
        {
            string path = MakeFile(300000);
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key?X-Amz-Signature=abc", path, "video/webm");
            yield return Finish(upload, 20);

            FlockFileUploadOutcome outcome = upload.Result;
            Assert.IsTrue(outcome.IsUploaded, $"{outcome.Result} {outcome.StatusCode} {outcome.Body}");
            Assert.AreEqual(300000, outcome.BytesSent);
            LocalStorage.Received sent = _storage.Single();
            Assert.AreEqual("PUT", sent.Method);
            Assert.AreEqual("/bucket/key?X-Amz-Signature=abc", sent.PathAndQuery, "The presigned address exactly as given");
            Assert.AreEqual("video/webm", sent.ContentType, "Exactly the type the link was signed for");
            CollectionAssert.AreEqual(File.ReadAllBytes(path), sent.Body, "The file, byte for byte");
            foreach (string header in new[] { "X-Flock-API-Key", "X-Game-Version-ID", "Authorization" })
                Assert.IsNull(sent.Headers[header], $"{header} is never sent to storage");
        }

        [UnityTest]
        public IEnumerator ARefusalIsAnAnswerAndNotAnUpload()
        {
            _storage.Answer = (403, "<Error><Code>AccessDenied</Code><Message>Request has expired</Message></Error>");
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key", MakeFile(1000), "video/webm");
            yield return Finish(upload, 20);

            FlockFileUploadOutcome outcome = upload.Result;
            Assert.AreEqual(FlockHttpResult.Success, outcome.Result, "A status came back");
            Assert.AreEqual(403, outcome.StatusCode);
            Assert.IsFalse(outcome.IsUploaded);
            StringAssert.Contains("<Code>AccessDenied</Code>", outcome.Body, "The storage's own code, so a caller can tell an expired link from a bad one");
        }

        [UnityTest]
        public IEnumerator AFileThatIsNotThereSendsNothing()
        {
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key", Path.Combine(_folder, "missing.webm"), "video/webm");
            yield return Finish(upload, 10);

            Assert.IsFalse(upload.Result.IsUploaded);
            Assert.AreEqual(FlockHttpResult.ConnectionError, upload.Result.Result);
            StringAssert.Contains("missing.webm", upload.Result.Body);
            Thread.Sleep(200);
            Assert.AreEqual(0, _storage.Count, "No request was made");
        }

        [UnityTest]
        public IEnumerator AnUploadUnityWillNotBeginIsAnOutcomeAndNotAnException()
        {
            // Measured: Unity throws for an address it cannot parse, and for a file another program holds open to itself.
            string held = MakeFile(1000);
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                foreach ((string url, string path) in new[] { ("http://[not an address", MakeFile(1000)), (_storage.Url + "bucket/key", held) })
                {
                    Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(url, path, "video/webm");
                    yield return Finish(upload, 10);

                    Assert.IsFalse(upload.IsFaulted, url + ": no exception, but " + upload.Exception?.GetBaseException().Message);
                    Assert.AreEqual(FlockHttpResult.ConnectionError, upload.Result.Result, url);
                    Assert.IsFalse(upload.Result.IsUploaded, url);
                    StringAssert.Contains("could not begin", upload.Result.Body, url);
                }
            }
            Thread.Sleep(200);
            Assert.AreEqual(0, _storage.Count, "No request was made");
        }

        [UnityTest]
        public IEnumerator AnUploadNothingAnswersIsGivenUpAfterItsStallTimeout()
        {
            _storage.NeverAnswer = true;
            FlockHttpClient.UseFileUploader(new UnityWebRequestFileUploader(TimeSpan.FromSeconds(1)));
            DateTime began = DateTime.UtcNow;
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key", MakeFile(1000), "video/webm");
            yield return Finish(upload, 15);

            Assert.AreEqual(FlockHttpResult.Timeout, upload.Result.Result, upload.Result.Body);
            Assert.Less((DateTime.UtcNow - began).TotalSeconds, 10.0, "Given up, not left hanging");
        }

        [UnityTest]
        public IEnumerator ASlowUploadThatKeepsSendingIsNotGivenUp()
        {
            // Read slowly for about four seconds against a one-second stall timeout: only an upload that never moves is given up.
            _storage.ReadBytesPerTenthOfASecond = 256 * 1024;
            FlockHttpClient.UseFileUploader(new UnityWebRequestFileUploader(TimeSpan.FromSeconds(1)));
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key", MakeFile(10 * 1024 * 1024), "video/webm");
            yield return Finish(upload, 60);

            Assert.IsTrue(upload.Result.IsUploaded, $"{upload.Result.Result} {upload.Result.StatusCode} {upload.Result.Body}");
            Assert.AreEqual(10 * 1024 * 1024, _storage.Single().Body.Length);
        }

        [UnityTest]
        public IEnumerator ACancelledUploadStopsAndThrows()
        {
            _storage.NeverAnswer = true;
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key", MakeFile(1000), "video/webm", cancel.Token);
                for (int frame = 0; frame < 10; frame++)
                    yield return null;
                cancel.Cancel();
                DateTime until = DateTime.UtcNow.AddSeconds(10);
                while (!upload.IsCompleted && DateTime.UtcNow < until)
                    yield return null;
                Assert.IsTrue(upload.IsCanceled || upload.Exception?.GetBaseException() is OperationCanceledException, "Cancelled, as every SDK request is");
            }
        }

        [Test]
        public void TheUploaderCanBeReplacedAndPutBack()
        {
            RecordingUploader stand = new RecordingUploader();
            FlockHttpClient.UseFileUploader(stand);
            FlockHttpClient.UploadFileAsync("http://storage.test/a", "file.webm", "video/mp4").Wait(TimeSpan.FromSeconds(5));
            Assert.AreEqual("video/mp4", stand.ContentType, "Everything goes to the uploader in use");
            FlockHttpClient.UseFileUploader(null);
            Assert.AreEqual(1, stand.Calls, "Precondition");
            FlockHttpClient.UploadFileAsync("http://storage.test/a", Path.Combine(_folder, "missing.webm"), "video/webm").Wait(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, stand.Calls, "Null goes back to the platform's own");
        }

        private sealed class RecordingUploader : IFlockFileUploader
        {
            public int Calls;
            public string ContentType;

            public Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType, CancellationToken cancellationToken)
            {
                Calls++;
                ContentType = contentType;
                return Task.FromResult(new FlockFileUploadOutcome { Result = FlockHttpResult.Success, StatusCode = 200 });
            }
        }

        /// <summary>Storage of the test's own on this machine: takes each PUT, keeps what arrived, and answers as the test says.</summary>
        private sealed class LocalStorage : IDisposable
        {
            internal sealed class Received
            {
                public string Method;
                public string PathAndQuery;
                public string ContentType;
                public System.Collections.Specialized.NameValueCollection Headers;
                public byte[] Body;
            }

            private readonly HttpListener _listener = new HttpListener();
            private readonly List<Received> _received = new List<Received>();
            private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
            private readonly Thread _thread;

            public (int Status, string Body) Answer = (200, "");
            public volatile bool NeverAnswer;
            public int ReadBytesPerTenthOfASecond;

            public string Url { get; }

            public LocalStorage()
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                Url = $"http://127.0.0.1:{port}/";
                _listener.Prefixes.Add(Url);
                _listener.Start();
                _thread = new Thread(Serve) { IsBackground = true, Name = "Local storage for upload tests" };
                _thread.Start();
            }

            public int Count
            {
                get { lock (_received) return _received.Count; }
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
                    byte[] answer = System.Text.Encoding.UTF8.GetBytes(Answer.Body ?? "");
                    context.Response.StatusCode = Answer.Status;
                    context.Response.ContentLength64 = answer.Length;
                    context.Response.OutputStream.Write(answer, 0, answer.Length);
                    context.Response.OutputStream.Close();
                }
                catch (Exception)
                {
                    // The uploader gave up or the test ended; nothing to answer.
                }
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
}
