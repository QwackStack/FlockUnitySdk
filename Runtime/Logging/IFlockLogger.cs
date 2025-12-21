using System;

namespace Flock.Logging
{
    /// <summary>
    /// Logging abstraction for the Flock SDK
    /// </summary>
    public interface IFlockLogger
    {
        /// <summary>
        /// Logs an informational message
        /// </summary>
        void LogInfo(string message);

        /// <summary>
        /// Logs a warning message
        /// </summary>
        void LogWarning(string message);

        /// <summary>
        /// Logs an error message
        /// </summary>
        void LogError(string message);

        /// <summary>
        /// Logs an error with exception details
        /// </summary>
        void LogError(string message, Exception exception);

        /// <summary>
        /// Logs a debug message (only when debug logs are enabled)
        /// </summary>
        void LogDebug(string message);
    }

    /// <summary>
    /// Unity-specific logger implementation using UnityEngine.Debug
    /// </summary>
    public class UnityFlockLogger : IFlockLogger
    {
        
        public void LogInfo(string message)
        {
            UnityEngine.Debug.Log($"[Flock SDK] {message}");
        }

        public void LogWarning(string message)
        {
            UnityEngine.Debug.LogWarning($"[Flock SDK] {message}");
        }

        public void LogError(string message)
        {
            UnityEngine.Debug.LogError($"[Flock SDK] {message}");
        }

        public void LogError(string message, Exception exception)
        {
            UnityEngine.Debug.LogError($"[Flock SDK] {message}\nException: {exception}");
        }

        public void LogDebug(string message)
        {
            UnityEngine.Debug.Log($"[Flock SDK Debug] {message}");
        }
    }

    /// <summary>
    /// No-op logger for when logging is disabled
    /// </summary>
    public class NullFlockLogger : IFlockLogger
    {
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception exception) { }
        public void LogDebug(string message) { }
    }
}
