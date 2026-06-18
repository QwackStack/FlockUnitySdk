using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Flock.Http
{
    /// <summary>UnityWebRequest transport. Selected on WebGL builds, where System.Net.Http has no working transport.</summary>
    public sealed class UnityWebRequestHttpAdapter : IFlockHttpAdapter
    {
        private readonly int _timeoutSeconds;

        public UnityWebRequestHttpAdapter(TimeSpan timeout)
        {
            _timeoutSeconds = timeout > TimeSpan.Zero ? (int)Math.Ceiling(timeout.TotalSeconds) : 0;
        }

        public async Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            using (UnityWebRequest webRequest = Build(request))
            {
                ApplyHeaders(webRequest, request.Headers);
                if (_timeoutSeconds > 0)
                    webRequest.timeout = _timeoutSeconds;

                UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (webRequest.result == UnityWebRequest.Result.ConnectionError)
                {
                    // UnityWebRequest folds timeouts into ConnectionError — split them back out by the error text.
                    bool timedOut = !string.IsNullOrEmpty(webRequest.error)
                        && webRequest.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new FlockHttpResponse
                    {
                        Result = timedOut ? FlockHttpResult.Timeout : FlockHttpResult.ConnectionError,
                        Body = webRequest.error
                    };
                }

                return new FlockHttpResponse
                {
                    Result = FlockHttpResult.Success,
                    StatusCode = (int)webRequest.responseCode,
                    Body = webRequest.downloadHandler != null ? webRequest.downloadHandler.text : null,
                    RetryAfterHeader = webRequest.GetResponseHeader("Retry-After")
                };
            }
        }

        private static UnityWebRequest Build(FlockHttpRequest request)
        {
            UnityWebRequest webRequest = new UnityWebRequest(request.Url, request.Method);
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            if (request.JsonBody != null)
            {
                byte[] payload = Encoding.UTF8.GetBytes(request.JsonBody);
                webRequest.uploadHandler = new UploadHandlerRaw(payload) { contentType = "application/json" };
            }
            return webRequest;
        }

        private static void ApplyHeaders(UnityWebRequest webRequest, Dictionary<string, string> headers)
        {
            if (headers == null)
                return;
            foreach (KeyValuePair<string, string> kvp in headers)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                    webRequest.SetRequestHeader(kvp.Key, kvp.Value);
            }
        }
    }
}
