using System.Collections.Generic;
using System.Text;

namespace Flock.Analytics
{
    /// <summary>One exception a hook saw, as the capture hands it on.</summary>
    internal sealed class FlockCapturedException
    {
        public string Message;
        public string StackTrace;

        /// <summary>Which hook saw it: one of the Source constants on <see cref="FlockRepeatedExceptionCounter"/>.</summary>
        public string Source;
    }

    /// <summary>A fault that repeated inside its window: sent once the window closes, beside the report sent when it opened.</summary>
    internal sealed class FlockExceptionRepeatSummary
    {
        public string Message;
        public string StackTrace;
        public string Source;

        /// <summary>Occurrences after the first, which was reported when the window opened.</summary>
        public int Repeats;
    }

    /// <summary>One report and a count per fault and window, and a limit on different faults a launch; the caller hands in the time.</summary>
    internal sealed class FlockRepeatedExceptionCounter
    {
        /// <summary>Unity's log handed it over.</summary>
        internal const string SourceLog = "log";

        /// <summary>A faulted task nobody awaited.</summary>
        internal const string SourceUnobservedTask = "unobserved_task";

        /// <summary>No thread caught it, while Unity's logging was switched off.</summary>
        internal const string SourceUnhandled = "unhandled";

        /// <summary>How many different faults one launch reports; past it, new faults are only counted as held back.</summary>
        internal const int MostDifferentFaultsPerLaunch = 100;

        // Frames joined to the key: enough to tell two call sites with one message apart, few enough not to split one bug.
        private const int FramesInKey = 2;

        private sealed class Window
        {
            public FlockCapturedException First;
            public double OpenedSeconds;
            public int Repeats;
        }

        private readonly double _windowSeconds;
        private readonly int _mostDifferentFaults;
        private readonly Dictionary<string, Window> _openWindows = new Dictionary<string, Window>();
        private readonly HashSet<string> _faultsReported = new HashSet<string>();
        private readonly List<FlockExceptionRepeatSummary> _closedEarly = new List<FlockExceptionRepeatSummary>();
        private double _earliestClose = double.MaxValue;
        private int _heldBack;

        /// <summary>A window of 0 reports every occurrence; the limit on different faults still holds.</summary>
        internal FlockRepeatedExceptionCounter(double windowSeconds, int mostDifferentFaults = MostDifferentFaultsPerLaunch)
        {
            _windowSeconds = windowSeconds > 0.0 ? windowSeconds : 0.0;
            _mostDifferentFaults = mostDifferentFaults > 0 ? mostDifferentFaults : 0;
        }

        internal double WindowSeconds => _windowSeconds;

        /// <summary>Faults not reported since the last take: over the launch's limit, or lost before they could be counted.</summary>
        internal int HeldBack => _heldBack;

        /// <summary>Source, message with numbers and addresses collapsed, and the first frames past Unity's logging: "Enemy 12 at 0x7f" is "Enemy 13 at 0x3a".</summary>
        internal static string MakeSameFaultKey(string source, string message, string stackTrace)
        {
            StringBuilder key = new StringBuilder(source ?? string.Empty).Append('|');
            string text = message ?? string.Empty;
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                // A 0x-prefixed run is one value, so its hex letters are not left behind to split the key.
                if (c == '0' && index + 1 < text.Length && (text[index + 1] == 'x' || text[index + 1] == 'X'))
                {
                    index += 2;
                    while (index < text.Length && IsHexDigit(text[index]))
                        index++;
                    key.Append('#');
                    continue;
                }
                if (char.IsDigit(c))
                {
                    while (index < text.Length && char.IsDigit(text[index]))
                        index++;
                    key.Append('#');
                    continue;
                }
                key.Append(c);
                index++;
            }
            key.Append('|');

            // Read in place: this runs for every occurrence of an exception thrown every frame.
            string stack = stackTrace ?? string.Empty;
            int lineStart = 0;
            int taken = 0;
            while (taken < FramesInKey && NextGameFrame(stack, ref lineStart, out int frameStart, out int frameEnd))
            {
                key.Append(stack, frameStart, frameEnd - frameStart).Append('\n');
                taken++;
            }
            return key.ToString();
        }

        /// <summary>A stack from its first frame past Unity's logging and the capture's own; empty when it has none.</summary>
        internal static string FromTheFirstFrameOfTheGame(string stackTrace)
        {
            int lineStart = 0;
            return NextGameFrame(stackTrace, ref lineStart, out int frameStart, out _) ? stackTrace.Substring(frameStart) : string.Empty;
        }

