using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Exceptions;
using Flock.Models;
using UnityEngine;

namespace Flock.LogEvent
{
    /// <summary>
    /// Static service for logging events to the Flock API
    /// </summary>
    public static class FlockLogEventService
    {
        // Hardcoded configuration
        private const string API_URL = "https://api-flock.qwacks.com";
        private const string gameId = "gameId";
        private const string path = "/log_event";

        /// <summary>
        /// Creates a log event for an exception
        /// </summary>
        public static async Task<LogEventSchema> LogExceptionAsync(
            string message,
            Exception exception,
            Dictionary<string, object> extraData = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Message cannot be null or empty", nameof(message));
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            var errorTracebackLines = exception.StackTrace?.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);

            var data = new LogEventDataSchema
            {
                Type = LogEventType.Exception.ToString().ToLower(),
                ErrorMessage = exception.Message,
                ErrorCode = exception.GetType().Name,
                ErrorTraceback = exception.StackTrace,
                ErrorTracebackLines = errorTracebackLines,
                ExtraData = extraData ?? new Dictionary<string, object>()
            };

            AddDefaultContext(data.ExtraData);
            return await CreateLogEventAsync(message, data, cancellationToken);
        }

        /// <summary>
        /// Creates a log event for a logic error
        /// </summary>
        public static async Task<LogEventSchema> LogLogicErrorAsync(
            string message,
            string errorCode = null,
            Dictionary<string, object> errorData = null,
            Dictionary<string, object> extraData = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Message cannot be null or empty", nameof(message));

            var data = new LogEventDataSchema
            {
                Type = LogEventType.LogicError.ToString().ToLower(),
                ErrorCode = errorCode,
                ErrorData = errorData,
                ExtraData = extraData ?? new Dictionary<string, object>()
            };

            AddDefaultContext(data.ExtraData);
            return await CreateLogEventAsync(message, data, cancellationToken);
        }

        /// <summary>
        /// Creates a log event on the server
        /// </summary>
        private static async Task<LogEventSchema> CreateLogEventAsync(
            string message,
            LogEventDataSchema data,
            CancellationToken cancellationToken)
        {
            var request = new LogEventRequest
            {
                Message = message,
                Data = data,
                TsUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var url = $"{API_URL}{path}";
            var headers = new Dictionary<string, string>
            {
                { "game-id", gameId },
                { "client-id", gameId },
                { "version", Application.version },
                { "device-id", SystemInfo.deviceUniqueIdentifier }
            };

            var response = await HttpClient.PostAsyncWithHeaders<GenericResponse<LogEventSchema>>(
                url,
                request,
                headers,
                null,
                cancellationToken);

            if (response?.Result == null)
                throw new FlockNetworkException("Invalid response from log event endpoint");

            return response.Result;
        }

        /// <summary>
        /// Adds default context information to the extra data
        /// </summary>
        private static void AddDefaultContext(Dictionary<string, object> extraData)
        {
            extraData["platform"] = Application.platform.ToString();
            extraData["unity_version"] = Application.unityVersion;
            extraData["app_version"] = Application.version;
            extraData["device_model"] = SystemInfo.deviceModel;
            extraData["device_name"] = SystemInfo.deviceName;
            extraData["device_type"] = SystemInfo.deviceType.ToString();
            extraData["operating_system"] = SystemInfo.operatingSystem;
        }
    }
}
