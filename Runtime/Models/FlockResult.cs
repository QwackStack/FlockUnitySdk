using System;

namespace Flock.Models
{
    /// <summary>
    /// Represents the result of an API operation with success/failure state
    /// </summary>
    public class FlockResult<T>
    {
        /// <summary>
        /// Indicates if the operation was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// The data returned from the operation (null if failed)
        /// </summary>
        public T Data { get; set; }

        /// <summary>
        /// Error message if the operation failed
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// HTTP status code from the response
        /// </summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// Exception that occurred during the operation (if any)
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Creates a successful result with data
        /// </summary>
        public static FlockResult<T> Ok(T data, int statusCode = 200)
        {
            return new FlockResult<T>
            {
                Success = true,
                Data = data,
                StatusCode = statusCode
            };
        }

        /// <summary>
        /// Creates a failed result with error message
        /// </summary>
        public static FlockResult<T> Fail(string errorMessage, int statusCode = 0, Exception exception = null)
        {
            return new FlockResult<T>
            {
                Success = false,
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                Exception = exception
            };
        }

        /// <summary>
        /// Creates a failed result from an exception
        /// </summary>
        public static FlockResult<T> FromException(Exception exception, string message = null)
        {
            return new FlockResult<T>
            {
                Success = false,
                ErrorMessage = message ?? exception.Message,
                Exception = exception
            };
        }
    }

    /// <summary>
    /// Represents the result of a void operation
    /// </summary>
    public class FlockResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int StatusCode { get; set; }
        public Exception Exception { get; set; }

        public static FlockResult Ok(int statusCode = 200)
        {
            return new FlockResult
            {
                Success = true,
                StatusCode = statusCode
            };
        }

        public static FlockResult Fail(string errorMessage, int statusCode = 0, Exception exception = null)
        {
            return new FlockResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                Exception = exception
            };
        }

        public static FlockResult FromException(Exception exception, string message = null)
        {
            return new FlockResult
            {
                Success = false,
                ErrorMessage = message ?? exception.Message,
                Exception = exception
            };
        }
    }
}
