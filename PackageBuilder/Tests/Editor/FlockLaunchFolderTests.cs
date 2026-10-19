using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flock.Analytics;
using Flock.Logging;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // Each launch keeps its crash marker, live-session record and event queues in a folder of its own, locked while it runs.
    // A launch "ended" in these tests by letting go of its lock, which is what the operating system does when a game is killed;
    // the real-process run (two players, one killed) is in the working docs.
    public class FlockLaunchFolderTests
    {
        private const string ConsentGrantedKey = "flock_analytics_consent";
        private const string ConsentSetKey = "flock_analytics_consent_set";

        private string _root;
        private string _runFolder;
        private readonly List<IDisposable> _held = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "flock_launches_" + Guid.NewGuid().ToString("N"));
            // Each test's SDK gets a folder of its own: in the run's shared one, a test SDK's queued events are taken over
            // by the next test's SDK, like any launch's.
            _runFolder = FlockAnalyticsLaunches.FolderForTesting;
            FlockAnalyticsLaunches.FolderForTesting = Path.Combine(_root, "sdk");
            PlayerPrefs.DeleteKey(ConsentGrantedKey);
            PlayerPrefs.DeleteKey(ConsentSetKey);
        }

        [TearDown]
        public void TearDown()
        {
            FlockTemporaryFiles.SetBeforeNextMoveForTesting(null);
            FlockLaunchFolder.BeforeNextFolderRemovalForTesting = null;
            foreach (IDisposable held in _held)
                held.Dispose();
            _held.Clear();
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            PlayerPrefs.DeleteKey(ConsentGrantedKey);
            PlayerPrefs.DeleteKey(ConsentSetKey);
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        private string LaunchesFolder => FlockTestGame.LaunchesFolderIn(_root);

        private FlockAnalyticsLaunches StartLaunch(FlockEarlierBuildFiles earlier = null, string gameKey = null)
        {
            FlockAnalyticsLaunches launches = FlockAnalyticsLaunches.Start(_root, gameKey ?? FlockTestGame.Key, earlier);
            _held.Add(launches);
            return launches;
        }

        // A launch that ended the way a killed game does: its lock let go, every file left where it was.
        private string PlantEndedLaunch(string markerJson = null, string sessionJson = null, params string[] queuedEntries)
        {
            FlockLaunchFolder folder = FlockLaunchFolder.Create(LaunchesFolder);
            Assert.IsNotNull(folder);
            if (markerJson != null)
                File.WriteAllText(Path.Combine(folder.Path, FlockAnalyticsLaunches.TerminationMarkerFileName), markerJson);
            if (sessionJson != null)
                File.WriteAllText(Path.Combine(folder.Path, FlockAnalyticsLaunches.SessionStateFileName), sessionJson);
            foreach (string entry in queuedEntries)
                PlantQueued(Path.Combine(folder.Path, FlockAnalyticsLaunches.AnalyticsEventsQueueName), entry);
            folder.Dispose();
            return folder.Path;
        }

        // Named by the test, not by the cache: two caches can mint one name, and the planted entry must survive to be moved.
        private static void PlantQueued(string queueFolder, string name)
        {
            Directory.CreateDirectory(queueFolder);
            File.WriteAllText(Path.Combine(queueFolder, name + FlockEventCacheNames.Extension), "{\"value\":\"" + name + "\"}");
        }

        private static List<string> QueuedNames(string queueFolder)
        {
            return FlockAnalyticsLaunches.QueuedEntries(queueFolder).Select(Path.GetFileNameWithoutExtension).ToList();
        }

        private static string SessionJson(string sessionId) =>
            "{\"session_id\":\"" + sessionId + "\",\"is_active\":true,\"start_time_utc\":\"2026-09-27T10:00:00Z\",\"last_heartbeat_utc\":\"2026-09-27T10:05:00Z\"}";

        private static string MarkerJson(string sessionId) =>
            "{\"session_id\":\"" + sessionId + "\",\"last_state\":\"foreground\",\"last_alive_utc\":\"2026-09-27T10:05:00Z\",\"exception_count\":2}";

        // ---- The name ----

        // Launch folders are taken over oldest first by name, so a machine set to another calendar (Thai, 2569) still names them by the Gregorian date.
        [Test]
        public void ALaunchFolderMadeOnAMachineWithAnotherCalendar_IsNamedForTheGregorianDate()
        {
            CultureInfo anotherCalendar;
            try
            {
                anotherCalendar = (CultureInfo)new CultureInfo("th-TH").Clone();
                anotherCalendar.DateTimeFormat.Calendar = new ThaiBuddhistCalendar();
            }
            catch (Exception ex) when (ex is CultureNotFoundException || ex is ArgumentException)
            {
                Assert.Ignore("This runtime offers no culture with another calendar to stand in for one: " + ex.Message);
                return;
            }
            CultureInfo before = CultureInfo.CurrentCulture;
            string dayBefore = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            FlockLaunchFolder folder;
            CultureInfo.CurrentCulture = anotherCalendar;
            try
            {
                folder = FlockLaunchFolder.Create(LaunchesFolder);
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
            string dayAfter = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            Assert.IsNotNull(folder);
            folder.Dispose();

            Assert.AreNotEqual(dayBefore, DateTime.UtcNow.ToString("yyyyMMdd", anotherCalendar), "Precondition: the stand-in culture writes another year");
            string name = Path.GetFileName(folder.Path);
            Assert.IsTrue(name.StartsWith(dayBefore + "-", StringComparison.Ordinal) || name.StartsWith(dayAfter + "-", StringComparison.Ordinal),
                $"{name} starts with the Gregorian date {dayBefore}");
        }

        // ---- The lock ----

        [Test]
        public void ClaimEnded_LaunchStillHoldingItsLock_IsRefused_EvenInThisProcess()
        {
            FlockLaunchFolder live = FlockLaunchFolder.Create(LaunchesFolder);
            _held.Add(live);

            Assert.IsNull(FlockLaunchFolder.ClaimEnded(live.Path), "A running launch's folder is never taken for ended.");

            live.Dispose();
            FlockLaunchFolder claimed = FlockLaunchFolder.ClaimEnded(live.Path);
            _held.Add(claimed);
            Assert.IsNotNull(claimed, "Once its lock is let go, the folder can be taken over.");
        }

        [Test]
        public void ClaimEnded_FolderWithNoLock_IsRefused()
        {
            string folder = Path.Combine(LaunchesFolder, "not-a-launch");
            Directory.CreateDirectory(folder);
            Assert.IsNull(FlockLaunchFolder.ClaimEnded(folder));
        }

        // An open file stops a delete or a move on Windows only; macOS and Linux let both go ahead.
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void DeleteEverything_FileThatCannotBeDeleted_KeepsTheLockSoALaterLaunchFindsItAgain()
        {
            string ended = PlantEndedLaunch(markerJson: MarkerJson("s1"));
            FlockLaunchFolder claimed = FlockLaunchFolder.ClaimEnded(ended);
            string marker = Path.Combine(ended, FlockAnalyticsLaunches.TerminationMarkerFileName);

            using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.IsFalse(claimed.DeleteEverything());

            Assert.IsTrue(File.Exists(Path.Combine(ended, FlockLaunchFolder.LockFileName)));
            FlockLaunchFolder again = FlockLaunchFolder.ClaimEnded(ended);
            Assert.IsNotNull(again, "The folder was let go, so a later launch can take it over and try again.");
            Assert.IsTrue(again.DeleteEverything());
            Assert.IsFalse(Directory.Exists(ended));
        }

        // The folder can still fail to go once its lock has (here a file lands in it meanwhile): the lock comes back, or no later
        // launch would ever find what is left.
        [Test]
        public void DeleteEverything_AFolderThatCannotBeRemoved_KeepsItsLock()
        {
            string ended = PlantEndedLaunch(markerJson: MarkerJson("s1"));
            FlockLaunchFolder claimed = FlockLaunchFolder.ClaimEnded(ended);
            Assert.IsNotNull(claimed);
            FlockLaunchFolder.BeforeNextFolderRemovalForTesting = () => File.WriteAllText(Path.Combine(ended, "arrived-meanwhile.txt"), "x");

            Assert.IsFalse(claimed.DeleteEverything(), "The folder is still there");
            Assert.IsTrue(File.Exists(Path.Combine(ended, FlockLaunchFolder.LockFileName)), "Its lock is back");
            FlockLaunchFolder again = FlockLaunchFolder.ClaimEnded(ended);
            Assert.IsNotNull(again, "So a later launch takes it over and tries again");
            Assert.IsTrue(again.DeleteEverything());
            Assert.IsFalse(Directory.Exists(ended));
        }

        // ---- Taking over ----

        [Test]
        public void Start_TakesOverAnEndedLaunchsQueueAndRecords_AndLeavesALiveOneAlone()
        {
            FlockAnalyticsLaunches live = StartLaunch();
            PlantQueued(Path.Combine(live.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName), "0002_live");
            string ended = PlantEndedLaunch(MarkerJson("s-ended"), SessionJson("s-ended"), "0001_ended");

            FlockAnalyticsLaunches next = StartLaunch();

            CollectionAssert.AreEqual(new[] { "0001_ended" }, QueuedNames(Path.Combine(next.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)));
            CollectionAssert.AreEqual(new[] { "0002_live" }, QueuedNames(Path.Combine(live.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)),
                "A launch still running keeps its queue: nobody else sends it.");
            List<FlockEndedLaunch> endedLaunches = next.TakeEndedLaunches();
            Assert.AreEqual(1, endedLaunches.Count);
            Assert.AreEqual(Path.Combine(ended, FlockAnalyticsLaunches.TerminationMarkerFileName), endedLaunches[0].TerminationMarkerPath);
            endedLaunches.ForEach(_held.Add);
        }

        [Test]
        public void Start_TwoLaunchesAtOnce_OnlyOneTakesTheEndedLaunchOver()
        {
            PlantEndedLaunch(MarkerJson("s1"), null, "0001_ended");

            FlockAnalyticsLaunches first = StartLaunch();
            FlockAnalyticsLaunches second = StartLaunch();

            List<FlockEndedLaunch> firstTook = first.TakeEndedLaunches();
            firstTook.ForEach(_held.Add);
            Assert.AreEqual(1, firstTook.Count);
            Assert.AreEqual(0, second.TakeEndedLaunches().Count);
            Assert.AreEqual(0, QueuedNames(Path.Combine(second.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)).Count);
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void Start_QueuedEntryThatCouldNotMove_KeepsTheEndedLaunchEvenOnceItCouldBeDeleted()
        {
            string ended = PlantEndedLaunch(null, null);
            string queue = Path.Combine(ended, FlockAnalyticsLaunches.AnalyticsEventsQueueName);
            PlantQueued(queue, "0001_stuck");
            string stuck = Path.Combine(queue, "0001_stuck" + FlockEventCacheNames.Extension);

            FlockAnalyticsLaunches launch;
            using (new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.Read))
                launch = StartLaunch();

            List<FlockEndedLaunch> endedLaunches = launch.TakeEndedLaunches();
            Assert.AreEqual(1, endedLaunches.Count);
            Assert.IsFalse(endedLaunches[0].QueuesTakenOver);
            // The file could be deleted now; only the rule about entries left behind keeps it.
            Assert.IsFalse(endedLaunches[0].DeleteEverything());
            Assert.IsTrue(File.Exists(stuck));
            FlockLaunchFolder again = FlockLaunchFolder.ClaimEnded(ended);
            Assert.IsNotNull(again, "Let go, so a later launch takes the entry over.");
            again.Dispose();
        }

        [Test]
        public void Start_NameAlreadyInThisLaunchsQueue_KeepsBothEntries()
        {
            PlantEndedLaunch(null, null, "0001_same");
            PlantEndedLaunch(null, null, "0001_same");

            FlockAnalyticsLaunches launch = StartLaunch();

            List<string> names = QueuedNames(Path.Combine(launch.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName));
            Assert.AreEqual(2, names.Count);
            Assert.AreEqual("0001_same", names[0]);
            StringAssert.StartsWith("0001_same_", names[1], "A taken name gets a suffix that sorts right after it.");
        }

        [Test]
        public void Start_LockCannotBeTaken_GetsAFolderOfItsOwnAndTakesOverNothing()
        {
            PlantEndedLaunch(MarkerJson("s1"), null, "0001_ended");
            // The launches folder's parent is a file, so no folder can be made under it.
            string blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "not a folder");

            FlockAnalyticsLaunches launch = FlockAnalyticsLaunches.Start(blocked, FlockTestGame.Key, null);
            _held.Add(launch);

            Assert.IsFalse(launch.IsHoldingItsFolder);
            Assert.AreEqual(0, launch.TakeEndedLaunches().Count);
        }

        // ---- Files a build before 1.48.0 left ----

        private class EarlierBuildRecords
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public FlockEarlierBuildFiles Files(string queuesFolder) =>
                new FlockEarlierBuildFiles(queuesFolder, key => Values.TryGetValue(key, out string value) ? value : null, key => Values.Remove(key));
        }

        [Test]
        public void Start_EarlierBuildFiles_AreTakenOverOnce()
        {
            string earlierFolder = Path.Combine(_root, "earlier");
            PlantQueued(Path.Combine(earlierFolder, FlockAnalyticsLaunches.LogEventsQueueName), "0001_earlier");
            EarlierBuildRecords records = new EarlierBuildRecords();
            records.Values["flock_session_active"] = SessionJson("s-earlier");
            records.Values["flock_termination_marker"] = MarkerJson("s-earlier");

            FlockAnalyticsLaunches first = StartLaunch(records.Files(earlierFolder));

            CollectionAssert.AreEqual(new[] { "0001_earlier" }, QueuedNames(Path.Combine(first.Folder, FlockAnalyticsLaunches.LogEventsQueueName)));
            Assert.AreEqual(0, records.Values.Count, "The records leave PlayerPrefs once they are saved in a launch folder.");
            List<FlockEndedLaunch> ended = first.TakeEndedLaunches();
            ended.ForEach(_held.Add);
            Assert.AreEqual(1, ended.Count);
            StringAssert.Contains("s-earlier", File.ReadAllText(ended[0].SessionStatePath));
            StringAssert.Contains("s-earlier", File.ReadAllText(ended[0].TerminationMarkerPath));
            Assert.IsFalse(File.Exists(Path.Combine(_root, FlockAnalyticsLaunches.EarlierBuildFilesLockName)));

            FlockAnalyticsLaunches second = StartLaunch(records.Files(earlierFolder));
            Assert.AreEqual(0, second.TakeEndedLaunches().Count, "Taken over once: the second launch finds nothing left.");
        }

        [Test]
        public void Start_EarlierBuildFilesWhileAnotherLaunchTakesThemOver_TakesNothing()
        {
            string earlierFolder = Path.Combine(_root, "earlier");
            PlantQueued(Path.Combine(earlierFolder, FlockAnalyticsLaunches.AnalyticsEventsQueueName), "0001_earlier");
            EarlierBuildRecords records = new EarlierBuildRecords();
            records.Values["flock_termination_marker"] = MarkerJson("s-earlier");
            Directory.CreateDirectory(_root);

            using (new FileStream(Path.Combine(_root, FlockAnalyticsLaunches.EarlierBuildFilesLockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                FlockAnalyticsLaunches launch = StartLaunch(records.Files(earlierFolder));
                Assert.AreEqual(0, launch.TakeEndedLaunches().Count);
                Assert.AreEqual(0, QueuedNames(Path.Combine(launch.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)).Count);
            }
            Assert.AreEqual(1, records.Values.Count, "The records stay for whichever launch holds the lock.");
        }

        [Test]
        public void Start_SecondLaunchStartingDuringTheEarlierBuildTakeover_TakesNothing()
        {
            string earlierFolder = Path.Combine(_root, "earlier");
            PlantQueued(Path.Combine(earlierFolder, FlockAnalyticsLaunches.AnalyticsEventsQueueName), "0001_earlier");
            EarlierBuildRecords records = new EarlierBuildRecords();
            records.Values["flock_session_active"] = SessionJson("s-earlier");
            FlockAnalyticsLaunches second = null;
            // Between saving the first record and deleting it from PlayerPrefs, the way another copy of the game can start.
            FlockTemporaryFiles.SetBeforeNextMoveForTesting(_ => second = StartLaunch(records.Files(earlierFolder)));

            FlockAnalyticsLaunches first = StartLaunch(records.Files(earlierFolder));

            Assert.IsNotNull(second, "The second launch started while the first was taking the files over.");
            Assert.AreEqual(0, second.TakeEndedLaunches().Count);
            Assert.AreEqual(0, QueuedNames(Path.Combine(second.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)).Count);
            List<FlockEndedLaunch> firstTook = first.TakeEndedLaunches();
            firstTook.ForEach(_held.Add);
            Assert.AreEqual(1, firstTook.Count);
            CollectionAssert.AreEqual(new[] { "0001_earlier" }, QueuedNames(Path.Combine(first.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)));
        }

        // ---- Temporary files ----

        [Test]
        public void MakePath_TwoWritesOfOneFile_GetTemporaryFilesOfTheirOwn()
        {
            string destination = Path.Combine(_root, "file.json");
            string first = FlockTemporaryFiles.MakePath(destination);
            string second = FlockTemporaryFiles.MakePath(destination);

            Assert.AreNotEqual(first, second);
            StringAssert.StartsWith(destination + ".", first);
            Assert.IsTrue(FlockTemporaryFiles.IsTemporaryFile(first));
        }

        [Test]
        public void DeleteLeftOverFiles_DeletesOnlyTemporaryFilesOverAMinuteOld()
        {
            Directory.CreateDirectory(Path.Combine(_root, "nested"));
            string fresh = Path.Combine(_root, "a.json." + Guid.NewGuid().ToString("N") + ".tmp");
            string old = Path.Combine(_root, "b.json." + Guid.NewGuid().ToString("N") + ".tmp");
            string oldNested = Path.Combine(_root, "nested", "c.json." + Guid.NewGuid().ToString("N") + ".tmp");
            string oldKept = Path.Combine(_root, "d.json");
            foreach (string path in new[] { fresh, old, oldNested, oldKept })
                File.WriteAllText(path, "{}");
            DateTime overAMinute = DateTime.UtcNow - TimeSpan.FromSeconds(61);
            File.SetLastWriteTimeUtc(old, overAMinute);
            File.SetLastWriteTimeUtc(oldNested, overAMinute);
            File.SetLastWriteTimeUtc(oldKept, overAMinute);
            // Another process's write in progress is not a crash's leftovers; half a minute old, it stays under the minute on a slow runner too.
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow - TimeSpan.FromSeconds(30));

            Assert.AreEqual(1, FlockTemporaryFiles.DeleteLeftOverFiles(_root, false));
            Assert.IsTrue(File.Exists(fresh));
            Assert.IsFalse(File.Exists(old));
            Assert.IsTrue(File.Exists(oldNested), "Not in subfolders unless asked.");
            Assert.IsTrue(File.Exists(oldKept));

            Assert.AreEqual(1, FlockTemporaryFiles.DeleteLeftOverFiles(_root, true));
            Assert.IsFalse(File.Exists(oldNested));
        }

        [Test]
        public void EventCache_AtStart_SweepsOnlyOldTemporaryFiles()
        {
            string queue = Path.Combine(_root, "queue");
            Directory.CreateDirectory(queue);
            string fresh = Path.Combine(queue, "1.evt." + Guid.NewGuid().ToString("N") + ".tmp");
            string old = Path.Combine(queue, "2.evt.tmp");
            File.WriteAllText(fresh, "{}");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromMinutes(2));

            new FlockEventCache<Dummy>(_root, "queue", 100, 10, new NullFlockLogger());

            Assert.IsTrue(File.Exists(fresh));
            Assert.IsFalse(File.Exists(old));
        }

        [Test]
        public void EventCache_FolderDeletedWhileRunning_KeepsQueueing()
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_root, "queue", 100, 10, new NullFlockLogger());
            Directory.Delete(Path.Combine(_root, "queue"), true);

            Assert.IsNotNull(cache.Enqueue(new Dummy { Value = "after" }));
        }

        [Test]
        public void Replace_DestinationGone_ThrowsAndLeavesNoTemporaryFile()
        {
            Directory.CreateDirectory(_root);
            Assert.Catch<Exception>(() => FlockTemporaryFiles.Replace(Path.Combine(_root, "gone.evt"), "{}"));
            Assert.AreEqual(0, Directory.GetFiles(_root).Length, "An erased entry is not brought back by a rewrite.");
        }

        [Test]
        public void SnapshotWrite_AnotherProcessWritesTheSameKeyBetweenWriteAndMove_BothSaves()
        {
            FlockSnapshotStore first = new FlockSnapshotStore(_root, new NullFlockLogger());
            FlockSnapshotStore second = new FlockSnapshotStore(_root, new NullFlockLogger());
            bool secondSaved = false;
            FlockTemporaryFiles.SetBeforeNextMoveForTesting(_ => secondSaved = second.Write("scope", "key", new Dummy { Value = "second" }));

            bool firstSaved = first.Write("scope", "key", new Dummy { Value = "first" });

            Assert.IsTrue(secondSaved);
            Assert.IsTrue(firstSaved, "A temporary file named after the destination alone was taken by the other write.");
            Assert.IsTrue(first.TryRead("scope", "key", out Dummy value));
            Assert.AreEqual("first", value.Value, "The save that moved last wins.");
            Assert.AreEqual(0, Directory.GetFiles(_root, "*" + FlockTemporaryFiles.Extension, SearchOption.AllDirectories).Length);
        }

        [Test]
        public void SnapshotStore_DeleteLeftOverFiles_SweepsEveryScopeByAge()
        {
            FlockSnapshotStore store = new FlockSnapshotStore(_root, new NullFlockLogger());
            store.Write("scope", "key", new Dummy { Value = "a" });
            string folder = Path.GetDirectoryName(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories)[0]);
            string old = Path.Combine(folder, "x.json." + Guid.NewGuid().ToString("N") + ".tmp");
            string fresh = Path.Combine(folder, "y.json." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(old, "{}");
            File.WriteAllText(fresh, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromMinutes(2));

            Assert.AreEqual(1, store.DeleteLeftOverFiles());
            Assert.IsTrue(File.Exists(fresh));
        }

        [Test]
        public void AssetCache_TwoDownloadsOfOneAsset_BothSave_AndOldTemporaryFilesAreSwept()
        {
            FlockAssetCache first = new FlockAssetCache(_root);
            FlockAssetCache second = new FlockAssetCache(_root);
            DateTime updated = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
            FlockTemporaryFiles.SetBeforeNextMoveForTesting(_ => second.Write("asset", updated, new byte[] { 2 }));

            first.Write("asset", updated, new byte[] { 1 });

            Assert.IsTrue(first.TryGetCachedFileUrl("asset", updated, out string url));
            Assert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(new Uri(url).LocalPath));
            string old = Path.Combine(_root, "z.cache." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromMinutes(2));
            Assert.AreEqual(1, first.DeleteLeftOverFiles());
            Assert.AreEqual(0, Directory.GetFiles(_root, "*" + FlockTemporaryFiles.Extension).Length);
        }

        private class Dummy
        {
            public string Value { get; set; }
        }

        // ---- Through the SDK ----

        private static FlockTestClient CreateSdk(FlockFakeTransport transport)
        {
            FlockTestClient sdk = FlockTestClient.Create(transport, config => config.AnalyticsConfig.PersistSessionOnDisk = true);
            transport.GoOffline();
            sdk.SetReachable(false);
            return sdk;
        }

        private static void Initialize(FlockTestClient sdk)
        {
            sdk.LoginAs("player-1");
            sdk.Run(() => sdk.Client.Analytics.InitializeAsync(CancellationToken.None));
        }

        private static List<string> QueuedContents(FlockTestClient sdk, string queue)
        {
            return FlockAnalyticsLaunches.QueuedEntries(Path.Combine(sdk.Client.AnalyticsLaunches.Folder, queue)).Select(File.ReadAllText).ToList();
        }

        private static string PlantEndedLaunchUnderTheTestFolder(string sessionId)
        {
            FlockLaunchFolder folder = FlockLaunchFolder.Create(FlockTestGame.LaunchesFolderIn(FlockTestSavedFiles.AnalyticsFolder));
            File.WriteAllText(Path.Combine(folder.Path, FlockAnalyticsLaunches.TerminationMarkerFileName), MarkerJson(sessionId));
            File.WriteAllText(Path.Combine(folder.Path, FlockAnalyticsLaunches.SessionStateFileName), SessionJson(sessionId));
            folder.Dispose();
            return folder.Path;
        }

        [Test]
        public void Sdk_EndedLaunch_ReportsItsCrashAndSessionOnce_ThenDeletesIt()
        {
            string ended = PlantEndedLaunchUnderTheTestFolder("s-crashed");
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient sdk = CreateSdk(transport))
            {
                Initialize(sdk);

                Assert.AreEqual(1, QueuedContents(sdk, FlockAnalyticsLaunches.LogEventsQueueName).Count(e => e.Contains(FlockTerminationTracker.EventName) && e.Contains("s-crashed")));
                Assert.AreEqual(1, QueuedContents(sdk, FlockAnalyticsLaunches.SessionEndsQueueName).Count(e => e.Contains("s-crashed")));
                Assert.IsFalse(Directory.Exists(ended), "Reported, so the ended launch's folder is gone.");

                // A second initialization (sign-out, then sign-in) reports nothing again.
                sdk.Client.ClearTokens();
                Initialize(sdk);
                Assert.AreEqual(1, QueuedContents(sdk, FlockAnalyticsLaunches.LogEventsQueueName).Count(e => e.Contains(FlockTerminationTracker.EventName)));
                Assert.AreEqual(1, QueuedContents(sdk, FlockAnalyticsLaunches.SessionEndsQueueName).Count(e => e.Contains("s-crashed")));
            }
        }

        [Test]
        public void Sdk_OwnLaunchsMarker_IsNotReportedAsACrash()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient sdk = CreateSdk(transport))
            {
                File.WriteAllText(sdk.Client.AnalyticsLaunches.TerminationMarkerPath, MarkerJson("s-mine"));
                Initialize(sdk);

                Assert.AreEqual(0, QueuedContents(sdk, FlockAnalyticsLaunches.LogEventsQueueName).Count(e => e.Contains(FlockTerminationTracker.EventName)));
                Assert.IsTrue(File.Exists(sdk.Client.AnalyticsLaunches.TerminationMarkerPath));
            }
        }

        // The session-end queue's folder becomes a file, so nothing can be written to it.
        private static string BlockSessionEndQueue(FlockTestClient sdk)
        {
            string queue = Path.Combine(sdk.Client.AnalyticsLaunches.Folder, FlockAnalyticsLaunches.SessionEndsQueueName);
            Directory.Delete(queue, true);
            File.WriteAllText(queue, "blocked");
            return queue;
        }

        [Test]
        public void Sdk_ConsentOff_DropsTheEndedLaunchsMarkerRatherThanReadingItEveryLaunch()
        {
            string ended = PlantEndedLaunchUnderTheTestFolder("s-crashed");
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient sdk = CreateSdk(transport))
            {
                sdk.Client.Analytics.SetConsent(false);
                // The ended launch is kept (its session cannot be spooled), so only the marker's own deletion can drop it.
                BlockSessionEndQueue(sdk);
                Initialize(sdk);

                Assert.AreEqual(0, QueuedContents(sdk, FlockAnalyticsLaunches.LogEventsQueueName).Count(e => e.Contains(FlockTerminationTracker.EventName)));
                Assert.IsTrue(Directory.Exists(ended));
                Assert.IsFalse(File.Exists(Path.Combine(ended, FlockAnalyticsLaunches.TerminationMarkerFileName)));
            }
        }

        [Test]
        public void Sdk_OrphanedSessionCouldNotBeSpooled_KeepsTheEndedLaunchForALaterOne()
        {
            string ended = PlantEndedLaunchUnderTheTestFolder("s-crashed");
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient sdk = CreateSdk(transport))
            {
                string queue = BlockSessionEndQueue(sdk);

                Initialize(sdk);

                Assert.IsTrue(File.Exists(Path.Combine(ended, FlockAnalyticsLaunches.SessionStateFileName)));
                Assert.IsFalse(File.Exists(Path.Combine(ended, FlockAnalyticsLaunches.TerminationMarkerFileName)),
                    "The crash was queued, so a later launch must not report it again.");
                Assert.AreEqual(1, QueuedContents(sdk, FlockAnalyticsLaunches.LogEventsQueueName).Count(e => e.Contains(FlockTerminationTracker.EventName)));

                // A second initialization in this launch leaves it alone: it was let go, and another launch may hold it now.
                File.Delete(queue);
                sdk.Client.ClearTokens();
                Initialize(sdk);
                Assert.IsTrue(File.Exists(Path.Combine(ended, FlockAnalyticsLaunches.SessionStateFileName)));
                Assert.AreEqual(0, QueuedContents(sdk, FlockAnalyticsLaunches.SessionEndsQueueName).Count(e => e.Contains("s-crashed")));

                FlockLaunchFolder later = FlockLaunchFolder.ClaimEnded(ended);
                Assert.IsNotNull(later, "Let go, not deleted: the next launch finds the session and spools it.");
                later.Dispose();
            }
        }

        [Test]
        public void Sdk_AtStart_SweepsOldTemporaryFilesFromTheSnapshotsAndTheAssetCache()
        {
            // Under the state scope, which the prune of other game versions keeps, so only the sweep can delete it.
            string snapshots = Path.Combine(_root, "snapshots", FlockSnapshotStore.StateScope);
            string assets = Path.Combine(_root, "assets");
            Directory.CreateDirectory(snapshots);
            Directory.CreateDirectory(assets);
            string[] planted =
            {
                Path.Combine(snapshots, "a.json." + Guid.NewGuid().ToString("N") + ".tmp"),
                Path.Combine(assets, "b.cache." + Guid.NewGuid().ToString("N") + ".tmp")
            };
            foreach (string path in planted)
            {
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(2));
            }

            using (FlockTestClient.Create(new FlockFakeTransport(), config =>
                   {
                       config.OfflineCacheDirectory = Path.Combine(_root, "snapshots");
                       config.AssetCacheDirectory = assets;
                   }))
            {
                Assert.IsFalse(File.Exists(planted[0]), "The snapshot store sweeps when the SDK starts.");
                Assert.IsFalse(File.Exists(planted[1]), "The asset cache sweeps when the SDK starts.");
            }
        }

        [Test]
        public void EarlierBuildFiles_OnThisMachine_ReadAndDeleteTheRecordsAnEarlierBuildSavedInPlayerPrefs()
        {
            const string key = "flock_termination_marker";
            bool had = PlayerPrefs.HasKey(key);
            string saved = PlayerPrefs.GetString(key, null);
            try
            {
                PlayerPrefs.SetString(key, "marker-from-1.47");
                FlockEarlierBuildFiles files = FlockEarlierBuildFiles.OnThisMachine();

                Assert.AreEqual(FlockUtil.FlockFilePath, files.QueuesFolder);
                Assert.AreEqual("marker-from-1.47", files.TerminationMarker);
                files.DeleteRecords();
                Assert.IsFalse(PlayerPrefs.HasKey(key));
            }
            finally
            {
                if (had)
                    PlayerPrefs.SetString(key, saved);
                else
                    PlayerPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void EventCache_Rewrite_NeverBringsBackAnEntryErasedMeanwhile()
        {
            FlockEventCache<Dummy> cache = new FlockEventCache<Dummy>(_root, "queue", 100, 10, new NullFlockLogger());
            string path = cache.Enqueue(new Dummy { Value = "a" });

            // Erased between being read and being written back, the way Clear() on another thread can.
            cache.Rewrite(entry => { File.Delete(path); return true; }, entry => entry.Value = "b");

            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_root, "queue")).Length);
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void Sdk_QueuedEntryThatCouldNotMove_KeepsTheEndedLaunch_WithoutItsReportedRecords()
        {
            string ended = PlantEndedLaunchUnderTheTestFolder("s-crashed");
            string queue = Path.Combine(ended, FlockAnalyticsLaunches.LogEventsQueueName);
            PlantQueued(queue, "0001_stuck");
            string stuck = Path.Combine(queue, "0001_stuck" + FlockEventCacheNames.Extension);
            FlockFakeTransport transport = new FlockFakeTransport();

            // Another program holding the file open keeps the takeover from moving it.
            using (new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FlockTestClient sdk = CreateSdk(transport))
            {
                Initialize(sdk);

                Assert.IsTrue(File.Exists(stuck), "An entry that could not move is not deleted with the folder.");
                Assert.IsFalse(File.Exists(Path.Combine(ended, FlockAnalyticsLaunches.TerminationMarkerFileName)));
                Assert.IsFalse(File.Exists(Path.Combine(ended, FlockAnalyticsLaunches.SessionStateFileName)),
                    "The session was spooled, so a later launch must not spool it again.");
                Assert.AreEqual(1, QueuedContents(sdk, FlockAnalyticsLaunches.SessionEndsQueueName).Count(e => e.Contains("s-crashed")));
            }

            FlockAnalyticsLaunches later = FlockAnalyticsLaunches.Start(FlockAnalyticsLaunches.FolderForTesting, FlockTestGame.Key, null);
            _held.Add(later);
            CollectionAssert.Contains(QueuedNames(Path.Combine(later.Folder, FlockAnalyticsLaunches.LogEventsQueueName)), "0001_stuck",
                "A later launch takes the entry over once it can move.");
        }

        [Test]
        public void Sdk_ShutdownThenCreateInOneProcess_TakesOverTheFirstLaunch()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            string firstFolder;
            using (FlockTestClient first = CreateSdk(transport))
                firstFolder = first.Client.AnalyticsLaunches.Folder;

            using (FlockTestClient second = CreateSdk(transport))
            {
                List<FlockEndedLaunch> ended = second.Client.AnalyticsLaunches.TakeEndedLaunches();
                ended.ForEach(_held.Add);
                Assert.IsTrue(ended.Any(launch => launch.SessionStatePath.StartsWith(firstFolder, StringComparison.Ordinal)),
                    "Shutdown let go of the first launch's folder.");
            }
        }

        [Test]
        public void Sdk_PlayModeStartedWithoutADomainReload_LetsGoOfThePreviousLaunch()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            FlockTestClient first = CreateSdk(transport);
            string firstFolder = first.Client.AnalyticsLaunches.Folder;
            typeof(FlockClient).GetMethod("ResetStaticState", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            // What the same reset does to the events hub, so the abandoned client's subscriptions reach no later test.
            FlockEvents.ClearAll();

            FlockLaunchFolder claimed = FlockLaunchFolder.ClaimEnded(firstFolder);
            Assert.IsNotNull(claimed, "The static reset let go of the previous play session's folder.");
            claimed.Dispose();
            first.Dispose();
        }

        [Test]
        public void Sdk_TestSdk_KeepsItsFilesOutOfTheGamesOwnFolder()
        {
            string gameFolder = FlockAnalyticsLaunches.DefaultFolder();
            Assert.AreEqual(Path.GetFullPath(Path.Combine(Application.persistentDataPath, "Flock", "analytics")), gameFolder);
            Assert.IsNotNull(_runFolder, "The run's SetUpFixture gives every test SDK a folder of its own.");
            FlockAnalyticsLaunches.FolderForTesting = _runFolder;
            string[] before = Directory.Exists(gameFolder) ? Directory.GetFileSystemEntries(gameFolder, "*", SearchOption.AllDirectories) : new string[0];

            using (FlockTestClient sdk = CreateSdk(new FlockFakeTransport()))
            {
                StringAssert.StartsWith(Path.GetFullPath(_runFolder), sdk.Client.AnalyticsLaunches.Folder);
                Initialize(sdk);
            }

            string[] after = Directory.Exists(gameFolder) ? Directory.GetFileSystemEntries(gameFolder, "*", SearchOption.AllDirectories) : new string[0];
            CollectionAssert.AreEquivalent(before, after);
        }

        [Test]
        public void Sdk_LockCannotBeTaken_SaysSo()
        {
            string blocked = Path.Combine(_root, "blocked");
            Directory.CreateDirectory(_root);
            File.WriteAllText(blocked, "not a folder");
            FlockAnalyticsLaunches.FolderForTesting = blocked;

            using (FlockTestClient sdk = FlockTestClient.Create(new FlockFakeTransport()))
            {
                Assert.IsFalse(sdk.Client.AnalyticsLaunches.IsHoldingItsFolder);
                Assert.IsTrue(sdk.Logger.Warnings.Any(entry => entry.Contains("Could not lock a folder for this launch's analytics files")));
            }
        }

        // ---- Each game's launches ----

        private const string AnotherGame = "0123456789abcdef";

        [Test]
        public void AnotherGamesEndedLaunch_IsNotTakenOver_ButItsOwnGameTakesItOver()
        {
            // A project whose game changed: the old game's unsent entry must never reach the new game's backend.
            string otherGames = FlockAnalyticsLaunches.LaunchesFolderOf(_root, AnotherGame);
            FlockLaunchFolder planted = FlockLaunchFolder.Create(otherGames);
            Assert.IsNotNull(planted, "Precondition: another game's launch folder was made");
            FlockSavedFiles.CreateFolder(Path.Combine(planted.Path, FlockAnalyticsLaunches.AnalyticsEventsQueueName));
            File.WriteAllText(Path.Combine(planted.Path, FlockAnalyticsLaunches.AnalyticsEventsQueueName, "0001_theirs" + FlockEventCacheNames.Extension), "{}");
            planted.Dispose();

            FlockAnalyticsLaunches ours = StartLaunch();
            Assert.AreEqual(0, ours.TakeEndedLaunches().Count, "This game takes over none of another game's launches");
            Assert.IsEmpty(QueuedNames(Path.Combine(ours.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)));

            FlockAnalyticsLaunches theirs = StartLaunch(gameKey: AnotherGame);
            List<FlockEndedLaunch> taken = theirs.TakeEndedLaunches();
            _held.AddRange(taken);
            Assert.AreEqual(1, taken.Count, "Its own game takes it over");
            CollectionAssert.Contains(QueuedNames(Path.Combine(theirs.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)), "0001_theirs");
        }

        [Test]
        public void TheLaunchesAnEarlierBuildKeptForEveryGame_AreTakenOverByTheFirstGameToStart_Once()
        {
            // Builds before 1.88.1 kept every game's launches in one folder: nearly every project only ever had one game.
            FlockLaunchFolder planted = FlockLaunchFolder.Create(FlockAnalyticsLaunches.EveryGamesLaunchesFolder(_root));
            Assert.IsNotNull(planted, "Precondition: an earlier build's launch folder was made");
            FlockSavedFiles.CreateFolder(Path.Combine(planted.Path, FlockAnalyticsLaunches.AnalyticsEventsQueueName));
            File.WriteAllText(Path.Combine(planted.Path, FlockAnalyticsLaunches.AnalyticsEventsQueueName, "0001_earlier" + FlockEventCacheNames.Extension), "{}");
            planted.Dispose();

            FlockAnalyticsLaunches first = StartLaunch();
            List<FlockEndedLaunch> ended = first.TakeEndedLaunches();
            Assert.AreEqual(1, ended.Count, "The first game to start takes it over");
            CollectionAssert.Contains(QueuedNames(Path.Combine(first.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)), "0001_earlier");
            foreach (FlockEndedLaunch each in ended)
                Assert.IsTrue(each.DeleteEverything(), "Precondition: the earlier build's launch is deleted once reported");
            Assert.IsEmpty(FlockLaunchFolder.FindFolders(FlockAnalyticsLaunches.EveryGamesLaunchesFolder(_root)), "Nothing is left where the earlier build kept them");

            FlockAnalyticsLaunches second = StartLaunch(gameKey: AnotherGame);
            Assert.AreEqual(0, second.TakeEndedLaunches().Count, "Taken over once: a second game finds nothing left");
            Assert.IsEmpty(QueuedNames(Path.Combine(second.Folder, FlockAnalyticsLaunches.AnalyticsEventsQueueName)));
        }

        [Test]
        public void TheLaunchesAnEarlierBuildKept_AreHandedOverBeforeTheGamesOwn_OldestFirst()
        {
            FlockLaunchFolder earlier = FlockLaunchFolder.Create(FlockAnalyticsLaunches.EveryGamesLaunchesFolder(_root));
            Assert.IsNotNull(earlier, "Precondition: an earlier build's launch folder was made");
            string earlierPath = earlier.Path;
            earlier.Dispose();
            PlantEndedLaunch(MarkerJson("s-own"));

            List<FlockEndedLaunch> ended = StartLaunch().TakeEndedLaunches();
            _held.AddRange(ended);
            Assert.AreEqual(2, ended.Count, "Precondition: both are taken over");
            StringAssert.StartsWith(earlierPath, ended[0].TerminationMarkerPath, "The earlier build's launch, older than any of the game's own, comes first");
        }

        // ---- The editor's own sign-in ----

        [Test]
        public void ATestClientPutsBackTheEditorsSavedSignIn()
        {
            PlayerPrefs.SetString("Flock.AccessToken", "the-developers-token");
            PlayerPrefs.SetInt(ConsentGrantedKey, 1);
            try
            {
                using (FlockTestClient h = FlockTestClient.Create(new FlockFakeTransport()))
                {
                    Assert.IsFalse(PlayerPrefs.HasKey("Flock.AccessToken"), "Precondition: a test's client starts signed out");
                    PlayerPrefs.SetString("Flock.AccessToken", "a-test-token");
                }
                Assert.AreEqual("the-developers-token", PlayerPrefs.GetString("Flock.AccessToken"), "The editor's sign-in is back");
                Assert.AreEqual(1, PlayerPrefs.GetInt(ConsentGrantedKey, -1), "And its consent answer, as the number it was");
            }
            finally
            {
                PlayerPrefs.DeleteKey("Flock.AccessToken");
            }
        }
    }
}
