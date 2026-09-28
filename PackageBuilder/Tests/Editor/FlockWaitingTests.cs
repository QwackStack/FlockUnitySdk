using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>How the SDK waits: the frame-by-frame wait WebGL uses, and a guard against waits WebGL can never finish.</summary>
    public class FlockWaitingTests
    {
        private static IEnumerator Await(Task task, float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < until)
                yield return null;
            Assert.IsTrue(task.IsCompleted, $"Ended within {seconds} s");
        }

        [UnityTest]
        public IEnumerator WaitingInFramesLastsAtLeastTheTimeAsked()
        {
            Stopwatch clock = Stopwatch.StartNew();
            Task wait = FlockWaiting.DelayInFramesAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            yield return Await(wait, 5);
            Assert.IsTrue(wait.Status == TaskStatus.RanToCompletion, $"Finished, not {wait.Status}");
            Assert.GreaterOrEqual(clock.ElapsedMilliseconds, 1000, "Waited the whole time");
        }

        [UnityTest]
        public IEnumerator WaitingInFramesStopsWhenCancelled()
        {
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Task wait = FlockWaiting.DelayInFramesAsync(TimeSpan.FromSeconds(30), cancel.Token);
                yield return null;
                cancel.Cancel();
                yield return Await(wait, 5);
                Assert.IsTrue(wait.IsCanceled, $"Cancelled, not {wait.Status}");
            }

            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.IsTrue(FlockWaiting.DelayInFramesAsync(TimeSpan.Zero, cancelled.Token).IsCanceled, "An already cancelled wait ends at once");
            }
        }

        // WebGL has no thread pool and no timers (measured in a WebGL player): a continuation or delay that needs them never finishes.
        [Test]
        public void NothingInTheRuntimeWaitsOnAThreadOrATimerWebGLDoesNotHave()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FlockClient).Assembly);
            if (package == null)
                Assert.Ignore("The SDK is not installed as a package here, so its source cannot be read.");
            string runtime = Path.Combine(package.resolvedPath, "Runtime");
            Regex unsafeWait = new Regex(@"ConfigureAwait\(\s*(continueOnCapturedContext\s*:\s*)?false\s*\)|Task\.Delay\(|Task\.Run\(|ThreadPool\.|Threading\.Timer|new Timer\(|new Thread\(");
            List<string> found = new List<string>();
            foreach (string file in Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "FlockWaiting.cs")
                    continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].TrimStart().StartsWith("//") && unsafeWait.IsMatch(lines[i]))
                        found.Add($"{file.Substring(runtime.Length + 1)}:{i + 1}: {lines[i].Trim()}");
                }
            }
            Assert.IsEmpty(found, "Wait through FlockWaiting (ResumeOnCallersThread, DelayAsync) instead:\n" + string.Join("\n", found));
        }
    }
}
