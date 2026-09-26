using System.Collections.Generic;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestFrameScheduleTests
    {
        // Each captured frame as (which game frame it was, when it is shown).
        private static List<KeyValuePair<int, long>> Captured(ProtokitePlaytestFrameSchedule schedule, IEnumerable<double> frameSeconds)
        {
            List<KeyValuePair<int, long>> captured = new List<KeyValuePair<int, long>>();
            int index = 0;
            foreach (double seconds in frameSeconds)
            {
                if (schedule.AddFrame(seconds, out long timestampMs) == ProtokitePlaytestFrameDecision.Capture)
                    captured.Add(new KeyValuePair<int, long>(index, timestampMs));
                index++;
            }
            return captured;
        }

        private static IEnumerable<double> Repeated(double seconds, int count)
        {
            for (int i = 0; i < count; i++)
                yield return seconds;
        }

        [Test]
        public void CapturesAtTheFrameRate()
        {
            List<KeyValuePair<int, long>> captured = Captured(new ProtokitePlaytestFrameSchedule(30, 3600), Repeated(1.0 / 60.0, 60));
            Assert.AreEqual(30, captured.Count, "A second of a 60 fps game gives 30 frames");
            Assert.AreEqual(0, captured[0].Value, "The first is shown at the start");
            Assert.AreEqual(2, captured[1].Key, "The second is the third frame drawn");
            Assert.AreEqual(33, captured[1].Value, "Shown 33 ms in");

            Assert.AreEqual(15, Captured(new ProtokitePlaytestFrameSchedule(15, 3600), Repeated(1.0 / 60.0, 60)).Count, "15 a second takes every fourth frame");
            Assert.AreEqual(20, Captured(new ProtokitePlaytestFrameSchedule(30, 3600), Repeated(0.05, 20)).Count, "A game slower than the capture rate has every frame captured");
        }

        [Test]
        public void WobblyFrameTimesStillCaptureEveryOtherFrame()
        {
            // A 60 fps game whose frame times wobble either side of 16.667 ms: every other frame is still the nearest to each capture time.
            double[] pattern = { 0.016267, 0.016667, 0.017067, 0.016667 };
            List<double> frames = new List<double>();
            for (int i = 0; i < 120; i++)
                frames.Add(pattern[i % 4]);
            List<KeyValuePair<int, long>> captured = Captured(new ProtokitePlaytestFrameSchedule(30, 3600), frames);
            Assert.AreEqual(60, captured.Count, "Two seconds give 60 frames");
            for (int i = 1; i < captured.Count; i++)
                Assert.AreEqual(2, captured[i].Key - captured[i - 1].Key, $"Frame {captured[i].Key} comes two frames after the one before");
        }

        [Test]
        public void LeavesOutTheFrameAfterTheBackground()
        {
            ProtokitePlaytestFrameSchedule schedule = new ProtokitePlaytestFrameSchedule(30, 3600);
            Captured(schedule, Repeated(0.1, 10));
            Assert.AreEqual(1.0, schedule.RecordedSeconds, 1e-9, "A second recorded");

            schedule.LeaveOutNextFrame();
            Assert.AreEqual(ProtokitePlaytestFrameDecision.Skip, schedule.AddFrame(300.0, out _), "The frame carrying the time away is not captured");
            Assert.AreEqual(1.0, schedule.RecordedSeconds, 1e-9, "And its time is not recorded");
            Assert.AreEqual(ProtokitePlaytestFrameDecision.Capture, schedule.AddFrame(0.1, out long timestampMs), "The frame after it is");
            Assert.AreEqual(1000, timestampMs, "Shown straight after the time before it");
        }

        [Test]
        public void ATimeThatIsNotAFrameIsSkippedAndAddsNothing()
        {
            ProtokitePlaytestFrameSchedule schedule = new ProtokitePlaytestFrameSchedule(30, 3600);
            Captured(schedule, Repeated(0.1, 3));
            foreach (double notAFrame in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                Assert.AreEqual(ProtokitePlaytestFrameDecision.Skip, schedule.AddFrame(notAFrame, out long timestampMs), notAFrame + " is not a frame");
                Assert.AreEqual(-1, timestampMs, "No time is handed back for " + notAFrame);
            }
            Assert.AreEqual(0.3, schedule.RecordedSeconds, 1e-9, "None of them adds time");
            Assert.AreEqual(ProtokitePlaytestFrameDecision.Capture, schedule.AddFrame(0.1, out long next), "Control: a real frame after them");
            Assert.AreEqual(300, next);
        }

        [Test]
        public void AHugeFrameTimeIsDecidedAtOnce()
        {
            foreach (double huge in new[] { 1e9, 1e300 })
            {
                ProtokitePlaytestFrameSchedule schedule = new ProtokitePlaytestFrameSchedule(30, double.MaxValue);
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                Assert.AreEqual(ProtokitePlaytestFrameDecision.Capture, schedule.AddFrame(huge, out _), huge + " s");
                schedule.AddFrame(0.01, out _);
                Assert.Less(clock.ElapsedMilliseconds, 1000, huge + " s is decided without stepping through every capture time in it");
            }
        }

        [Test]
        public void ALengthLimitThatIsNotANumberRecordsNothing()
        {
            ProtokitePlaytestFrameSchedule schedule = new ProtokitePlaytestFrameSchedule(30, double.NaN);
            Assert.AreEqual(ProtokitePlaytestFrameDecision.ReachedLengthLimit, schedule.AddFrame(0.1, out _));
        }

        [Test]
        public void StopsAtTheLengthLimit()
        {
            ProtokitePlaytestFrameSchedule schedule = new ProtokitePlaytestFrameSchedule(30, 1.0);
            int captures = 0;
            long lastTimestampMs = -1;
            int limitReachedAt = -1;
            for (int i = 0; i < 100 && limitReachedAt < 0; i++)
            {
                ProtokitePlaytestFrameDecision decision = schedule.AddFrame(0.05, out long timestampMs);
                if (decision == ProtokitePlaytestFrameDecision.Capture)
                {
                    captures++;
                    lastTimestampMs = timestampMs;
                }
                else if (decision == ProtokitePlaytestFrameDecision.ReachedLengthLimit)
                {
                    limitReachedAt = i;
                }
            }
            Assert.AreEqual(20, limitReachedAt, "The limit is reached by the frame that starts at one second");
            Assert.AreEqual(20, captures, "Twenty frames fit in a second");
            Assert.Less(lastTimestampMs, 1000, "No frame is shown at or after the limit");
            Assert.AreEqual(ProtokitePlaytestFrameDecision.ReachedLengthLimit, schedule.AddFrame(0.05, out _), "And it stays reached");
        }

        [Test]
        public void NeverRepeatsATime()
        {
            // A tiny frame between two long ones at 30 a second: the tiny one reaches a capture time, and so does the long one
            // after it, which starts 0.2 ms later. Rounded to milliseconds both would be shown at 40 ms.
            ProtokitePlaytestFrameSchedule schedule = new ProtokitePlaytestFrameSchedule(30, 3600);
            Assert.AreEqual(ProtokitePlaytestFrameDecision.Capture, schedule.AddFrame(0.0402, out long timestampMs));
            Assert.AreEqual(0, timestampMs);
            Assert.AreEqual(ProtokitePlaytestFrameDecision.Capture, schedule.AddFrame(0.0002, out timestampMs), "The tiny frame reaches the next capture time");
            Assert.AreEqual(40, timestampMs);
            Assert.AreEqual(ProtokitePlaytestFrameDecision.Skip, schedule.AddFrame(0.060, out timestampMs), "The long frame after it, also at 40 ms once rounded, is not captured");
            Assert.AreEqual(-1, timestampMs, "And no time is handed back for it");
        }
    }
}
