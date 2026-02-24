using System;

namespace Flock.Exceptions
{
    public class FlockException : Exception
    {
        public FlockException() : base() { }
        public FlockException(string message) : base(message) { }
        public FlockException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class FlockNetworkException : FlockException
    {
        public int? StatusCode { get; set; }

        public FlockNetworkException() : base() { }
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
    }

    public class FlockAuthException : FlockException
    {
        public FlockAuthException() : base() { }
        public FlockAuthException(string message) : base(message) { }
        public FlockAuthException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class FlockValidationException : FlockException
    {
        public FlockValidationException() : base() { }
        public FlockValidationException(string message) : base(message) { }
        public FlockValidationException(string message, Exception innerException) : base(message, innerException) { }
    }
}
