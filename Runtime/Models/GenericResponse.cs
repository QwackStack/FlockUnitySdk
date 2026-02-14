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
}
