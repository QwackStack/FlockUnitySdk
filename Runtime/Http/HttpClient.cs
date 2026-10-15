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
        private static IFlockFileUploader _fileUploader;

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

        /// <summary>Uses this uploader for <see cref="UploadFileAsync"/> (a stand-in for tests, or a studio's own); null goes back to the platform's.</summary>
        public static void UseFileUploader(IFlockFileUploader uploader)
        {
            _fileUploader = uploader;
        }

        /// <summary>PUTs a file to a URL, such as a presigned storage link, with this Content-Type and no other SDK header, never whole in memory. Main thread.</summary>
        public static Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType,
            CancellationToken cancellationToken = default)
        {
            if (_fileUploader == null)
                _fileUploader = new UnityWebRequestFileUploader();
            return _fileUploader.UploadFileAsync(url, filePath, contentType, cancellationToken);
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

        /// <summary>Sends to a route whose answer has nothing to read: a 2xx with no body or a JSON body is a success, a 204 included.</summary>
        public static Task PostAsync(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendWithoutReadingAsync(new FlockHttpRequest
            {
                Method = "POST", Url = url, Headers = headers, JsonBody = JsonConvert.SerializeObject(data)
            }, cancellationToken);

        /// <summary>Sends to a route whose answer has nothing to read: a 2xx with no body or a JSON body is a success, a 204 included.</summary>
        public static Task PutAsync(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendWithoutReadingAsync(new FlockHttpRequest
            {
                Method = "PUT", Url = url, Headers = headers, JsonBody = JsonConvert.SerializeObject(data)
            }, cancellationToken);

        /// <summary>Sends to a route whose answer has nothing to read: a 2xx with no body or a JSON body is a success, a 204 included.</summary>
        public static Task PatchAsync(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendWithoutReadingAsync(new FlockHttpRequest
            {
                Method = "PATCH", Url = url, Headers = headers, JsonBody = JsonConvert.SerializeObject(data)
            }, cancellationToken);

        /// <summary>Sends to a route whose answer has nothing to read: a 2xx with no body or a JSON body is a success, a 204 included.</summary>
        public static Task DeleteAsync(string url, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
            => SendWithoutReadingAsync(new FlockHttpRequest { Method = "DELETE", Url = url, Headers = headers }, cancellationToken);

        // A body that is there must still be JSON: a captive portal's page is not the server's answer.
        private static async Task SendWithoutReadingAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            FlockHttpResponse response = await SendAndCheckStatusAsync(request, cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Body))
                return;

            try
            {
                JToken.Parse(response.Body);
            }
            catch (JsonException ex)
            {
                throw new FlockSerializationException(UnreadableAnswer, ex) { Body = response.Body, Hint = FlockErrorHints.NotFlocksAnswer };
            }
        }

        // A read exists for its body, so a 2xx without one still fails it.
        private static async Task<T> SendAsync<T>(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            FlockHttpResponse response = await SendAndCheckStatusAsync(request, cancellationToken);

            if (string.IsNullOrEmpty(response.Body))
                throw new FlockSerializationException("The server's answer was empty, so there was nothing to read") { Hint = FlockErrorHints.NotFlocksAnswer };

            try
            {
                return JsonConvert.DeserializeObject<T>(response.Body);
            }
            catch (JsonException ex)
            {
                throw new FlockSerializationException(UnreadableAnswer, ex) { Body = response.Body, Hint = FlockErrorHints.NotFlocksAnswer };
            }
        }

        private const string UnreadableAnswer = "The server's answer could not be read as JSON";

        // The one place a response's status becomes an exception, for reads and sends alike. What is wrong is the server's own
        // reason when it gave one (FlockException composes it), the status's plain meaning otherwise; the fix comes from the
        // code, or from the kind of failure (FlockErrorHints.ForFailure).
        private static async Task<FlockHttpResponse> SendAndCheckStatusAsync(FlockHttpRequest request, CancellationToken cancellationToken)
        {
            FlockHttpResponse response = await Adapter.SendAsync(request, cancellationToken);

            if (response.Result == FlockHttpResult.Timeout)
                throw new FlockNetworkException("The server did not answer in time") { Hint = FlockErrorHints.NoAnswerInTime };
            if (response.Result == FlockHttpResult.ConnectionError)
                throw new FlockNetworkException("The request could not reach the server") { Body = response.Body, Hint = FlockErrorHints.CouldNotReachTheServer };

            int code = response.StatusCode;
            if (code < 200 || code >= 300)
            {
                string errorContent = response.Body;
                CodedErrorDetail detail = ParseErrorDetail(errorContent);
                string errorCode = detail?.Code;
                string serverMessage = detail?.Message;
                string cause = DescribeStatus(code);

                if (code == 401 || code == 403)
                    throw new FlockAuthException(cause) { Body = errorContent, StatusCode = code, Code = errorCode, ServerMessage = serverMessage };

                if (code == 400 || code == 422)
                    throw new FlockValidationException(cause) { Body = errorContent, StatusCode = code, Code = errorCode, ServerMessage = serverMessage };

                throw new FlockNetworkException(cause, code)
                {
                    Body = errorContent,
                    Code = errorCode,
                    ServerMessage = serverMessage,
                    RetryAfter = ParseRetryAfter(response.RetryAfterHeader)
                };
            }

            return response;
        }

        // What a failed status means, for an answer that gave no reason of its own. Flock's game API always gives one, so these
        // are mostly read on answers from something else: a proxy, a captive portal, another server.
        private static string DescribeStatus(int statusCode)
        {
            switch (statusCode)
            {
                case 400: return "The server could not use the request";
                case 401: return "The server did not accept the request's credentials";
                case 403: return "The server refused to do this";
                case 404: return "The server found nothing at this address";
                case 405: return "The server does not take this kind of request at this address";
                case 408: return "The server stopped waiting for the request";
                case 409: return "The request clashes with what the server holds now";
                case 413: return "The request is larger than the server accepts";
                case 422: return "The server refused a value in the request";
                case 429: return "The server is getting too many requests from this game";
                default: return statusCode >= 500 ? "The server failed while handling the request" : "The server refused the request";
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

        // Parses Retry-After as delta-seconds or an HTTP date so the retry handler can honor it. A negative value means "now":
        // a negative wait would make the delay throw.
        internal static TimeSpan? ParseRetryAfter(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue))
                return null;
            if (int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                return TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (DateTimeOffset.TryParse(headerValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
            {
                TimeSpan until = date - DateTimeOffset.UtcNow;
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }
            return null;
        }
    }
}
