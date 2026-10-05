using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    /// <summary>The performance-window maths, with no engine and no network: frame times in, windows out.</summary>
    public class ProtokitePlaytestPerformanceTimelineTests
    {
        private const long Megabyte = 1024 * 1024;

        private static ProtokitePlaytestPerformanceTimeline Timeline(double hitchThresholdMs = 60.0, long? memoryUsedBytes = 512 * Megabyte)
            => new ProtokitePlaytestPerformanceTimeline(() => hitchThresholdMs, () => memoryUsedBytes);

        // Adds each frame in order; the windows it finished.
        private static List<ProtokitePlaytestPerformanceWindow> Play(ProtokitePlaytestPerformanceTimeline timeline, IEnumerable<double> frameSeconds)
        {
            List<ProtokitePlaytestPerformanceWindow> windows = new List<ProtokitePlaytestPerformanceWindow>();
            foreach (double seconds in frameSeconds)
            {
                if (timeline.AddFrame(seconds, out ProtokitePlaytestPerformanceWindow window))
                    windows.Add(window);
            }
            return windows;
        }

        [Test]
        public void TenSecondsOfPlayMakeAWindowWhateverTheFrameCount()
        {
            ProtokitePlaytestPerformanceTimeline timeline = Timeline();
            Assert.IsEmpty(Play(timeline, Enumerable.Repeat(0.001, 1000)), "A thousand quick frames are one second of play, not a window");

            List<ProtokitePlaytestPerformanceWindow> windows = Play(Timeline(), Enumerable.Repeat(0.25, 80));
            Assert.AreEqual(2, windows.Count, "Twenty seconds of play at four frames a second");
            Assert.AreEqual(10.0, windows[0].Seconds);
            Assert.AreEqual(40, windows[0].Frames, "Closed by the frame that brought the play time to ten seconds");
            Assert.AreEqual(40, windows[1].Frames, "And the next window starts empty");
        }

        [Test]
        public void PercentilesAreTheNearestRank()
        {
            List<double> sorted = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
            Assert.AreEqual(50.0, ProtokitePlaytestPerformanceTimeline.Percentile(sorted, 50.0));
            Assert.AreEqual(95.0, ProtokitePlaytestPerformanceTimeline.Percentile(sorted, 95.0));
            Assert.AreEqual(99.0, ProtokitePlaytestPerformanceTimeline.Percentile(sorted, 99.0));
            Assert.AreEqual(7.0, ProtokitePlaytestPerformanceTimeline.Percentile(new List<double> { 7.0 }, 99.0), "One frame is every percentile");
            Assert.AreEqual(0.0, ProtokitePlaytestPerformanceTimeline.Percentile(new List<double>(), 50.0), "No frames, no time");
        }

        [Test]
        public void AWindowsFrameTimesPercentilesAndHitches()
        {
            // 36 frames of 200 ms, then 4 of 800 ms: the window closes on the last, at 10.4 seconds.
            List<double> frames = Enumerable.Repeat(0.2, 36).Concat(Enumerable.Repeat(0.8, 4)).ToList();

            ProtokitePlaytestPerformanceWindow atTheThreshold = Play(Timeline(hitchThresholdMs: 200.0), frames).Single();
            Assert.AreEqual(40, atTheThreshold.Frames);
            Assert.AreEqual(10.4, atTheThreshold.Seconds, 1e-9);
            Assert.AreEqual(200.0, atTheThreshold.MedianFrameTimeMs, 1e-9);
            Assert.AreEqual(800.0, atTheThreshold.FrameTime95thPercentileMs, 1e-9);
            Assert.AreEqual(800.0, atTheThreshold.FrameTime99thPercentileMs, 1e-9);
            Assert.AreEqual(40, atTheThreshold.Hitches, "A frame that took exactly the threshold is a hitch");
            Assert.AreEqual(200.0, atTheThreshold.HitchThresholdMs);

            Assert.AreEqual(4, Play(Timeline(hitchThresholdMs: 200.01), frames).Single().Hitches, "And one a hair under it is not");
        }

        [Test]
        public void TheHitchThresholdIsReadWhenTheWindowCloses()
        {
            double threshold = 60.0;
            ProtokitePlaytestPerformanceTimeline timeline = new ProtokitePlaytestPerformanceTimeline(() => threshold, () => null);
            Play(timeline, Enumerable.Repeat(0.25, 39));
            threshold = 250.0;
            ProtokitePlaytestPerformanceWindow window = Play(timeline, new[] { 0.25 }).Single();
            Assert.AreEqual(250.0, window.HitchThresholdMs);
            Assert.AreEqual(40, window.Hitches);
        }

        [Test]
        public void MemoryIsTheUseAtTheCloseAndTheMostSeenSinceMeasuringStarted()
        {
            long? used = 100 * Megabyte;
            ProtokitePlaytestPerformanceTimeline timeline = new ProtokitePlaytestPerformanceTimeline(() => 60.0, () => used);
            Play(timeline, Enumerable.Repeat(0.25, 10));
            used = 300 * Megabyte + 5;
            Play(timeline, new[] { 0.25 });
            used = 150 * Megabyte;
            ProtokitePlaytestPerformanceWindow first = Play(timeline, Enumerable.Repeat(0.25, 29)).Single();
            Assert.AreEqual(150, first.MemoryUsedMb);
            Assert.AreEqual(300, first.MemoryPeakMb, "A spike between two closes is still the peak");

            ProtokitePlaytestPerformanceWindow second = Play(timeline, Enumerable.Repeat(0.25, 40)).Single();
            Assert.AreEqual(300, second.MemoryPeakMb, "The peak is kept from one window to the next");
        }

        [Test]
        public void MemoryThePlatformDoesNotReportIsLeftOutRatherThanZero()
        {
            ProtokitePlaytestPerformanceWindow window = Play(Timeline(memoryUsedBytes: null), Enumerable.Repeat(0.25, 40)).Single();
            Assert.IsNull(window.MemoryUsedMb);
            Assert.IsNull(window.MemoryPeakMb);
        }

        [Test]
        public void AFrameWithNoTimeIsNotAFrame()
        {
            ProtokitePlaytestPerformanceTimeline timeline = Timeline();
            Assert.IsEmpty(Play(timeline, new[] { 0.0, -1.0, double.NaN }), "Not even a window of nothing");
            ProtokitePlaytestPerformanceWindow window = Play(timeline, Enumerable.Repeat(0.25, 40)).Single();
            Assert.AreEqual(40, window.Frames, "None of them counted as a frame");
        }

        [Test]
        public void ALeftOutFrameIsNotPlayAndOnlyOneIsLeftOut()
        {
            ProtokitePlaytestPerformanceTimeline timeline = Timeline();
            Play(timeline, Enumerable.Repeat(0.25, 20));
            timeline.LeaveOutNextFrame();
            Assert.IsEmpty(Play(timeline, new[] { 300.0 }), "Five minutes away closes no window");
            ProtokitePlaytestPerformanceWindow window = Play(timeline, Enumerable.Repeat(0.25, 20)).Single();
            Assert.AreEqual(10.0, window.Seconds);
            Assert.AreEqual(40, window.Frames);
            Assert.AreEqual(250.0, window.FrameTime99thPercentileMs, "The time away is in no percentile");
        }
    }
}
