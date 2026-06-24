using System;

namespace Flock.Exceptions
{
    public class FlockException : Exception
    {
        /// <summary>Raw server response body, kept off Message so error trackers bucket by type instead of payload.</summary>
        public string Body { get; set; }

        /// <summary>HTTP status from the server response when the error came from one; null for client-side or transport failures.</summary>
        public int? StatusCode { get; set; }

        /// <summary>Server's machine-readable error code from the coded-error body (e.g. "player.email_already_registered"); null when the body had none.</summary>
        public string Code { get; set; }

        /// <summary>Typed form of <see cref="Code"/> for readable checks/switches; <see cref="FlockErrorCode.Unknown"/> when there was no code or this SDK version doesn't recognize it.</summary>
        public FlockErrorCode ErrorCode => FlockErrorCodes.Parse(Code);

        public FlockException(string message) : base(message) { }
        public FlockException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>Appends <see cref="Body"/> to the standard exception text so logs show the server's reason while Message stays terse for error-tracker grouping.</summary>
        public override string ToString()
        {
            return string.IsNullOrEmpty(Body) ? base.ToString() : $"{base.ToString()}\nResponse body: {Body}";
        }
    }

    public class FlockNetworkException : FlockException
    {
        /// <summary>Server-provided Retry-After hint (429/503); honored by the retry handler when set.</summary>
        public TimeSpan? RetryAfter { get; set; }

        public FlockNetworkException(string message) : base(message) { }
        public FlockNetworkException(string message, Exception innerException) : base(message, innerException) { }

        public FlockNetworkException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        public FlockNetworkException(string message, int statusCode, Exception innerException) : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        // Permanent 4xx is an authoritative server answer; 408/429 are 4xx but transient by spec.
        public static bool IsPermanentStatus(int? statusCode)
        {
            if (!statusCode.HasValue)
                return false;

            int code = statusCode.Value;
            if (code == 408 || code == 429)
                return false;

            return code >= 400 && code < 500;
        }
    }

    public class FlockAuthException : FlockException
    {
        public FlockAuthException(string message) : base(message) { }
        public FlockAuthException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class FlockValidationException : FlockException
    {
        public FlockValidationException(string message) : base(message) { }
        public FlockValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>The server's response could not be turned into the expected type — malformed JSON or an empty body. Permanent, so it is not retried.</summary>
    public class FlockSerializationException : FlockException
    {
        public FlockSerializationException(string message) : base(message) { }
        public FlockSerializationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
