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
        [JsonProperty("expression")]
        public string Expression { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("ts_unix")]
        public long TimestampUnix { get; set; }
    }
    public class FlockExpressionService
    {
        private readonly string _baseUrl;
        private readonly string _endpointPath; // e.g. "/events/expression" or "/analytics/expression"
        private readonly Func<Task<string>> _getAccessToken; // optional maybe?
        private readonly string _gameId; // optional maybe?
        private readonly int _timeoutSeconds;

        /// <param name="baseUrl">Example: https://api.flock.qwacks.com</param>
        /// <param name="endpointPath">Example: /events/expression</param>
        /// <param name="getAccessToken">Return bearer token or empty string if none</param>
        /// <param name="gameId">Sent as X-Game-Id if provided</param>
        /// <param name="timeoutSeconds">time out value for request</param>
        public FlockExpressionService(
            string baseUrl,
            string endpointPath,
            Func<Task<string>> getAccessToken = null,
            string gameId = null,
            int timeoutSeconds = 15)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _endpointPath = endpointPath.StartsWith("/") ? endpointPath : "/" + endpointPath;
            _getAccessToken = getAccessToken;
            _gameId = gameId;
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>
        /// Sends: { expression: "...", data: {...}, ts_unix: ..., context: {...} }
        /// </summary>
        public async Task<ExpressionSendResult> SendExpressionAsync(
            string expressionName,
            Dictionary<string, object> customData = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(expressionName))
                throw new ArgumentException("expression is required", nameof(expressionName));

            var data = BuildDefaultContext(customData);
            var payload = new ExpressionEventPayload
            {
                Expression = expressionName,
                Data = data ?? new Dictionary<string, object>(),
                TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var json = JsonConvert.SerializeObject(payload);
            var url = _baseUrl + _endpointPath;

            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.timeout = _timeoutSeconds;
            req.downloadHandler = new DownloadHandlerBuffer();
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));

            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");

            if (!string.IsNullOrWhiteSpace(_gameId))
                req.SetRequestHeader("ChangeThisTaka", _gameId);

            if (_getAccessToken != null)
            {
                var token = await _getAccessToken();
                if (!string.IsNullOrWhiteSpace(token))
                    req.SetRequestHeader("Authorization", $"Bearer {token}");
            }

            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            var status = (int)req.responseCode;
            var body = req.downloadHandler?.text ?? "";
            var error = req.result == UnityWebRequest.Result.Success ? null : req.error;

            // Treat 2xx as success ? need to know response structure 
            var ok = status >= 200 && status < 300;

            return new ExpressionSendResult(ok, status, body, error);
        }

        private static Dictionary<string, object> BuildDefaultContext(Dictionary<string, object> overrides)
        {
            var ctx = new Dictionary<string, object>
            {
                ["platform"] = Application.platform.ToString(),
                ["unity_version"] = Application.unityVersion,
                ["app_version"] = Application.version,
                ["device_model"] = SystemInfo.deviceModel,
                ["device_name"] = SystemInfo.deviceName,
            };

            if (overrides != null)
            {
                foreach (var kv in overrides)
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