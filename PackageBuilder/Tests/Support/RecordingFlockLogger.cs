using System;
using System.Collections.Generic;
using Flock.Logging;

namespace Flock.Tests.Support
{
    // Captures what the SDK logs instead of forwarding it to Unity. Two jobs: keep Debug.LogError out of the
    // test run (the framework fails a test on an unexpected one), and let a test assert what was reported.
    public class RecordingFlockLogger : IFlockLogger
    {
        private readonly List<string> _infos = new List<string>();
        private readonly List<string> _warnings = new List<string>();
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _debugs = new List<string>();

        // The SDK logs from threads of its own too (the relay's, a direct link's), so each level is written under its own lock and
        // read as a copy taken under it: a test's LINQ over a level never walks a list another thread is adding to.
        public List<string> Infos => Copy(_infos);
        public List<string> Warnings => Copy(_warnings);
        public List<string> Errors => Copy(_errors);
        public List<string> Debugs => Copy(_debugs);

        public void LogInfo(string message) => Add(_infos, message);
        public void LogWarning(string message) => Add(_warnings, message);
        public void LogError(string message) => Add(_errors, message);
        public void LogError(string message, Exception exception) => Add(_errors, $"{message}: {exception?.Message}");
        public void LogException(Exception exception) => Add(_errors, exception?.Message ?? "");
        public void LogDebug(string message) => Add(_debugs, message);

        public void Clear()
        {
            foreach (List<string> level in new[] { _infos, _warnings, _errors, _debugs })
            {
                lock (level)
                    level.Clear();
            }
        }

        /// <summary>True when any message in <paramref name="level"/> (a level read from this logger) contains <paramref name="fragment"/>.</summary>
        public bool Logged(List<string> level, string fragment) => level.Exists(m => m != null && m.Contains(fragment));

        private static List<string> Copy(List<string> level)
        {
            lock (level)
                return new List<string>(level);
        }

        private static void Add(List<string> level, string message)
        {
            lock (level)
                level.Add(message);
        }
    }
}
