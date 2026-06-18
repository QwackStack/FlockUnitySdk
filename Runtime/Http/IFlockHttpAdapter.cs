using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Flock.Http
{
    /// <summary>Transport seam for SDK HTTP. Picked per-platform so WebGL uses UnityWebRequest while other platforms use System.Net.Http.</summary>
    public interface IFlockHttpAdapter
    {
        /// <summary>Sends one request and returns a normalized response. Throws OperationCanceledException when the token cancels; transport failures come back as a non-Success result, not an exception.</summary>
        Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Transport outcome of a request the adapter attempted.</summary>
    public enum FlockHttpResult
    {
        /// <summary>The HTTP exchange completed; StatusCode is set (any status, including 4xx/5xx).</summary>
        Success,
        /// <summary>The request timed out before a response arrived.</summary>
        Timeout,
        /// <summary>A transport-level failure (DNS, refused, offline) — no HTTP response.</summary>
        ConnectionError
    }

    /// <summary>Normalized request handed to an <see cref="IFlockHttpAdapter"/>.</summary>
    public sealed class FlockHttpRequest
    {
        public string Method { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; set; }

        /// <summary>Serialized JSON body, or null for bodyless verbs (GET/DELETE).</summary>
        public string JsonBody { get; set; }
    }

    /// <summary>Normalized response returned by an <see cref="IFlockHttpAdapter"/>.</summary>
    public sealed class FlockHttpResponse
    {
        public FlockHttpResult Result { get; set; }

        /// <summary>HTTP status code when <see cref="Result"/> is Success; 0 otherwise.</summary>
        public int StatusCode { get; set; }

        /// <summary>Response text on Success; transport error detail on failure.</summary>
        public string Body { get; set; }

        /// <summary>Raw Retry-After header value, or null. Parsed by the caller.</summary>
        public string RetryAfterHeader { get; set; }
    }
}
