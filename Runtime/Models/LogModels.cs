using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Flock.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum LogEventType
    {
        [EnumMember(Value = "exception")]
        Exception,

        [EnumMember(Value = "logic_error")]
        LogicError,

        [EnumMember(Value = "debug")]
        Debug
    }

    public class LogEventDataSchema
    {
        [JsonProperty("type")]
        public LogEventType Type { get; set; }

        [JsonProperty("game_version")]
        public string GameVersion { get; set; }

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
        public List<string> ErrorTracebackLines { get; set; }

        [JsonProperty("extra_data")]
        public Dictionary<string, object> ExtraData { get; set; }
    }

    public class LogEventRequest
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public LogEventDataSchema Data { get; set; }

        [JsonProperty("ts_unix")]
        public long TsUnix { get; set; }
    }

    public class LogEventsRequest
    {
        [JsonProperty("events")]
        public List<LogEventRequest> Events { get; set; } = new List<LogEventRequest>();
    }
}
