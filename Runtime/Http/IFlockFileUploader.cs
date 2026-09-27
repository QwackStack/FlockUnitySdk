using System.Threading;
using System.Threading.Tasks;

namespace Flock.Http
{
    /// <summary>Streams a file from disk to a URL, as a presigned storage link wants it: the file is the body, read as it is sent.</summary>
    // Kept apart from IFlockHttpAdapter, whose requests carry a JSON string and a short timeout, so a studio's own adapter is untouched.
    public interface IFlockFileUploader
    {
        /// <summary>PUTs the file with this Content-Type and no other SDK header; throws only on cancel, and every other failure is in the outcome.</summary>
        Task<FlockFileUploadOutcome> UploadFileAsync(string url, string filePath, string contentType, CancellationToken cancellationToken);
    }

    /// <summary>What became of a file upload.</summary>
    public sealed class FlockFileUploadOutcome
    {
        /// <summary>Success when an HTTP status came back (any status); Timeout or ConnectionError when none did, or the upload could not begin.</summary>
        public FlockHttpResult Result { get; set; }

        /// <summary>The HTTP status when <see cref="Result"/> is Success; 0 otherwise.</summary>
        public int StatusCode { get; set; }

        /// <summary>The answer's body when there was one, or why no answer came.</summary>
        public string Body { get; set; }

        /// <summary>Bytes of the file sent before the upload ended.</summary>
        public long BytesSent { get; set; }

        /// <summary>True only when the storage answered 2xx: the one proof the file arrived.</summary>
        public bool IsUploaded => Result == FlockHttpResult.Success && StatusCode >= 200 && StatusCode < 300;
    }
}
