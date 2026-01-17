using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.LogEvent
{
    /// <summary>
    /// Type of log event
    /// </summary>
    public enum LogEventType
    {
        Exception,
        LogicError
    }

    /// <summary>
    /// Data schema for log event
    /// </summary>
    [Serializable]
    public class LogEventDataSchema
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("logical_expression")]
        public string LogicalExpression { get; set; }

        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; }

        [JsonProperty("error_code")]
        public string ErrorCode { get; set; }

        [JsonProperty("error_data")]
        public Dictionary<string, object> ErrorData { get; set; }

        [JsonProperty("error_traceback")]
        public string ErrorTraceback { get; set; }

        [JsonProperty("error_traceback_lines")]
        public string[] ErrorTracebackLines { get; set; }

        [JsonProperty("extra_data")]
        public Dictionary<string, object> ExtraData { get; set; }
    }

    /// <summary>
    /// Request model for creating a log event
    /// </summary>
    [Serializable]
    public class LogEventRequest
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public LogEventDataSchema Data { get; set; }

        [JsonProperty("ts_unix")]
        public long TsUnix { get; set; }
    }

    /// <summary>
    /// Response model for log event
    /// </summary>
    [Serializable]
    public class LogEventSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public LogEventDataSchema Data { get; set; }

        [JsonProperty("additional_data")]
        public Dictionary<string, object> AdditionalData { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }
    }
}
