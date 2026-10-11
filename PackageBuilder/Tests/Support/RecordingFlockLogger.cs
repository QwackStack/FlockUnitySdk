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

        // The SDK logs from threads of its own too (the relay's, a direct link's), so each list is written and read under its own lock.
        public void LogInfo(string message) => Add(Infos, message);
        public void LogWarning(string message) => Add(Warnings, message);
        public void LogError(string message) => Add(Errors, message);
        public void LogError(string message, Exception exception) => Add(Errors, $"{message}: {exception?.Message}");
        public void LogException(Exception exception) => Add(Errors, exception?.Message ?? "");
        public void LogDebug(string message) => Add(Debugs, message);

        public void Clear()
        {
            foreach (List<string> level in new[] { Infos, Warnings, Errors, Debugs })
            {
                lock (level)
                    level.Clear();
            }
        }

        /// <summary>True when any message at the given level contains <paramref name="fragment"/>.</summary>
        public bool Logged(List<string> level, string fragment)
        {
            lock (level)
                return level.Exists(m => m != null && m.Contains(fragment));
        }

        private static void Add(List<string> level, string message)
        {
            lock (level)
                level.Add(message);
        }
    }
}
