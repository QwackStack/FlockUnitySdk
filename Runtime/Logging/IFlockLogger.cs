using System;

namespace Flock.Logging
{
    public interface IFlockLogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogError(string message, Exception exception);
        void LogException(Exception exception);
        void LogDebug(string message);
    }

    public class UnityFlockLogger : IFlockLogger
    {
        private readonly bool _verbose;

        /// <summary>Errors and warnings always surface; <paramref name="verbose"/> adds info and debug on top.</summary>
        public UnityFlockLogger(bool verbose = true) => _verbose = verbose;

        public void LogInfo(string message) { if (_verbose) UnityEngine.Debug.Log($"[Flock SDK] {message}"); }
        public void LogWarning(string message) => UnityEngine.Debug.LogWarning($"[Flock SDK] {message}");
        public void LogError(string message) => UnityEngine.Debug.LogError($"[Flock SDK] {message}");
        public void LogError(string message, Exception exception) => UnityEngine.Debug.LogError($"[Flock SDK] {message}\nException: {exception}");
        public void LogException(Exception exception) => UnityEngine.Debug.LogException(exception);
        public void LogDebug(string message) { if (_verbose) UnityEngine.Debug.Log($"[Flock SDK] {message}"); }
    }

    public class NullFlockLogger : IFlockLogger
    {
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception exception) { }
        public void LogException(Exception exception) { }
        public void LogDebug(string message) { }
    }
}
