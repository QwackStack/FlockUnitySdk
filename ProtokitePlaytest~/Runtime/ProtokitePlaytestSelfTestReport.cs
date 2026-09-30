using System.Collections.Generic;
using System.Threading.Tasks;

namespace Protokite.Playtest
{
    /// <summary>How one step of the playtest self-test came out.</summary>
    public enum ProtokitePlaytestSelfTestOutcome
    {
        /// <summary>What the step checks held.</summary>
        Passed,
        /// <summary>What the step checks did not hold; its detail says what happened instead.</summary>
        Failed,
        /// <summary>The step could not be run here; its detail says why and what would let it run.</summary>
        Skipped
    }

    /// <summary>One step of the playtest self-test: what it checked, how it came out, and why.</summary>
    public sealed class ProtokitePlaytestSelfTestStep
    {
        /// <summary>What the step checks, in the words the self-test logs.</summary>
        public string Name { get; }

        /// <summary>How the step came out.</summary>
        public ProtokitePlaytestSelfTestOutcome Outcome { get; }

        /// <summary>What was found: the ids involved when it passed, what happened instead when it failed, why it could not run when skipped.</summary>
        public string Detail { get; }

        internal ProtokitePlaytestSelfTestStep(string name, ProtokitePlaytestSelfTestOutcome outcome, string detail)
        {
            Name = name;
            Outcome = outcome;
            Detail = detail ?? "";
        }
    }

    /// <summary>What a run of the playtest self-test found.</summary>
    public sealed class ProtokitePlaytestSelfTestReport
    {
        private readonly List<ProtokitePlaytestSelfTestStep> _steps = new List<ProtokitePlaytestSelfTestStep>();

        /// <summary>This run's id, carried by every exception, event and form it sent, to find them in Flock and Protokite.</summary>
        public string RunId { get; }

        /// <summary>Why the self-test did not run at all, or null when it ran.</summary>
        public string NotRunBecause { get; }

        /// <summary>Every step, in the order run.</summary>
        public IReadOnlyList<ProtokitePlaytestSelfTestStep> Steps => _steps;

        /// <summary>How many steps passed.</summary>
        public int Passed => Count(ProtokitePlaytestSelfTestOutcome.Passed);

        /// <summary>How many steps failed.</summary>
        public int Failed => Count(ProtokitePlaytestSelfTestOutcome.Failed);

        /// <summary>How many steps were skipped.</summary>
        public int Skipped => Count(ProtokitePlaytestSelfTestOutcome.Skipped);

        /// <summary>True when it ran, nothing failed and at least one step passed.</summary>
        public bool AllRunStepsPassed => NotRunBecause == null && Failed == 0 && Passed > 0;

        internal ProtokitePlaytestSelfTestReport(string runId, string notRunBecause)
        {
            RunId = runId ?? "";
            NotRunBecause = notRunBecause;
        }

        internal void Add(ProtokitePlaytestSelfTestStep step) => _steps.Add(step);

        private int Count(ProtokitePlaytestSelfTestOutcome outcome)
        {
            int count = 0;
            foreach (ProtokitePlaytestSelfTestStep step in _steps)
            {
                if (step.Outcome == outcome)
                    count++;
            }
            return count;
        }
    }

    /// <summary>Checks this build's playtest end to end against the live Protokite, each check paired with a request Protokite must refuse; editor and development builds only.</summary>
    public static class ProtokitePlaytestSelfTest
    {
        /// <summary>Whether a self-test is running now.</summary>
        public static bool IsRunning => ProtokitePlaytest.SelfTestIsRunning;

        /// <summary>Runs and logs every step, once a player is signed in; it uploads this launch's recording, so run it in a launch of its own. A closed playtest's Game Version ID checks that one takes no session. Main thread only.</summary>
        public static Task<ProtokitePlaytestSelfTestReport> RunAsync(string closedPlaytestGameVersionId = null)
            => ProtokitePlaytest.RunSelfTestAsync(closedPlaytestGameVersionId);
    }
}
