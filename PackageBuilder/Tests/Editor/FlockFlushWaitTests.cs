using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Http;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>An awaited flush that finds another flush sending waits for it and then sends what is left; a flush trigger leaves the queue to it.</summary>
    public class FlockFlushWaitTests
    {
        private string _runFolder;
        private string _folder;
        private FlockTestClient _h;

        public sealed class QueuedThing
        {
            public string Name;
        }

        // A sender that holds its first batch until the test lets it go (or fails it), and records every batch.
        private sealed class HeldSender
        {
            public readonly List<string[]> Batches = new List<string[]>();
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>();

            public Task Send(IReadOnlyList<QueuedThing> batch, CancellationToken token)
            {
                lock (Batches)
                    Batches.Add(batch.Select(thing => thing.Name).ToArray());
                return Batches.Count == 1 ? Release.Task : Task.CompletedTask;
            }
        }

        [SetUp]
        public void SetUp()
        {
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            _folder = Path.Combine(Path.GetTempPath(), "flock_flush_wait_" + Guid.NewGuid().ToString("N"));
            FlockAnalyticsLaunches.FolderForTesting = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private FlockEventCache<QueuedThing> Queue() => new FlockEventCache<QueuedThing>(_folder, "events", 100, 10, null);

        // Waits real time, a frame at a time, so a check that something did not happen is not decided inside one timer tick.
        private static IEnumerator Frames(float seconds)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
                yield return null;
        }

        // The running flush's send fails, as a dropped connection would, so what is left is the awaited flush's to send.
        [UnityTest]
        public IEnumerator AnAwaitedFlushWaitsForTheRunningFlushThenSendsWhatItLeft()
        {
            FlockEventCache<QueuedThing> queue = Queue();
            HeldSender sender = new HeldSender();
            queue.Enqueue(new QueuedThing { Name = "first" });
            Task running = queue.FlushAsync(sender.Send, CancellationToken.None);
            queue.Enqueue(new QueuedThing { Name = "second" });

            Task awaited = queue.FlushAsync(sender.Send, CancellationToken.None, true);
            yield return Frames(0.3f);
            Assert.AreEqual(1, sender.Batches.Count, "Precondition: the running flush holds its batch");
            Assert.IsFalse(awaited.IsCompleted, "Waits while another flush sends");

            sender.Release.SetException(new TimeoutException("the connection dropped"));
            yield return FlockTestWait.Until(() => awaited.IsCompleted && running.IsCompleted, "both flushes ended");
            Assert.IsTrue(awaited.Status == TaskStatus.RanToCompletion, awaited.Status.ToString());
            Assert.AreEqual(2, sender.Batches.Count, "The awaited flush sent once the running one gave up");
            CollectionAssert.AreEqual(new[] { "first", "second" }, sender.Batches[1]);
            Assert.AreEqual(0, queue.PendingCount);
        }

        [UnityTest]
        public IEnumerator AFlushTriggerDuringARunningFlushLeavesTheQueueToIt()
        {
            FlockEventCache<QueuedThing> queue = Queue();
            HeldSender sender = new HeldSender();
            queue.Enqueue(new QueuedThing { Name = "first" });
            Task running = queue.FlushAsync(sender.Send, CancellationToken.None);
            queue.Enqueue(new QueuedThing { Name = "second" });

            Task trigger = queue.FlushAsync(sender.Send, CancellationToken.None);
            Assert.IsTrue(trigger.IsCompleted, "A trigger does not wait");
            Assert.AreEqual(1, sender.Batches.Count, "And sends nothing of its own");

            sender.Release.SetResult(true);
            yield return FlockTestWait.Until(() => running.IsCompleted, "the running flush ended");
            Assert.AreEqual(2, sender.Batches.Count, "The running flush sent what arrived meanwhile");
        }

        [UnityTest]
        public IEnumerator AWaitingFlushStopsWhenCancelled()
        {
            FlockEventCache<QueuedThing> queue = Queue();
            HeldSender sender = new HeldSender();
            queue.Enqueue(new QueuedThing { Name = "first" });
            Task running = queue.FlushAsync(sender.Send, CancellationToken.None);

            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Task awaited = queue.FlushAsync(sender.Send, cancel.Token, true);
                yield return Frames(0.2f);
                Assert.IsFalse(awaited.IsCompleted, "Precondition: waiting for the running flush");
                cancel.Cancel();
                yield return FlockTestWait.Until(() => awaited.IsCompleted, "the cancelled wait ended", 5f);
                Assert.IsTrue(awaited.IsCanceled, awaited.Status.ToString());
                Assert.IsFalse(running.IsCompleted, "Before the running flush let go");
            }
            sender.Release.SetResult(true);
            yield return FlockTestWait.Until(() => running.IsCompleted, "the running flush ended");
        }

        // Through the SDK a game uses: the public FlushAsync waits for a flush already sending.
        [UnityTest]
        public IEnumerator FlushAsyncWaitsForAFlushAlreadySendingAndSendsTheEventsAfterIt()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Ok("{\"ok\":true}"));
            _h = FlockTestClient.Create(transport);
            _h.SetReachable(true);
            _h.LoginAs("player-a");
            Assert.IsTrue(_h.Client.Analytics.TrackEvent("first"));
            transport.GateNext(FlockEndpoints.AnalyticsEvents);
            Task running = _h.Client.Analytics.FlushAsync();
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.AnalyticsEvents) == 1, "the first flush is sending");

            Assert.IsTrue(_h.Client.Analytics.TrackEvent("second"));
            Task awaited = _h.Client.Analytics.FlushAsync();
            yield return Frames(0.3f);
            Assert.IsFalse(awaited.IsCompleted, "Waits while another flush sends");

            transport.ReleaseGate();
            yield return FlockTestWait.Until(() => awaited.IsCompleted && running.IsCompleted, "both flushes ended");
            List<string[]> batches = transport.AllTo(FlockEndpoints.AnalyticsEvents)
                .Select(request => ((JArray)JObject.Parse(request.JsonBody)["events"]).Select(e => (string)e["event_name"]).ToArray())
                .ToList();
            Assert.AreEqual(2, batches.Count);
            CollectionAssert.AreEqual(new[] { "first" }, batches[0]);
            CollectionAssert.AreEqual(new[] { "second" }, batches[1], "Sent before FlushAsync ended");
        }
    }
}
