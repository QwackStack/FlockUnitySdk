using System.Collections.Generic;
using Flock.Analytics;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // The repeat rule on its own: which occurrences are reported, what the summaries say, and the limit on different faults.
    public class FlockRepeatedExceptionCounterTests
    {
        private const string Log = FlockRepeatedExceptionCounter.SourceLog;

        private static FlockCapturedException Captured(string message, string stackTrace = "Game:Update ()", string source = Log)
            => new FlockCapturedException { Message = message, StackTrace = stackTrace, Source = source };

        private static bool Occur(FlockRepeatedExceptionCounter counter, FlockCapturedException captured, double atSeconds)
            => counter.ShouldReportNow(FlockRepeatedExceptionCounter.MakeSameFaultKey(captured.Source, captured.Message, captured.StackTrace), captured, atSeconds);

        private static List<FlockExceptionRepeatSummary> Finished(FlockRepeatedExceptionCounter counter, double atSeconds)
        {
            List<FlockExceptionRepeatSummary> summaries = new List<FlockExceptionRepeatSummary>();
            counter.CollectFinished(atSeconds, summaries);
            return summaries;
        }

        [Test]
        public void NumbersAndAddressesInTheMessageAreOneFault()
        {
            string stack = "Enemy:Attack ()\nGame:Update ()";
            Assert.AreEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Enemy 12 failed at 0x7ff3", stack),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Enemy 13 failed at 0x7ab9", stack));
            Assert.AreNotEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Enemy 12 failed", stack),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Enemy 12 fled", stack), "Different words are different faults");
        }

        [Test]
        public void OneMessageFromTwoCallSitesIsTwoFaults()
        {
            Assert.AreNotEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", "Enemy:Attack ()\nGame:Update ()"),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", "Tower:Fire ()\nGame:Update ()"));
        }

        [Test]
        public void OnlyTheFirstTwoFramesCount()
        {
            Assert.AreEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", "Enemy:Attack ()\nEnemy:Think ()\nLevelOne:Update ()"),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", "Enemy:Attack ()\nEnemy:Think ()\nLevelTwo:Update ()"));
        }

        [Test]
        public void UnitysLoggingFramesAreNotPartOfTheFault()
        {
            string logging = "UnityEngine.Debug:LogException (System.Exception)\nUnityEngine.Logger:LogException (System.Exception)\n";
            Assert.AreNotEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", logging + "Enemy:Attack ()"),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", logging + "Tower:Fire ()"),
                "Two logged exceptions from two places are two faults, though both stacks start in Unity's logging");
        }

        [Test]
        public void TheLogCallbacksAndTheCapturesOwnFramesAreNotPartOfTheFault()
        {
            string callback = "Flock.Analytics.FlockExceptionCapture:Keep (string,string,string)\n"
                + "Flock.Analytics.FlockExceptionCapture:HandleLog (string,string,UnityEngine.LogType)\n"
                + "UnityEngine.Application:CallLogCallback (string,string,UnityEngine.LogType,bool)\n"
                + "UnityEngine.DebugLogHandler:Internal_LogException (System.Exception,UnityEngine.Object)\n";
            Assert.AreEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", "Enemy:Attack ()\nEnemy:Think ()"),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Missing target", callback + "Enemy:Attack ()\nEnemy:Think ()"),
                "A stack taken inside the log callback starts at the game");
        }

        [Test]
        public void AStackTakenInsideTheLogCallbackIsSentFromTheGamesFirstFrame()
        {
            string callback = "UnityEngine.StackTraceUtility:ExtractStackTrace ()\n"
                + "Flock.Analytics.FlockExceptionCapture:Keep (string,string,string)\n"
                + "UnityEngine.Application:CallLogCallback (string,string,UnityEngine.LogType,bool)\n"
                + "UnityEngine.Logger:LogException (System.Exception,UnityEngine.Object)\n"
                + "UnityEngine.Debug:LogException (System.Exception)\n";
            Assert.AreEqual("Enemy:Attack ()\nEnemy:Think ()\n", FlockRepeatedExceptionCounter.FromTheFirstFrameOfTheGame(callback + "Enemy:Attack ()\nEnemy:Think ()\n"));
            Assert.AreEqual(string.Empty, FlockRepeatedExceptionCounter.FromTheFirstFrameOfTheGame(callback),
                "An exception Unity caught itself has no frame of the game's");
            Assert.AreEqual(string.Empty, FlockRepeatedExceptionCounter.FromTheFirstFrameOfTheGame(string.Empty));
        }

        [Test]
        public void TheSameMessageFromAnotherHookIsAnotherFault()
        {
            Assert.AreNotEqual(
                FlockRepeatedExceptionCounter.MakeSameFaultKey(Log, "Boom", "Game:Update ()"),
                FlockRepeatedExceptionCounter.MakeSameFaultKey(FlockRepeatedExceptionCounter.SourceUnobservedTask, "Boom", "Game:Update ()"));
        }

        [Test]
        public void AStormIsOneReportThenOneSummaryOfTheRest()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(60.0);
            FlockCapturedException first = Captured("InvalidOperationException: every frame");
            int reported = 0;
            for (int frame = 0; frame < 100; frame++)
            {
                if (Occur(counter, frame == 0 ? first : Captured("InvalidOperationException: every frame"), frame * 0.1))
                    reported++;
            }
            Assert.AreEqual(1, reported, "The first is reported at once, the rest counted");
            Assert.IsEmpty(Finished(counter, 59.9), "Nothing until the window closes");

            FlockExceptionRepeatSummary summary = Finished(counter, 60.0)[0];
            Assert.AreEqual(99, summary.Repeats, "The ones after the first");
            Assert.AreEqual(first.Message, summary.Message);
            Assert.AreEqual(first.StackTrace, summary.StackTrace);
            Assert.AreEqual(Log, summary.Source);
            Assert.IsEmpty(Finished(counter, 120.0), "A window is summarised once");
            Assert.IsTrue(Occur(counter, Captured("InvalidOperationException: every frame"), 121.0), "And the next occurrence opens a new one");
        }

        [Test]
        public void AWindowRunsFromItsFirstReportNotItsLatestRepeat()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(60.0);
            List<double> reportedAt = new List<double>();
            for (int second = 0; second <= 130; second++)
            {
                if (Occur(counter, Captured("NullReferenceException: every second"), second))
                    reportedAt.Add(second);
            }
            CollectionAssert.AreEqual(new[] { 0.0, 60.0, 120.0 }, reportedAt, "A fault that never stops is still reported once a minute");

            List<FlockExceptionRepeatSummary> summaries = Finished(counter, 130.0);
            Assert.AreEqual(2, summaries.Count, "The two windows it closed, each with its repeats");
            Assert.AreEqual(59, summaries[0].Repeats);
            Assert.AreEqual(59, summaries[1].Repeats);
        }

        [Test]
        public void AWindowOfZeroReportsEveryOccurrence()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(0.0);
            int reported = 0;
            for (int frame = 0; frame < 5; frame++)
            {
                if (Occur(counter, Captured("Boom"), frame * 0.1))
                    reported++;
            }
            Assert.AreEqual(5, reported);
            Assert.IsEmpty(Finished(counter, 1000.0));
        }

        [Test]
        public void ALaunchReportsAtMostTheLimitOfDifferentFaultsAndCountsTheRest()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(60.0);
            int reported = 0;
            for (int fault = 0; fault < FlockRepeatedExceptionCounter.MostDifferentFaultsPerLaunch + 1; fault++)
            {
                if (Occur(counter, Captured("Fault", $"Script{fault}:Update ()"), 1.0))
                    reported++;
            }
            Assert.AreEqual(100, FlockRepeatedExceptionCounter.MostDifferentFaultsPerLaunch);
            Assert.AreEqual(100, reported);
            Assert.AreEqual(1, counter.HeldBack, "The 101st is counted, not reported");
            Assert.AreEqual(1, counter.TakeHeldBack());
            Assert.AreEqual(0, counter.HeldBack, "Taken, and started again");
        }

        [Test]
        public void AFaultReportedBeforeIsNotStoppedByTheLimit()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(60.0, mostDifferentFaults: 2);
            Assert.IsTrue(Occur(counter, Captured("Always", "Loop:Update ()"), 0.0));
            // The same fault in window after window takes no more room under the limit than its first report did.
            for (int window = 1; window <= 150; window++)
                Assert.IsTrue(Occur(counter, Captured("Always", "Loop:Update ()"), window * 61.0), $"Window {window}");

            Assert.IsTrue(Occur(counter, Captured("Second", "Other:Update ()"), 9200.0), "Room is left for a second fault");
            Assert.IsFalse(Occur(counter, Captured("Third", "Third:Update ()"), 9200.0), "And the limit still holds for a third");
            Assert.IsTrue(Occur(counter, Captured("Always", "Loop:Update ()"), 9300.0), "While a fault already reported goes on being reported");
        }

        [Test]
        public void ExceptionsLostBeforeTheCounterAreHeldBackToo()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(60.0);
            counter.CountHeldBack(44);
            counter.CountHeldBack(0);
            Assert.AreEqual(44, counter.HeldBack);
        }

        [Test]
        public void CollectAllHandsBackEveryWindowWithRepeatsHoweverYoung()
        {
            FlockRepeatedExceptionCounter counter = new FlockRepeatedExceptionCounter(60.0);
            Occur(counter, Captured("Repeated"), 0.0);
            Occur(counter, Captured("Repeated"), 1.0);
            Occur(counter, Captured("Repeated"), 2.0);
            Occur(counter, Captured("Once", "Other:Update ()"), 2.0);

            List<FlockExceptionRepeatSummary> summaries = new List<FlockExceptionRepeatSummary>();
            counter.CollectAll(summaries);
            Assert.AreEqual(1, summaries.Count, "A fault seen once has nothing more to say");
            Assert.AreEqual(2, summaries[0].Repeats);
            Assert.IsTrue(Occur(counter, Captured("Repeated"), 3.0), "Every window was closed");
        }
    }
}
