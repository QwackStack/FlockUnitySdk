using System;

namespace Flock.Exceptions
{
    /// <summary>
    /// Base exception for all Flock SDK errors
    /// </summary>
    public class FlockException : Exception
    {
        public FlockException() : base() { }

        public FlockException(string message) : base(message) { }

        public FlockException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when network-related errors occur
    /// </summary>
    public class FlockNetworkException : FlockException
    {
        public int? StatusCode { get; set; }

        public FlockNetworkException() : base() { }

        public FlockNetworkException(string message) : base(message) { }

        public FlockNetworkException(string message, Exception innerException)
            : base(message, innerException) { }

        public FlockNetworkException(string message, int statusCode)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public FlockNetworkException(string message, int statusCode, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// Exception thrown when authentication or authorization fails
    /// </summary>
    public class FlockAuthException : FlockException
    {
        public FlockAuthException() : base() { }

        public FlockAuthException(string message) : base(message) { }

        public FlockAuthException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when API validation fails
    /// </summary>
    public class FlockValidationException : FlockException
    {
        public FlockValidationException() : base() { }

        public FlockValidationException(string message) : base(message) { }

        public FlockValidationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when token refresh fails
    /// </summary>
    public class FlockTokenRefreshException : FlockAuthException
    {
        public FlockTokenRefreshException() : base() { }

        public FlockTokenRefreshException(string message) : base(message) { }

        public FlockTokenRefreshException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
