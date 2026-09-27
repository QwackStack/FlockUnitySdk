using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Flock.Http
{
    /// <summary>The platform's file upload: UnityWebRequest reading the file from disk as it sends it. Call it on the main thread, as UnityWebRequest requires.</summary>
    // A whole upload has no time limit (1.5 GB on a slow connection takes long); it is given up only when nothing moves for a while.
    public sealed class UnityWebRequestFileUploader : IFlockFileUploader
    {
        /// <summary>How long an upload may go without sending a byte or getting its answer before it is given up.</summary>
        public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

        // Enough of a storage error to read its code without keeping a page someone else served.
        private const int LongestBodyKept = 512;

        private readonly TimeSpan _stallTimeout;

        public UnityWebRequestFileUploader() : this(DefaultStallTimeout)
        {
        }

        /// <summary>An uploader that gives up after this long with nothing sent or answered; zero or less never gives up.</summary>
        public UnityWebRequestFileUploader(TimeSpan stallTimeout)
        {
            _stallTimeout = stallTimeout;
        }

        public Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<FlockFileUploadOutcome>(cancellationToken);
#if UNITY_WEBGL && !UNITY_EDITOR
            // Nothing streams a file here, and reading a whole recording into memory to send it could end the game, so none is sent.
            return Task.FromResult(CouldNotBegin("this platform cannot stream a file upload, so none is sent"));
#else
            return UploadAsync(url, filePath, contentType, cancellationToken);
#endif
        }

        private async Task<FlockFileUploadOutcome> UploadAsync(string url, string filePath, string contentType, CancellationToken cancellationToken)
        {
            UnityWebRequest request = null;
            UnityWebRequestAsyncOperation operation;
            try
            {
                request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT);
                request.uploadHandler = new UploadHandlerFile(filePath) { contentType = contentType };
                request.downloadHandler = new DownloadHandlerBuffer();
                operation = request.SendWebRequest();
            }
            catch (Exception ex)
            {
                // Unity refuses by throwing an address it cannot parse and a file it cannot open (missing, or held by another program).
                request?.Dispose();
                return CouldNotBegin($"the upload could not begin: {ex.Message}");
            }

            using (request)
            {
                ulong lastBytesSent = 0;
                // Timed on a clock the system time cannot move, so a clock correction never gives up an upload that is going.
                Stopwatch sinceLastMoved = Stopwatch.StartNew();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    ulong bytesSent = request.uploadedBytes;
                    if (bytesSent != lastBytesSent)
                    {
                        lastBytesSent = bytesSent;
                        sinceLastMoved.Restart();
                    }
                    else if (_stallTimeout > TimeSpan.Zero && sinceLastMoved.Elapsed > _stallTimeout)
                    {
                        request.Abort();
                        return new FlockFileUploadOutcome
                        {
                            Result = FlockHttpResult.Timeout,
                            Body = $"nothing was sent or answered for {_stallTimeout.TotalSeconds:0} s",
                            BytesSent = (long)bytesSent
                        };
                    }
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();

                long sent = (long)request.uploadedBytes;
                // A status that came back is an answer, whatever it says; only its absence is a transport failure.
                if (request.responseCode > 0)
                {
                    return new FlockFileUploadOutcome
                    {
                        Result = FlockHttpResult.Success,
                        StatusCode = (int)request.responseCode,
                        Body = Shorten(request.downloadHandler != null ? request.downloadHandler.text : null),
                        BytesSent = sent
                    };
                }
                // UnityWebRequest folds timeouts into its connection errors, so they are told apart by the error text.
                bool timedOut = !string.IsNullOrEmpty(request.error) && request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                return new FlockFileUploadOutcome
                {
                    Result = timedOut ? FlockHttpResult.Timeout : FlockHttpResult.ConnectionError,
                    Body = request.error,
                    BytesSent = sent
                };
            }
        }

        private static FlockFileUploadOutcome CouldNotBegin(string why)
            => new FlockFileUploadOutcome { Result = FlockHttpResult.ConnectionError, Body = why };

        private static string Shorten(string body)
            => body != null && body.Length > LongestBodyKept ? body.Substring(0, LongestBodyKept) : body;
    }
}