        // The next non-empty frame from lineStart that is not a logging frame, trimmed; lineStart moves past its line.
        private static bool NextGameFrame(string stack, ref int lineStart, out int frameStart, out int frameEnd)
        {
            while (lineStart < stack.Length)
            {
                int lineEnd = stack.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                    lineEnd = stack.Length;
                frameStart = lineStart;
                frameEnd = lineEnd;
                lineStart = lineEnd + 1;
                while (frameStart < frameEnd && char.IsWhiteSpace(stack[frameStart]))
                    frameStart++;
                while (frameEnd > frameStart && char.IsWhiteSpace(stack[frameEnd - 1]))
                    frameEnd--;
                if (frameEnd > frameStart && !IsLoggingFrame(stack, frameStart, frameEnd))
                    return true;
            }
            frameStart = 0;
            frameEnd = 0;
            return false;
        }

        /// <summary>True when this occurrence is reported now; false when it is counted as a repeat or held back.</summary>
        internal bool ShouldReportNow(string sameFaultKey, FlockCapturedException captured, double nowSeconds)
        {
            if (_openWindows.TryGetValue(sameFaultKey, out Window open))
            {
                if (nowSeconds - open.OpenedSeconds < _windowSeconds)
                {
                    open.Repeats++;
                    return false;
                }
                // Closed before a collect reached it: its repeats still happened, so they wait for the next collect.
                if (open.Repeats > 0)
                    _closedEarly.Add(Summarise(open));
                _openWindows.Remove(sameFaultKey);
            }

            if (!_faultsReported.Contains(sameFaultKey))
            {
                if (_faultsReported.Count >= _mostDifferentFaults)
                {
                    _heldBack++;
                    return false;
                }
                _faultsReported.Add(sameFaultKey);
            }

            if (_windowSeconds > 0.0)
            {
                _openWindows[sameFaultKey] = new Window { First = captured, OpenedSeconds = nowSeconds };
                if (nowSeconds + _windowSeconds < _earliestClose)
                    _earliestClose = nowSeconds + _windowSeconds;
            }
            return true;
        }

        /// <summary>Faults lost before they reached the counter (a full queue); reported with those held back.</summary>
        internal void CountHeldBack(int count)
        {
            if (count > 0)
                _heldBack += count;
        }

        /// <summary>Hands back the held-back count and starts it again from 0.</summary>
        internal int TakeHeldBack()
        {
            int count = _heldBack;
            _heldBack = 0;
            return count;
        }

        /// <summary>Adds the summaries of windows closed by now to <paramref name="into"/>; cheap when none is due, as it runs every frame.</summary>
        internal void CollectFinished(double nowSeconds, List<FlockExceptionRepeatSummary> into)
        {
            if (_closedEarly.Count > 0)
            {
                into.AddRange(_closedEarly);
                _closedEarly.Clear();
            }
            if (nowSeconds < _earliestClose)
                return;

            _earliestClose = double.MaxValue;
            List<string> closed = null;
            foreach (KeyValuePair<string, Window> pair in _openWindows)
            {
                if (nowSeconds - pair.Value.OpenedSeconds >= _windowSeconds)
                {
                    if (pair.Value.Repeats > 0)
                        into.Add(Summarise(pair.Value));
                    (closed ?? (closed = new List<string>())).Add(pair.Key);
                }
                else if (pair.Value.OpenedSeconds + _windowSeconds < _earliestClose)
                {
                    _earliestClose = pair.Value.OpenedSeconds + _windowSeconds;
                }
            }
            if (closed != null)
            {
                foreach (string key in closed)
                    _openWindows.Remove(key);
            }
        }

        /// <summary>Every window with repeats, however young: for quitting, when no later frame will close them.</summary>
        internal void CollectAll(List<FlockExceptionRepeatSummary> into)
        {
            into.AddRange(_closedEarly);
            _closedEarly.Clear();
            foreach (Window window in _openWindows.Values)
            {
                if (window.Repeats > 0)
                    into.Add(Summarise(window));
            }
            _openWindows.Clear();
            _earliestClose = double.MaxValue;
        }

        private static FlockExceptionRepeatSummary Summarise(Window window) => new FlockExceptionRepeatSummary
        {
            Message = window.First.Message,
            StackTrace = window.First.StackTrace,
            Source = window.First.Source,
            Repeats = window.Repeats
        };

        // Unity's logging, its log callback and the capture's own frames start a logged stack, the same for every fault.
        private static bool IsLoggingFrame(string stack, int frameStart, int frameEnd)
        {
            foreach (string prefix in LoggingFramePrefixes)
            {
                if (frameEnd - frameStart >= prefix.Length && string.CompareOrdinal(stack, frameStart, prefix, 0, prefix.Length) == 0)
                    return true;
            }
            return false;
        }

        private static readonly string[] LoggingFramePrefixes =
        {
            "UnityEngine.Debug", "UnityEngine.Logger", "UnityEngine.StackTraceUtility", "UnityEngine.Application:CallLogCallback",
            "Flock.Analytics.FlockExceptionCapture"
        };

        private static bool IsHexDigit(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
