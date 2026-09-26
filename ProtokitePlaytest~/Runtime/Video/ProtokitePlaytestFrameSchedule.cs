using System;

namespace Protokite.Playtest
{
    /// <summary>What the frame schedule decided about one game frame.</summary>
    internal enum ProtokitePlaytestFrameDecision
    {
        /// <summary>Not captured.</summary>
        Skip,
        /// <summary>Captured, shown at the time handed back.</summary>
        Capture,
        /// <summary>The recording holds as much time as it may; nothing more is captured.</summary>
        ReachedLengthLimit,
    }

    /// <summary>
    /// Decides which game frames a recording captures, and when each is shown. Inert: it is handed each frame's time and
    /// keeps no clock of its own, so time the game spends not drawing, or in the background, is not recorded. At each capture
    /// time the frame nearest it is taken, which keeps a steady rhythm when frame times wobble; a game drawing fewer frames than
    /// the capture rate has every frame captured.
    /// </summary>
    internal sealed class ProtokitePlaytestFrameSchedule
    {
        private readonly double _captureIntervalSeconds;
        private readonly double _maxSeconds;
        private double _nextCaptureSeconds;
        private long _lastTimestampMs = -1;
        private bool _leaveOutNextFrame;

        /// <summary>How much time the recording holds so far.</summary>
        public double RecordedSeconds { get; private set; }

        public ProtokitePlaytestFrameSchedule(int framesPerSecond, double maxSeconds)
        {
            _captureIntervalSeconds = 1.0 / Math.Max(1, framesPerSecond);
            // A limit that is not a number would never be reached; it records nothing instead.
            _maxSeconds = double.IsNaN(maxSeconds) ? 0 : maxSeconds;
        }

        /// <summary>The next frame's time is not recorded: the frame that carries time spent in the background.</summary>
        public void LeaveOutNextFrame()
        {
            _leaveOutNextFrame = true;
        }

        /// <summary>One frame's time. When it is captured, timestampMs is when it is shown, from the start of the recording.</summary>
        public ProtokitePlaytestFrameDecision AddFrame(double frameSeconds, out long timestampMs)
        {
            timestampMs = -1;
            // Not a positive, finite time is not a frame; an endless one would also never reach its next capture time.
            if (!(frameSeconds > 0.0) || double.IsInfinity(frameSeconds))
            {
                return ProtokitePlaytestFrameDecision.Skip;
            }
            if (_leaveOutNextFrame)
            {
                _leaveOutNextFrame = false;
                return ProtokitePlaytestFrameDecision.Skip;
            }

            double frameStartSeconds = RecordedSeconds;
            if (frameStartSeconds >= _maxSeconds)
            {
                return ProtokitePlaytestFrameDecision.ReachedLengthLimit;
            }
            RecordedSeconds += frameSeconds;

            // The frame whose middle has reached the capture time is the one nearest it. Deciding on the frame's start instead
            // takes the frame before or after as its time wobbles either side, and the video stutters.
            double frameMiddleSeconds = frameStartSeconds + frameSeconds * 0.5;
            if (frameMiddleSeconds < _nextCaptureSeconds)
            {
                return ProtokitePlaytestFrameDecision.Skip;
            }
            // A frame far longer than the interval jumps most of the way at once; one interval at a time it could run for ever.
            if (frameMiddleSeconds - _nextCaptureSeconds > _captureIntervalSeconds * 1000)
            {
                _nextCaptureSeconds += Math.Floor((frameMiddleSeconds - _nextCaptureSeconds) / _captureIntervalSeconds) * _captureIntervalSeconds;
            }
            while (_nextCaptureSeconds <= frameMiddleSeconds)
            {
                double next = _nextCaptureSeconds + _captureIntervalSeconds;
                _nextCaptureSeconds = next > _nextCaptureSeconds ? next : double.PositiveInfinity;
            }

            // Frames far shorter than a millisecond could share one; a video needs every time later than the last.
            long shownAtMs = (long)Math.Round(frameStartSeconds * 1000.0, MidpointRounding.AwayFromZero);
            if (shownAtMs <= _lastTimestampMs)
            {
                return ProtokitePlaytestFrameDecision.Skip;
            }
            _lastTimestampMs = shownAtMs;
            timestampMs = shownAtMs;
            return ProtokitePlaytestFrameDecision.Capture;
        }
    }
}
