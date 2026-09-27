using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Flock.Tests
{
    /// <summary>The platform's file upload on the wire, against storage of the test's own on this machine: what is sent, and when it gives up.</summary>
    public class FlockFileUploaderTests
    {
        private string _folder;
        private FlockLocalStorage _storage;

        [SetUp]
        public void SetUp()
        {
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                Assert.Ignore("This project refuses plain http, which the storage stand-in on this machine answers on.");
            _folder = Path.Combine(Path.GetTempPath(), "flock_upload_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _storage = new FlockLocalStorage();
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

        [UnityTest]
        public IEnumerator TheFileIsPutWithItsContentTypeAndNoOtherHeaderOfTheSdks()
        {
            string path = MakeFile(300000);
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key?X-Amz-Signature=abc", path, "video/webm");
            yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 20 s", 20);

            FlockFileUploadOutcome outcome = upload.Result;
            Assert.IsTrue(outcome.IsUploaded, $"{outcome.Result} {outcome.StatusCode} {outcome.Body}");
            Assert.AreEqual(300000, outcome.BytesSent);
            FlockLocalStorage.Received sent = _storage.Single();
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
            yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 20 s", 20);

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
            yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 10 s", 10);

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
                    yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 10 s", 10);

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
            yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 15 s", 15);

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
            yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 60 s", 60);

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
    }
}
