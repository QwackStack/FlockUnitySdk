using System;
using System.Collections;
using System.Diagnostics;
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
#if UNITY_2022_1_OR_NEWER
            // Unity refuses plain http only from 2022.1.
            if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                Assert.Ignore("This project refuses plain http, which the storage stand-in on this machine answers on.");
#endif
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
            yield return WaitASecondForAnyRequest();
            Assert.AreEqual(0, _storage.RequestsArrived, "No request was made" + _storage.ServeFailuresSaid());
        }

        // The editor loop keeps ticking, as a UnityWebRequest needs, for a second or until a request reaches the storage.
        private IEnumerator WaitASecondForAnyRequest()
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (_storage.RequestsArrived == 0 && waited.Elapsed < TimeSpan.FromSeconds(1))
                yield return null;
        }

        // Measured: Unity throws for an address it cannot parse.
        [UnityTest]
        public IEnumerator AnAddressUnityCannotParseIsAnOutcomeAndNotAnException()
        {
            yield return AnUploadUnityWillNotBegin("http://[not an address", MakeFile(1000));
        }

        // Measured on Windows: Unity throws for a file another program holds open to itself. On Linux 2021.3 uploads it anyway (CI, 2026-10-15).
        [UnityTest]
        [UnityPlatform(UnityEngine.RuntimePlatform.WindowsEditor)]
        public IEnumerator AFileAnotherProgramHoldsOpenIsAnOutcomeAndNotAnException()
        {
            string held = MakeFile(1000);
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                yield return AnUploadUnityWillNotBegin(_storage.Url + "bucket/key", held);
        }

        private IEnumerator AnUploadUnityWillNotBegin(string url, string path)
        {
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(url, path, "video/webm");
            yield return FlockTestWait.Until(() => upload.IsCompleted, "The upload ended within 10 s", 10);

            Assert.IsFalse(upload.IsFaulted, url + ": no exception, but " + upload.Exception?.GetBaseException().Message);
            Assert.AreEqual(FlockHttpResult.ConnectionError, upload.Result.Result, url);
            Assert.IsFalse(upload.Result.IsUploaded, url);
            StringAssert.Contains("could not begin", upload.Result.Body, url);
            yield return WaitASecondForAnyRequest();
            Assert.AreEqual(0, _storage.RequestsArrived, "No request was made" + _storage.ServeFailuresSaid());
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
            // Only an upload that never moves is given up: the storage reads 44 MiB at 512 KiB a tenth, about eight seconds against a three-second stall timeout.
            const int Size = 60 * 1024 * 1024;
            TimeSpan stallTimeout = TimeSpan.FromSeconds(3);
            _storage.ReadBytesPerTenthOfASecond = 512 * 1024;
            // The client counts what it hands to the socket, so the last ~9 MiB drain from Linux loopback's buffers with nothing moving (2.6 to 2.9 s read slowly on CI); read at full speed, milliseconds.
            _storage.LastBytesReadAtFullSpeed = 16 * 1024 * 1024;
            string path = MakeFile(Size);
            FlockHttpClient.UseFileUploader(new UnityWebRequestFileUploader(stallTimeout));
            Stopwatch sinceStart = Stopwatch.StartNew();
            Task<FlockFileUploadOutcome> upload = FlockHttpClient.UploadFileAsync(_storage.Url + "bucket/key", path, "video/webm");
            // Timed where the upload ends, not when a later frame sees it.
            Task<TimeSpan> took = upload.ContinueWith(_ => sinceStart.Elapsed, TaskContinuationOptions.ExecuteSynchronously);
            yield return FlockTestWait.Until(() => took.IsCompleted, "The upload ended within 60 s", 60);

            Assert.IsTrue(upload.Result.IsUploaded, $"{upload.Result.Result} {upload.Result.StatusCode} {upload.Result.Body}");
            Assert.AreEqual(Size, _storage.Single().Body.Length);
            Assert.Greater(took.Result.TotalSeconds, 2 * stallTimeout.TotalSeconds,
                "The upload outlasted two stall timeouts, so an uploader that timed the whole upload would have given it up");
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
