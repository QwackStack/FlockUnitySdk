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
        public void LogError(string message, Exception exception) => UnityEngine.Debug.LogError($"[Flock SDK] {message}\nException: {FlockExceptionText.FullText(exception)}");
        public void LogException(Exception exception)
        {
            _loggingAnException = true;
            try
            {
                UnityEngine.Debug.LogException(exception);
            }
            finally
            {
                _loggingAnException = false;
            }
        }

        public void LogDebug(string message) { if (_verbose) UnityEngine.Debug.Log($"[Flock SDK] {message}"); }

        [ThreadStatic] private static bool _loggingAnException;

        /// <summary>True while this thread hands Unity an exception of the SDK's own, which exception capture leaves out: it is not the game's.</summary>
        internal static bool IsLoggingAnExceptionOnThisThread => _loggingAnException;
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
