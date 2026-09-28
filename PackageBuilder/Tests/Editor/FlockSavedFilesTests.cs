using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    /// <summary>Every change to the SDK's saved files asks a WebGL player to copy them to browser storage, after the change is made.</summary>
    public class FlockSavedFilesTests
    {
        private string _folder;
        private readonly List<string> _seenAtEachCopy = new List<string>();

        public sealed class QueuedThing
        {
            public string Name;
        }

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "flock_saved_files_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _seenAtEachCopy.Clear();
            // Only this test's own changes: another test's work still finishing on a background thread asks for copies too.
            FlockSavedFiles.CopyToBrowserStorageForTesting = changedPath =>
            {
                if (!changedPath.StartsWith(_folder, StringComparison.Ordinal))
                    return;
                lock (_seenAtEachCopy)
                    _seenAtEachCopy.Add(WhatIsOnDisk());
            };
        }

        [TearDown]
        public void TearDown()
        {
            FlockSavedFiles.CopyToBrowserStorageForTesting = null;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        // The files and folders under the test's folder, as one sorted line, so each copy records what it would have copied.
        private string WhatIsOnDisk() =>
            string.Join(",", Directory.GetFileSystemEntries(_folder, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(_folder.Length + 1).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal));

        private string In(string name) => Path.Combine(_folder, name);

        [Test]
        public void EveryChangeAsksForACopyOnceItIsMade()
        {
            FlockSavedFiles.CreateFolder(In("queue"));
            FlockSavedFiles.WriteText(In("queue/a.txt"), "a");
            FlockSavedFiles.WriteBytes(In("queue/b.bin"), new byte[] { 1 });
            FlockSavedFiles.SetLastWriteTime(In("queue/b.bin"), DateTime.UtcNow);
            FlockSavedFiles.Move(In("queue/a.txt"), In("queue/c.txt"));
            FlockSavedFiles.WriteText(In("queue/d.txt"), "d");
            FlockSavedFiles.Replace(In("queue/d.txt"), In("queue/c.txt"));
            FlockSavedFiles.Delete(In("queue/b.bin"));
            using (FlockSavedFiles.OpenLock(In("queue/in-use.lock"), FileMode.OpenOrCreate))
            {
            }
            FlockSavedFiles.Delete(In("queue/in-use.lock"));
            FlockSavedFiles.Delete(In("queue/c.txt"));
            FlockSavedFiles.DeleteFolder(In("queue"), false);

            CollectionAssert.AreEqual(new[]
            {
                "queue",
                "queue,queue/a.txt",
                "queue,queue/a.txt,queue/b.bin",
                "queue,queue/a.txt,queue/b.bin",
                "queue,queue/b.bin,queue/c.txt",
                "queue,queue/b.bin,queue/c.txt,queue/d.txt",
                "queue,queue/b.bin,queue/c.txt",
                "queue,queue/c.txt",
                "queue,queue/c.txt,queue/in-use.lock",
                "queue,queue/c.txt",
                "queue",
                ""
            }, _seenAtEachCopy, "One copy per change, each seeing the change made");
        }

        [Test]
        public void AChangeThatFailsStillAsksForACopy()
        {
            Assert.Throws<FileNotFoundException>(() => FlockSavedFiles.Move(In("missing.txt"), In("moved.txt")));
            Assert.AreEqual(1, _seenAtEachCopy.Count, "A change that fails part-way may have changed something");
        }

        // The case that re-sent events on every visit: a queued event's file was deleted after its send, and nothing copied that.
        [Test]
        public void AnEventSentFromTheQueueIsGoneFromTheLastCopy()
        {
            FlockEventCache<QueuedThing> queue = new FlockEventCache<QueuedThing>(_folder, "events", 100, 10, null);
            Assert.IsNotNull(queue.Enqueue(new QueuedThing { Name = "level_complete" }));
            StringAssert.Contains(".evt", _seenAtEachCopy.Last(), "Precondition: the queued event was copied once written");

            List<string> sent = new List<string>();
            queue.FlushAsync((batch, token) =>
            {
                sent.AddRange(batch.Select(thing => thing.Name));
                return Task.CompletedTask;
            }, CancellationToken.None).GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { "level_complete" }, sent, "Precondition: sent");
            Assert.AreEqual(0, queue.PendingCount);
            StringAssert.DoesNotContain(".evt", _seenAtEachCopy.Last(), "The last copy has no queued event left to send again");
        }

        // Static File and Directory changes, streams that write, and FileInfo or DirectoryInfo changes (which take no path).
        private static readonly Regex FileChange = new Regex(
            @"(?<![\w.])(File\.(Delete|Move|Replace|WriteAll\w*|AppendAll\w*|AppendText|Copy|Create|CreateText|Open|OpenWrite|Set\w+)\(|Directory\.(Delete|Move|CreateDirectory|Set\w+)\()"
            + @"|new FileStream\(|new StreamWriter\(|\.(MoveTo|CopyTo|CreateSubdirectory)\(|\.Delete\((true|false)?\)");

        [Test]
        public void NothingInTheRuntimeChangesASavedFileExceptThroughFlockSavedFiles()
        {
            Assert.GreaterOrEqual(FlockRuntimeSource.LinesMatchingIn("FlockSavedFiles.cs", FileChange).Count, 9, "Control: the scan finds the changes FlockSavedFiles makes");
            Assert.IsTrue(FileChange.IsMatch("new FileInfo(path).Delete();") && FileChange.IsMatch("folder.MoveTo(other);"), "Control: FileInfo changes are found");
            Assert.IsFalse(FileChange.IsMatch("FlockSavedFiles.Delete(path);") || FileChange.IsMatch("new DirectoryInfo(dir).GetFiles()"), "Control: reads and FlockSavedFiles are not");
            List<string> found = FlockRuntimeSource.LinesMatching(FileChange, "FlockSavedFiles.cs");
            Assert.IsEmpty(found, "Change saved files through FlockSavedFiles, so a WebGL player copies them to browser storage:\n" + string.Join("\n", found));
        }
    }
}
