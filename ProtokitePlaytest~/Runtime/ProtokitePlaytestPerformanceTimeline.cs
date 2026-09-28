using System;
using System.Collections.Generic;

namespace Protokite.Playtest
{
    /// <summary>The category and names of the events a playtest sends through the Flock SDK when the playtest turns heavy analytics on.</summary>
    public static class ProtokitePlaytestEvents
    {
        /// <summary>Every playtest event is filed under this category, so a dashboard can show or hide them as a group.</summary>
        public const string Category = "playtest";

        /// <summary>One per ten seconds of play: frame times, hitches and memory.</summary>
        public const string PerformanceWindow = "performance_window";

        /// <summary>One per scene the game loads in place of the one before.</summary>
        public const string LevelLoaded = "level_loaded";
    }

    /// <summary>What one window of play measured.</summary>
    internal sealed class ProtokitePlaytestPerformanceWindow
    {
        /// <summary>The play time the window covers, added up from frame times, so a change to the clock cannot stretch it.</summary>
        public double Seconds;

        public int Frames;
        public double MedianFrameTimeMs;
        public double FrameTime95thPercentileMs;
        public double FrameTime99thPercentileMs;

        /// <summary>Frames that took <see cref="HitchThresholdMs"/> or longer.</summary>
        public int Hitches;
        public double HitchThresholdMs;

        /// <summary>The memory the game's process used when the window closed; null where the platform does not report it.</summary>
        public long? MemoryUsedMb;

        /// <summary>The most the process used since measuring started; null where the platform does not report it.</summary>
        public long? MemoryPeakMb;
    }

    /// <summary>Turns frame times into windows of ten seconds of play; it owns no timer and knows nothing of scenes or sending.</summary>
    internal sealed class ProtokitePlaytestPerformanceTimeline
    {
        internal const double WindowSeconds = 10.0;
        private const long BytesPerMegabyte = 1024 * 1024;

        private readonly Func<double> _readHitchThresholdMs;
        private readonly Func<long?> _readMemoryUsedBytes;
        private readonly List<double> _frameTimesMs = new List<double>(1024);
        private double _elapsedSeconds;
        private bool _leaveOutNextFrame;
        private long? _memoryPeakBytes;

        /// <summary>The hitch threshold is read when a window closes; the memory every frame, so the peak sees what happened between windows.</summary>
        internal ProtokitePlaytestPerformanceTimeline(Func<double> readHitchThresholdMs, Func<long?> readMemoryUsedBytes)
        {
            _readHitchThresholdMs = readHitchThresholdMs;
            _readMemoryUsedBytes = readMemoryUsedBytes;
        }

        /// <summary>Adds one frame. True, with the window, when this frame finishes one. A frame with no time is not a frame.</summary>
        internal bool AddFrame(double frameSeconds, out ProtokitePlaytestPerformanceWindow window)
        {
            window = null;
            if (!(frameSeconds > 0.0))
                return false;

            long? memoryUsedBytes = _readMemoryUsedBytes();
            if (memoryUsedBytes.HasValue && (!_memoryPeakBytes.HasValue || memoryUsedBytes.Value > _memoryPeakBytes.Value))
                _memoryPeakBytes = memoryUsedBytes;

            if (_leaveOutNextFrame)
            {
                _leaveOutNextFrame = false;
                return false;
            }

            _frameTimesMs.Add(frameSeconds * 1000.0);
            _elapsedSeconds += frameSeconds;
            if (_elapsedSeconds < WindowSeconds)
                return false;

            _frameTimesMs.Sort();
            double hitchThresholdMs = _readHitchThresholdMs();
            window = new ProtokitePlaytestPerformanceWindow
            {
                Seconds = _elapsedSeconds,
                Frames = _frameTimesMs.Count,
                MedianFrameTimeMs = Percentile(_frameTimesMs, 50.0),
                FrameTime95thPercentileMs = Percentile(_frameTimesMs, 95.0),
                FrameTime99thPercentileMs = Percentile(_frameTimesMs, 99.0),
                HitchThresholdMs = hitchThresholdMs,
                // A frame that took exactly the threshold is a hitch.
                Hitches = _frameTimesMs.Count - FirstIndexAtOrOver(_frameTimesMs, hitchThresholdMs),
                MemoryUsedMb = memoryUsedBytes / BytesPerMegabyte,
                MemoryPeakMb = _memoryPeakBytes / BytesPerMegabyte
            };
            _frameTimesMs.Clear();
            _elapsedSeconds = 0.0;
            return true;
        }

        /// <summary>Leaves out the next frame added, whose time was not play: time in the background, or a scene load.</summary>
        internal void LeaveOutNextFrame() => _leaveOutNextFrame = true;

        /// <summary>The frame times the window in progress has counted, in the order they came, for tests.</summary>
        internal IReadOnlyList<double> FrameTimesMsSoFarForTesting => _frameTimesMs;

        /// <summary>The frame time that <paramref name="percent"/> of the frames took or less (nearest rank), from times sorted shortest first. 0 with no frames.</summary>
        internal static double Percentile(List<double> sortedFrameTimesMs, double percent)
        {
            int count = sortedFrameTimesMs.Count;
            if (count == 0)
                return 0.0;
            long rank = (long)Math.Ceiling(percent * count / 100.0);
            return sortedFrameTimesMs[(int)Math.Max(0, Math.Min(rank - 1, count - 1))];
        }

        // The first index whose time is the threshold or more, in times sorted shortest first; the count when none is.
        private static int FirstIndexAtOrOver(List<double> sortedFrameTimesMs, double thresholdMs)
        {
            int low = 0;
            int high = sortedFrameTimesMs.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (sortedFrameTimesMs[middle] < thresholdMs)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }
}
