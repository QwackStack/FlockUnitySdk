using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Flock.Analytics
{
    /// <summary>A launch that has ended: where it kept its crash marker and its live-session record, and its folder, held until it is deleted or let go.</summary>
    internal sealed class FlockEndedLaunch : IDisposable
    {
        private readonly FlockLaunchFolder _folder;

        internal string SessionStatePath { get; }
        internal string TerminationMarkerPath { get; }
        /// <summary>False when a queued entry could not be moved out; the folder is then kept for a later launch.</summary>
        internal bool QueuesTakenOver { get; }

        internal FlockEndedLaunch(FlockLaunchFolder folder, bool queuesTakenOver)
        {
            _folder = folder;
            QueuesTakenOver = queuesTakenOver;
            SessionStatePath = Path.Combine(folder.Path, FlockAnalyticsLaunches.SessionStateFileName);
            TerminationMarkerPath = Path.Combine(folder.Path, FlockAnalyticsLaunches.TerminationMarkerFileName);
        }

        /// <summary>Deletes the launch's folder once its records are reported, unless a queued entry is still in it: then it is let go for a later launch. Returns whether it is gone.</summary>
        internal bool DeleteEverything()
        {
            if (QueuesTakenOver)
                return _folder.DeleteEverything();
            _folder.Dispose();
            return false;
        }

        /// <summary>Lets go of the launch and leaves its files, so a later launch finds them again.</summary>
        public void Dispose() => _folder.Dispose();
    }

    /// <summary>What a build before 1.48.0 kept for every launch at once: the three event queues straight under the Flock folder, and two records in PlayerPrefs.</summary>
    internal sealed class FlockEarlierBuildFiles
    {
        private const string SessionStateKey = "flock_session_active";
        private const string TerminationMarkerKey = "flock_termination_marker";

        internal string QueuesFolder { get; }
        private readonly Func<string, string> _readRecord;
        private readonly Action<string> _deleteRecord;

        internal FlockEarlierBuildFiles(string queuesFolder, Func<string, string> readRecord, Action<string> deleteRecord)
        {
            QueuesFolder = queuesFolder;
            _readRecord = readRecord;
            _deleteRecord = deleteRecord;
        }

        /// <summary>The files an earlier build of this game left on this machine. Main thread only.</summary>
        internal static FlockEarlierBuildFiles OnThisMachine()
        {
            return new FlockEarlierBuildFiles(FlockUtil.FlockFilePath,
                key => PlayerPrefs.GetString(key, null),
                key =>
                {
                    PlayerPrefs.DeleteKey(key);
                    PlayerPrefs.Save();
                });
        }

        internal string SessionState => _readRecord(SessionStateKey);
        internal string TerminationMarker => _readRecord(TerminationMarkerKey);

        internal void DeleteRecords()
        {
            _deleteRecord(SessionStateKey);
            _deleteRecord(TerminationMarkerKey);
        }

        internal bool Exist()
        {
            if (!string.IsNullOrEmpty(SessionState) || !string.IsNullOrEmpty(TerminationMarker))
                return true;
            return FlockAnalyticsLaunches.QueueNames.Any(queue => FlockAnalyticsLaunches.QueuedEntries(Path.Combine(QueuesFolder, queue)).Count > 0);
        }
    }

    /// <summary>The analytics files of each launch of the game, in a folder of that launch's own, locked while it runs.</summary>
    // <analytics folder>/launches/<UTC time>-<8 hex>/ holds the launch's crash marker, its live-session record and its three
    // queues. Two copies of one game, or the Editor beside a player, share persistentDataPath, so no launch may read another's
    // files as left over while that launch still runs. When a launch starts it takes over every launch whose lock it can open:
    // their queued entries move into this launch's queues at once, and their records wait for the analytics provider, which
    // reports each once and then deletes that launch's folder. Consent and sign-in stay per install, in PlayerPrefs.
    internal sealed class FlockAnalyticsLaunches : IDisposable
    {
        internal const string LaunchesFolderName = "launches";
        internal const string SessionStateFileName = "session_state.json";
        internal const string TerminationMarkerFileName = "termination_marker.json";
        internal const string AnalyticsEventsQueueName = "analytics_events";
        internal const string LogEventsQueueName = "log_events";
        internal const string SessionEndsQueueName = "session_ends";
        /// <summary>Held while the files of a build before 1.48.0 are taken over.</summary>
        internal const string EarlierBuildFilesLockName = "earlier-build-files.lock";
        internal const string EarlierBuildFolderPrefix = "earlier-build-";

        internal static readonly string[] QueueNames = { AnalyticsEventsQueueName, LogEventsQueueName, SessionEndsQueueName };

        /// <summary>Where every test's SDK keeps its analytics files instead of the game's own folder. Set by the test fixture only.</summary>
        internal static string FolderForTesting { get; set; }

        private readonly string _analyticsFolder;
        private FlockLaunchFolder _thisLaunch;
        private List<FlockEndedLaunch> _endedLaunches = new List<FlockEndedLaunch>();

        /// <summary>This launch's folder. The event queues are its subfolders.</summary>
        internal string Folder { get; private set; }

        /// <summary>False when this launch's lock could not be taken; nothing was taken over then.</summary>
        internal bool IsHoldingItsFolder => _thisLaunch != null;

        internal string SessionStatePath => Path.Combine(Folder, SessionStateFileName);
        internal string TerminationMarkerPath => Path.Combine(Folder, TerminationMarkerFileName);

        private FlockAnalyticsLaunches(string analyticsFolder)
        {
            _analyticsFolder = Path.GetFullPath(analyticsFolder);
        }

        /// <summary>persistentDataPath/Flock/analytics, as a full path.</summary>
        internal static string DefaultFolder() => Path.GetFullPath(Path.Combine(FlockUtil.FlockFilePath, "analytics"));

        /// <summary>Makes this launch's folder, holds its lock, and takes over every launch that has ended and the files an earlier build left. Without the lock it still gets a folder of its own but takes over nothing.</summary>
        internal static FlockAnalyticsLaunches Start(string analyticsFolder, FlockEarlierBuildFiles earlierBuildFiles)
        {
            FlockAnalyticsLaunches launches = new FlockAnalyticsLaunches(analyticsFolder);
            string launchesFolder = Path.Combine(launches._analyticsFolder, LaunchesFolderName);

            launches._thisLaunch = FlockLaunchFolder.Create(launchesFolder);
            if (launches._thisLaunch == null)
            {
                // A launch that cannot show it is running takes over nothing, so it cannot take a running launch's files either.
                launches.Folder = Path.Combine(launchesFolder, "unheld-" + Guid.NewGuid().ToString("N"));
                return launches;
            }
            launches.Folder = launches._thisLaunch.Path;

            // A folder this launch cannot read is left for a later one; it never stops the SDK starting.
            try
            {
                launches.TakeOverEarlierBuildFiles(earlierBuildFiles, launchesFolder);
            }
            catch (Exception)
            {
            }
            List<string> folders;
            try
            {
                folders = FlockLaunchFolder.FindFolders(launchesFolder);
            }
            catch (Exception)
            {
                return launches;
            }
            // This launch's own folder is among these; its own lock refuses the claim.
            foreach (string folder in folders)
            {
                FlockLaunchFolder ended = FlockLaunchFolder.ClaimEnded(folder);
                if (ended == null)
                    continue;
                launches._endedLaunches.Add(new FlockEndedLaunch(ended, launches.TakeOverQueues(folder)));
            }
            return launches;
        }

        /// <summary>Hands over every launch taken over, oldest first; the caller deletes or lets go of each.</summary>
        internal List<FlockEndedLaunch> TakeEndedLaunches()
        {
            List<FlockEndedLaunch> ended = _endedLaunches;
            _endedLaunches = new List<FlockEndedLaunch>();
            return ended;
        }

        /// <summary>Lets go of this launch and of every launch taken over and not handed on, leaving their files for a later launch.</summary>
        public void Dispose()
        {
            foreach (FlockEndedLaunch ended in _endedLaunches)
                ended.Dispose();
            _endedLaunches.Clear();
            _thisLaunch?.Dispose();
            _thisLaunch = null;
        }

        /// <summary>The queued entries in a queue folder, oldest name first.</summary>
        internal static List<string> QueuedEntries(string queueFolder)
        {
            if (!Directory.Exists(queueFolder))
                return new List<string>();
            List<string> entries = Directory.EnumerateFiles(queueFolder)
                .Where(path => path.EndsWith(FlockEventCacheNames.Extension, StringComparison.Ordinal)).ToList();
            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        private void TakeOverEarlierBuildFiles(FlockEarlierBuildFiles earlier, string launchesFolder)
        {
            if (earlier == null || !earlier.Exist())
                return;

            // Earlier builds kept one set of files for every launch, with nothing to tell whether the game writing them still
            // runs. They are taken over once, by whichever launch opens this lock first.
            string lockPath = Path.Combine(_analyticsFolder, EarlierBuildFilesLockName);
            FileStream earlierBuildLock;
            try
            {
                FlockSavedFiles.CreateFolder(_analyticsFolder);
                earlierBuildLock = FlockSavedFiles.OpenLock(lockPath, FileMode.OpenOrCreate);
            }
            catch (Exception)
            {
                return;
            }

            using (earlierBuildLock)
            {
                // Another launch may have finished the takeover while this one waited for the lock.
                if (!earlier.Exist())
                    return;

                // The records become an ended launch of their own, so they are reported the way every ended launch is.
                FlockLaunchFolder recordsFolder = FlockLaunchFolder.Create(launchesFolder, EarlierBuildFolderPrefix);
                if (recordsFolder == null)
                    return;
                try
                {
                    string sessionState = earlier.SessionState;
                    if (!string.IsNullOrEmpty(sessionState))
                        FlockTemporaryFiles.Save(Path.Combine(recordsFolder.Path, SessionStateFileName), sessionState);
                    string marker = earlier.TerminationMarker;
                    if (!string.IsNullOrEmpty(marker))
                        FlockTemporaryFiles.Save(Path.Combine(recordsFolder.Path, TerminationMarkerFileName), marker);
                }
                catch (Exception)
                {
                    // The records stay where they are, for a later launch.
                    recordsFolder.DeleteEverything();
                    return;
                }
                earlier.DeleteRecords();
                // Entries left behind are still there for the next launch to take over.
                TakeOverQueues(earlier.QueuesFolder);
                _endedLaunches.Add(new FlockEndedLaunch(recordsFolder, true));
            }
            try { FlockSavedFiles.Delete(lockPath); }
            catch (Exception) { }
        }

        // Moves every queued entry in the other folder's queues into this launch's queues. Returns whether all of them moved.
        private bool TakeOverQueues(string fromFolder)
        {
            bool allMoved = true;
            foreach (string queue in QueueNames)
            {
                try
                {
                    string from = Path.Combine(fromFolder, queue);
                    List<string> entries = QueuedEntries(from);
                    if (entries.Count == 0)
                        continue;

                    string to = Path.Combine(Folder, queue);
                    FlockSavedFiles.CreateFolder(to);
                    foreach (string entry in entries)
                    {
                        // Entries are named for the time they were queued, so they keep their age order among this launch's. A
                        // name already taken gets a suffix that sorts right after it.
                        string destination = Path.Combine(to, Path.GetFileName(entry));
                        if (File.Exists(destination))
                            destination = Path.Combine(to, Path.GetFileNameWithoutExtension(entry) + "_"
                                + Guid.NewGuid().ToString("N").Substring(0, 8) + FlockEventCacheNames.Extension);
                        try { FlockSavedFiles.Move(entry, destination); }
                        catch (Exception) { allMoved = false; }
                    }
                }
                catch (Exception)
                {
                    allMoved = false;
                }
            }
            return allMoved;
        }
    }
}
