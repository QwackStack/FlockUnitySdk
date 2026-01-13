using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Plastic.Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Flock.Expression
{
    [Serializable]
    public sealed class ExpressionEventPayload
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("ts_unix")]
        public long TimestampUnix { get; set; }

        [JsonProperty("exception_data")]
        public ExpressionException ExceptionData { get; set; }
    }

    [Serializable]
    public class ExpressionException
    {
        public string StackTrace { get; set; }
        public string ExceptionMessage { get; set; }
    }
    public class FlockMessageService
    {
        private readonly string _apiSecret;
        private readonly string _baseUrl = "https://api.flock.qwacks.com";
        private readonly string _endpointPath = "/events/expression";
        private readonly string _gameId;
        private readonly int _timeoutSeconds;

        /// <param name="apiSecret">API secret key for authentication with Flock API</param>
        /// <param name="gameId">Optional ID of the game in Flock</param>
        /// <param name="timeoutSeconds">Timeout value for request in seconds</param>
        public FlockMessageService(
            string apiSecret,
            string gameId = null,
            int timeoutSeconds = 20)
        {
            _apiSecret = apiSecret;
            _gameId = gameId;
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>
        /// Sends an expression event to the Flock API with message, data, timestamp, and exception information
        /// </summary>
        public async Task<ExpressionSendResult> SendMessageAsync(
            string trackedMessage, string exceptionMessage, string exceptionStackTrace = null, Dictionary<string, object> customData = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(trackedMessage))
                throw new ArgumentException("expression is required", nameof(trackedMessage));

            Dictionary<string, object> data = BuildDefaultContext(customData);
            ExpressionEventPayload payload = new ExpressionEventPayload
            {
                Message = trackedMessage,
                Data = data ?? new Dictionary<string, object>(),
                TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExceptionData = new ExpressionException()
                {
                    StackTrace = exceptionStackTrace,
                    ExceptionMessage = exceptionMessage
                }
            };

            string json = JsonConvert.SerializeObject(payload);
            string url = _baseUrl + _endpointPath;

            using UnityWebRequest req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.timeout = _timeoutSeconds;
            req.downloadHandler = new DownloadHandlerBuffer();
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));

            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.SetRequestHeader("X-API-Key", _apiSecret);

            if (!string.IsNullOrWhiteSpace(_gameId))
                req.SetRequestHeader("Game-ID", _gameId);

            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            int status = (int)req.responseCode;
            string body = req.downloadHandler?.text ?? "";
            string error = req.result == UnityWebRequest.Result.Success ? null : req.error;
            bool ok = status >= 200 && status < 300;

            return new ExpressionSendResult(ok, status, body, error);
        }

        private static Dictionary<string, object> BuildDefaultContext(Dictionary<string, object> overrides)
        {
            Dictionary<string, object> ctx = new Dictionary<string, object>
            {
                ["platform"] = Application.platform.ToString(),
                ["unity_version"] = Application.unityVersion,
                ["app_version"] = Application.version,
                ["device_model"] = SystemInfo.deviceModel,
                ["device_name"] = SystemInfo.deviceName,
            };

            if (overrides != null)
            {
                foreach (KeyValuePair<string, object> kv in overrides)
                    ctx[kv.Key] = kv.Value;
            }

            return ctx;
        }
    }

    public readonly struct ExpressionSendResult
    {
        public readonly bool Ok;
        public readonly int StatusCode;
        public readonly string ResponseBody;
        public readonly string Error;

        public ExpressionSendResult(bool ok, int statusCode, string responseBody, string error)
        {
            Ok = ok;
            StatusCode = statusCode;
            ResponseBody = responseBody;
            Error = error;
        }
    }
}