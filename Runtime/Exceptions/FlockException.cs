using System;

namespace Flock.Exceptions
{
    public class FlockException : Exception
    {
        public FlockException(string message) : base(message) { }
        public FlockException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class FlockNetworkException : FlockException
    {
        public int? StatusCode { get; set; }
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
}
