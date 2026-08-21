using System;
using System.Collections.Generic;
using Flock.Logging;

namespace Flock.Tests.Support
{
    // Captures what the SDK logs instead of forwarding it to Unity. Two jobs: keep Debug.LogError out of the
    // test run (the framework fails a test on an unexpected one), and let a test assert what was reported.
    public class RecordingFlockLogger : IFlockLogger
    {
        public readonly List<string> Infos = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Debugs = new List<string>();

        public void LogInfo(string message) => Infos.Add(message);
        public void LogWarning(string message) => Warnings.Add(message);
        public void LogError(string message) => Errors.Add(message);
        public void LogError(string message, Exception exception) => Errors.Add($"{message}: {exception?.Message}");
        public void LogException(Exception exception) => Errors.Add(exception?.Message ?? "");
        public void LogDebug(string message) => Debugs.Add(message);

        public void Clear()
        {
            Infos.Clear();
            Warnings.Clear();
            Errors.Clear();
            Debugs.Clear();
        }

        /// <summary>True when any message at the given level contains <paramref name="fragment"/>.</summary>
        public bool Logged(List<string> level, string fragment)
        {
            return level.Exists(m => m != null && m.Contains(fragment));
        }
    }
}
