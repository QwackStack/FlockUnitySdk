using System;
using System.Text;

namespace Flock.Logging
{
    public interface IFlockLogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogError(string message, Exception exception);
        void LogDebug(string message);
    }

    public class UnityFlockLogger : IFlockLogger
    {
        public void LogInfo(string message) => UnityEngine.Debug.Log(new StringBuilder().Append("[Flock SDK] ")
            .Append(message)
            .ToString());
        public void LogWarning(string message) => UnityEngine.Debug.LogWarning(new StringBuilder()
            .Append("[Flock SDK] ")
            .Append(message)
            .ToString());
        public void LogError(string message) => UnityEngine.Debug.LogError(new StringBuilder().Append("[Flock SDK] ")
            .Append(message)
            .ToString());
        public void LogError(string message, Exception exception) => UnityEngine.Debug.LogError(new StringBuilder()
            .Append("[Flock SDK] ")
            .Append(message)
            .Append("\nException: ")
            .Append(exception)
            .ToString());
        public void LogDebug(string message) => UnityEngine.Debug.Log(new StringBuilder().Append("[Flock SDK] ")
            .Append(message)
            .ToString());
    }

    public class NullFlockLogger : IFlockLogger
    {
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
        public void LogError(string message, Exception exception) { }
        public void LogDebug(string message) { }
    }
}
