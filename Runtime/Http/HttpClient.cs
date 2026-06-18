using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Newtonsoft.Json;

namespace Flock.Http
{
    public static class FlockHttpClient
    {
        private static IFlockHttpAdapter _adapter;

        private static IFlockHttpAdapter Adapter
        {
            get
            {
                if (_adapter == null)
                    _adapter = CreateDefaultAdapter(TimeSpan.FromSeconds(30));
                return _adapter;
            }
        }

        /// <summary>Sets the per-request timeout and (re)builds the platform transport. Call once at init.</summary>
        public static void Configure(TimeSpan timeout)
        {
            _adapter = CreateDefaultAdapter(timeout);
        }

        /// <summary>Swaps in a custom transport (e.g. a mock for tests). Overrides the platform default.</summary>
        public static void Configure(IFlockHttpAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        private static IFlockHttpAdapter CreateDefaultAdapter(TimeSpan timeout)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new UnityWebRequestHttpAdapter(timeout);
#else
            return new SystemNetHttpAdapter(timeout);
#endif
        }

        public static Task<T> GetAsync<T>(string url, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendAsync<T>(new FlockHttpRequest { Method = "GET", Url = url, Headers = headers }, cancellationToken);

        public static Task<T> PostAsync<T>(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendAsync<T>(new FlockHttpRequest
            {
                Method = "POST", Url = url, Headers = headers, JsonBody = JsonConvert.SerializeObject(data)
            }, cancellationToken);

        public static Task<T> PutAsync<T>(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendAsync<T>(new FlockHttpRequest
            {
                Method = "PUT", Url = url, Headers = headers, JsonBody = JsonConvert.SerializeObject(data)
            }, cancellationToken);

        public static Task<T> PatchAsync<T>(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendAsync<T>(new FlockHttpRequest
            {
                Method = "PATCH", Url = url, Headers = headers, JsonBody = JsonConvert.SerializeObject(data)
            }, cancellationToken);

        public static Task<T> DeleteAsync<T>(string url, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendAsync<T>(new FlockHttpRequest { Method = "DELETE", Url = url, Headers = headers }, cancellationToken);

        private static async Task<T> SendAsync<T>(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            FlockHttpResponse response = await Adapter.SendAsync(request, cancellationToken);

            if (response.Result == FlockHttpResult.Timeout)
                throw new FlockNetworkException("Request timeout");
            if (response.Result == FlockHttpResult.ConnectionError)
                throw new FlockNetworkException("Network request failed") { Body = response.Body };

            int code = response.StatusCode;
            if (code < 200 || code >= 300)
            {
                string errorContent = response.Body;

                if (code == 401 || code == 403)
                    throw new FlockAuthException($"Authentication failed (HTTP {code})") { Body = errorContent, StatusCode = code };

                if (code == 400 || code == 422)
                    throw new FlockValidationException($"Validation failed (HTTP {code})") { Body = errorContent, StatusCode = code };

                throw new FlockNetworkException($"HTTP request failed (HTTP {code})", code)
                {
                    Body = errorContent,
                    RetryAfter = ParseRetryAfter(response.RetryAfterHeader)
                };
            }

            if (string.IsNullOrEmpty(response.Body))
                throw new FlockSerializationException("Empty response from server");

            try
            {
                return JsonConvert.DeserializeObject<T>(response.Body);
            }
            catch (JsonException ex)
            {
                throw new FlockSerializationException("Malformed response body", ex) { Body = response.Body };
            }
        }

        // Parses Retry-After as delta-seconds or an HTTP date so the retry handler can honor it.
        private static TimeSpan? ParseRetryAfter(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue))
                return null;
            if (int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                return TimeSpan.FromSeconds(seconds);
            if (DateTimeOffset.TryParse(headerValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
            {
                TimeSpan until = date - DateTimeOffset.UtcNow;
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }
            return null;
        }
    }
}
