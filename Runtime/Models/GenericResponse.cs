using Newtonsoft.Json;

namespace Flock.Models
{
    public class ErrorSchema
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    public class ResponseSchema
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }
    }

    public class GenericResponse<T>
    {
        [JsonProperty("error")]
        public ErrorSchema Error { get; set; }

        [JsonProperty("response")]
        public ResponseSchema Response { get; set; }

        [JsonProperty("result")]
        public T Result { get; set; }
    }

    /// <summary>Coded-error envelope returned on 4xx/5xx client-route failures: {"detail":{"code","message"}}.</summary>
    public class CodedErrorResponse
    {
        [JsonProperty("detail")]
        public CodedErrorDetail Detail { get; set; }
    }

    /// <summary>Machine-readable error code (e.g. "player.email_already_registered") plus its human message.</summary>
    public class CodedErrorDetail
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
