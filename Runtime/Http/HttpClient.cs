using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Flock.Http
{
    public static class FlockHttpClient
    {
        // Enough field errors to spot the pattern without flooding the console line.
        private const int MaxFieldErrorsShown = 3;

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
                CodedErrorDetail detail = ParseErrorDetail(errorContent);
                string errorCode = detail?.Code;
                string serverMessage = detail?.Message;
                string hint = FlockErrorHints.For(FlockErrorCodes.Parse(errorCode));

                if (code == 401 || code == 403)
                    throw new FlockAuthException("Authentication failed") { Body = errorContent, StatusCode = code, Code = errorCode, ServerMessage = serverMessage, Hint = hint };

                if (code == 400 || code == 422)
                    throw new FlockValidationException("Validation failed") { Body = errorContent, StatusCode = code, Code = errorCode, ServerMessage = serverMessage, Hint = hint };

                throw new FlockNetworkException("HTTP request failed", code)
                {
                    Body = errorContent,
                    Code = errorCode,
                    ServerMessage = serverMessage,
                    Hint = hint,
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

        // Two shapes share `detail`: the game routes' coded {code,message} object, and FastAPI's own 422 array of field errors.
        private static CodedErrorDetail ParseErrorDetail(string body)
        {
            if (string.IsNullOrEmpty(body))
                return null;
            try
            {
                JToken detail = JObject.Parse(body)["detail"];
                if (detail == null)
                    return null;
                if (detail.Type == JTokenType.Object)
                    return detail.ToObject<CodedErrorDetail>();
                if (detail.Type == JTokenType.Array)
                    return new CodedErrorDetail { Message = DescribeFieldErrors((JArray)detail) };
                return new CodedErrorDetail { Message = detail.ToString() };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // "body.player_data: Input should be a valid dictionary" — names the offending field so the caller can fix the payload.
        private static string DescribeFieldErrors(JArray errors)
        {
            StringBuilder text = new StringBuilder();
            int shown = 0;
            foreach (JToken error in errors)
            {
                if (shown == MaxFieldErrorsShown)
                {
                    text.Append($"; (+{errors.Count - shown} more)");
                    break;
                }

                string where = JoinLocation(error["loc"] as JArray);
                string why = error["msg"]?.ToString();
                if (string.IsNullOrEmpty(why))
                    continue;

                if (shown > 0)
                    text.Append("; ");
                text.Append(string.IsNullOrEmpty(where) ? why : $"{where}: {why}");
                shown++;
            }
            return text.ToString();
        }

        private static string JoinLocation(JArray location)
        {
            if (location == null)
                return null;

            StringBuilder path = new StringBuilder();
            foreach (JToken part in location)
            {
                if (path.Length > 0)
                    path.Append('.');
                path.Append(part.ToString());
            }
            return path.ToString();
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
