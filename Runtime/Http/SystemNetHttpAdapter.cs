#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flock.Http
{
    /// <summary>System.Net.Http transport. Used on every platform except WebGL builds, where HttpClient has no transport.</summary>
    public sealed class SystemNetHttpAdapter : IFlockHttpAdapter
    {
        private readonly HttpClient _client;

        public SystemNetHttpAdapter(TimeSpan timeout)
        {
            _client = new HttpClient();
            if (timeout > TimeSpan.Zero)
                _client.Timeout = timeout;
        }

        public async Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
            {
                ApplyHeaders(message, request.Headers);
                if (request.JsonBody != null)
                    message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");

                try
                {
                    using (HttpResponseMessage response = await _client.SendAsync(message, cancellationToken))
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        return new FlockHttpResponse
                        {
                            Result = FlockHttpResult.Success,
                            StatusCode = (int)response.StatusCode,
                            Body = body,
                            RetryAfterHeader = ReadRetryAfter(response)
                        };
                    }
                }
                catch (HttpRequestException ex)
                {
                    return new FlockHttpResponse
                    {
                        Result = FlockHttpResult.ConnectionError,
                        Body = (ex.InnerException ?? ex).Message
                    };
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient.Timeout fired (the user's token did not) — surface as a retryable timeout.
                    return new FlockHttpResponse { Result = FlockHttpResult.Timeout };
                }
            }
        }

        private static string ReadRetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string> values))
            {
                foreach (string value in values)
                    return value;
            }
            return null;
        }

        private static void ApplyHeaders(HttpRequestMessage message, Dictionary<string, string> headers)
        {
            if (headers == null)
                return;
            foreach (KeyValuePair<string, string> kvp in headers)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                    message.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }
    }
}
#endif
