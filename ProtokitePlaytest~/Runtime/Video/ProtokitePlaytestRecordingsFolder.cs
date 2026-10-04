using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Protokite.Playtest
{
    /// <summary>What a recording was made for, which decides what a later launch keeps and what making room deletes first.</summary>
    internal enum ProtokitePlaytestRecordingKind
    {
        /// <summary>A playtest's recording, kept until it is uploaded to the Protokite session saved beside it.</summary>
        Playtest,
        /// <summary>A test video: kept while there is room, never uploaded.</summary>
        TestVideo
    }

    /// <summary>The Protokite session a recording belongs to, as a later launch needs it to upload the recording. Never the API key.</summary>
    internal sealed class ProtokitePlaytestSavedSession
    {
        public string PlaytestSessionId;
        public string ProtokiteApiUrl;
        /// <summary>The Game Version ID the session started with, or null when the build sent none.</summary>
        public string FlockGameVersionId;
    }

    /// <summary>One recording's folder, held by a lock file its launch keeps open, shared with nobody, for as long as it runs.</summary>
    // A lock rather than a time or a process id: the operating system lets go of it however its owner ends. FileShare.None shuts
    // other processes out on Windows only; Mono and IL2CPP keep share modes inside the process on macOS and Linux.
    internal sealed class ProtokitePlaytestRecordingRun : IDisposable
    {
        internal const string LockFileName = "in-use.lock";
        internal const string SessionFileName = "session.json";
        internal const string ReservedBytesFileName = "reserved-bytes.txt";
        internal const string TemporaryFileEnding = ".tmp";

        private const string SessionIdKey = "protokite_session_id";
        private const string ApiUrlKey = "protokite_api_url";
        private const string GameVersionIdKey = "flock_game_version_id";

        private FileStream _lock;

        /// <summary>The run folder's full path.</summary>
        internal string FolderPath { get; }

        internal ProtokitePlaytestRecordingKind Kind { get; }

        /// <summary>The folder's name: the UTC time the run started and eight random hex digits, so names sort oldest first.</summary>
        internal string Name => Path.GetFileName(FolderPath);

        private ProtokitePlaytestRecordingRun(string folderPath, ProtokitePlaytestRecordingKind kind, FileStream folderLock)
        {
            FolderPath = folderPath;
            Kind = kind;
            _lock = folderLock;
        }

        /// <summary>Where the run's video is kept once it is finished. It is written as this path plus ".part" until then.</summary>
        internal string VideoPath(string fileExtension)
            => Path.Combine(FolderPath, (Kind == ProtokitePlaytestRecordingKind.TestVideo ? "test-recording-" : "recording-") + Name + fileExtension);

        /// <summary>Makes a run folder of its own for a new recording, holds its lock and saves the room it reserves. Null, with why, when it could not.</summary>
        internal static ProtokitePlaytestRecordingRun Start(string recordingsFolder, ProtokitePlaytestRecordingKind kind, long reservedBytes, out string error)
        {
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string folder = Path.GetFullPath(Path.Combine(ProtokitePlaytestRecordingsFolder.KindFolder(recordingsFolder, kind), name));
            FileStream folderLock = null;
            bool madeTheFolder = false;
            try
            {
                // Another run's folder, however unlikely the same name is, is never taken over or deleted.
                if (Directory.Exists(folder))
                {
                    error = $"the recording folder {folder} is already there.";
                    return null;
                }
                Directory.CreateDirectory(folder);
                madeTheFolder = true;
                folderLock = OpenLock(Path.Combine(folder, LockFileName), FileMode.CreateNew);
                ProtokitePlaytestRecordingRun run = new ProtokitePlaytestRecordingRun(folder, kind, folderLock);
                if (!run.SaveReservedBytes(reservedBytes, out error))
                {
                    run.DeleteEverything();
                    return null;
                }
                return run;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                folderLock?.Dispose();
                // A folder with no lock is never claimed, so it would stay for good.
                try
                {
                    if (madeTheFolder)
                        Directory.Delete(folder, true);
                }
                catch (Exception deleteProblem) when (IsFileProblem(deleteProblem))
                {
                }
                error = $"the recording folder {folder} could not be made: {ex.Message}";
                return null;
            }
        }

        /// <summary>Holds a run whose launch has let go of its lock. Null while the lock is held, and for a folder with no lock file.</summary>
        internal static ProtokitePlaytestRecordingRun ClaimEnded(string runFolder, ProtokitePlaytestRecordingKind kind)
        {
            string folder = Path.GetFullPath(runFolder);
            string lockPath = Path.Combine(folder, LockFileName);
            // No lock file: a folder being made right now, or one whose run deleted everything but the folder itself.
            if (!File.Exists(lockPath))
                return null;
            try
            {
                return new ProtokitePlaytestRecordingRun(folder, kind, OpenLock(lockPath, FileMode.Open));
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                return null;
            }
        }

        /// <summary>Saves the most the run's video may take, which other launches count it at while the run is held.</summary>
        internal bool SaveReservedBytes(long bytes, out string error)
            => SaveThroughTemporaryFile(ReservedBytesFileName, bytes.ToString(CultureInfo.InvariantCulture), out error);

        /// <summary>Saves the Protokite session the run's recording belongs to.</summary>
        internal bool SaveSession(ProtokitePlaytestSavedSession session, out string error)
        {
            JObject saved = new JObject
            {
                [SessionIdKey] = session.PlaytestSessionId,
                [ApiUrlKey] = session.ProtokiteApiUrl
            };
            if (session.FlockGameVersionId != null)
                saved[GameVersionIdKey] = session.FlockGameVersionId;
            return SaveThroughTemporaryFile(SessionFileName, saved.ToString(Formatting.None), out error);
        }

        /// <summary>Removes the run's saved session, and saves of it in progress, so no later launch uploads its recording: a run without one is deleted.</summary>
        internal bool ForgetSession(out string error)
        {
            error = null;
            DeleteTemporaryFiles();
            string path = Path.Combine(FolderPath, SessionFileName);
            if (TryDelete(path))
                return true;
            error = "could not delete " + path;
            return false;
        }

        /// <summary>The room a run reserved, or null when it saved none that can be read.</summary>
        internal static long? ReadReservedBytes(string runFolder)
        {
            string text = ReadSmallFile(Path.Combine(runFolder, ReservedBytesFileName), out _);
            return text != null && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) ? bytes : (long?)null;
        }

        /// <summary>The session saved in a run, or null when none was saved or it names no session. couldNotRead says why a file that is there could not be read.</summary>
        internal static ProtokitePlaytestSavedSession ReadSession(string runFolder, out string couldNotRead)
        {
            string text = ReadSmallFile(Path.Combine(runFolder, SessionFileName), out couldNotRead);
            if (text == null)
                return null;
            try
            {
                JObject saved = JObject.Parse(text);
                string sessionId = saved[SessionIdKey]?.Type == JTokenType.String ? (string)saved[SessionIdKey] : null;
                if (string.IsNullOrEmpty(sessionId))
                    return null;
                return new ProtokitePlaytestSavedSession
                {
                    PlaytestSessionId = sessionId,
                    ProtokiteApiUrl = saved[ApiUrlKey]?.Type == JTokenType.String ? (string)saved[ApiUrlKey] : null,
                    FlockGameVersionId = saved[GameVersionIdKey]?.Type == JTokenType.String ? (string)saved[GameVersionIdKey] : null
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The run's videos, finished or not: every file but its lock, its session, its reservation and temporary files.</summary>
        internal static List<string> VideoFiles(string runFolder)
        {
            List<string> videos = new List<string>();
            try
            {
                foreach (string path in Directory.EnumerateFiles(runFolder))
                {
                    string name = Path.GetFileName(path);
                    if (!IsNamed(name, LockFileName) && !IsNamed(name, SessionFileName) && !IsNamed(name, ReservedBytesFileName)
                        && !name.EndsWith(TemporaryFileEnding, StringComparison.OrdinalIgnoreCase))
                        videos.Add(path);
                }
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
            }
            videos.Sort(StringComparer.Ordinal);
            return videos;
        }

        /// <summary>The run's finished video, of a kind Protokite takes, or null while it has none; a stray file beside it is never taken for it.</summary>
        internal static string FinishedVideoPath(string runFolder)
            => VideoFiles(runFolder).FirstOrDefault(path => ProtokitePlaytestRecordingFiles.ContentTypeFor(path) != null);

        /// <summary>Whether a video in the run is still unfinished: being written, or cut off when its launch ended.</summary>
        internal static bool HasUnfinishedVideo(string runFolder)
            => VideoFiles(runFolder).Any(path => path.EndsWith(ProtokitePlaytestVideoRecording.PartSuffix, StringComparison.OrdinalIgnoreCase));

        /// <summary>What every file in the run takes on disk.</summary>
        internal static long BytesOnDisk(string runFolder)
        {
            long bytes = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(runFolder))
                {
                    try
                    {
                        bytes += new FileInfo(path).Length;
                    }
                    catch (Exception ex) when (IsFileProblem(ex))
                    {
                    }
                }
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
            }
            return bytes;
        }

        /// <summary>Deletes the temporary files a save left. Only a run's holder may: while its launch runs, one may be a save in progress.</summary>
        internal void DeleteTemporaryFiles()
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(FolderPath, "*" + TemporaryFileEnding).ToList())
                {
                    // The wildcard also matches longer endings on Windows.
                    if (path.EndsWith(TemporaryFileEnding, StringComparison.OrdinalIgnoreCase))
                        TryDelete(path);
                }
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
            }
        }

        /// <summary>Deletes the run, its lock last, and lets go of it; the lock file stays when anything could not be deleted. True when the folder is gone.</summary>
        internal bool DeleteEverything()
        {
            bool everythingElseDeleted = true;
            try
            {
                foreach (string path in Directory.EnumerateFiles(FolderPath).ToList())
                {
                    if (!IsNamed(Path.GetFileName(path), LockFileName) && !TryDelete(path))
                        everythingElseDeleted = false;
                }
                foreach (string subfolder in Directory.EnumerateDirectories(FolderPath).ToList())
                {
                    try
                    {
                        Directory.Delete(subfolder, true);
                    }
                    catch (Exception ex) when (IsFileProblem(ex))
                    {
                        everythingElseDeleted = false;
                    }
                }
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                everythingElseDeleted = false;
            }

            Dispose();
            if (!everythingElseDeleted)
                return false;
            try
            {
                File.Delete(Path.Combine(FolderPath, LockFileName));
                Directory.Delete(FolderPath, false);
                return true;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                return false;
            }
        }

        /// <summary>Lets go of the lock and leaves every file where it is, the way a launch that crashed looks to the next one.</summary>
        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }

        internal static bool IsFileProblem(Exception ex)
            => ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException;

        // Shared with nobody: while this handle is open, no other launch can open the lock.
        private static FileStream OpenLock(string lockPath, FileMode mode)
            => new FileStream(lockPath, mode, FileAccess.ReadWrite, FileShare.None);

        // A temporary file of its own per save, moved over the file in one step, so a reader finds the old file or the new one, never half of one.
        private bool SaveThroughTemporaryFile(string fileName, string text, out string error)
        {
            string destination = Path.Combine(FolderPath, fileName);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + TemporaryFileEnding;
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
                error = null;
                return true;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                TryDelete(temporary);
                error = $"{destination} could not be saved: {ex.Message}";
                return false;
            }
        }

        // Read sharing everything, delete included, so the run's own launch can still replace the file while another reads it.
        private static string ReadSmallFile(string path, out string couldNotRead)
        {
            couldNotRead = null;
            if (!File.Exists(path))
                return null;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                couldNotRead = $"{path} could not be read: {ex.Message}";
                return null;
            }
        }

        private static bool IsNamed(string fileName, string name) => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase);

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                return false;
            }
        }
    }

    /// <summary>What going through the runs earlier launches left did.</summary>
    internal sealed class ProtokitePlaytestEarlierRecordings
    {
        /// <summary>Cut-off videos finished with every whole frame they held.</summary>
        public int CutOffVideosFinished;
        public int FramesKeptInFinishedVideos;
        /// <summary>Playtest recordings kept, with their session, to be uploaded.</summary>
        public int RecordingsWaitingToUpload;
        public int TestVideosKept;
        /// <summary>Playtest recordings deleted because no Protokite session started for them.</summary>
        public int RecordingsDeletedWithNoSession;
        /// <summary>Runs deleted because they held no video.</summary>
        public int EmptyRunsRemoved;
        /// <summary>What could not be finished or deleted, and why; each is tried again by the next launch.</summary>
        public readonly List<string> LeftForTheNextLaunch = new List<string>();
    }

    /// <summary>What making room for a new recording found and did.</summary>
    internal sealed class ProtokitePlaytestRoomMade
    {
        /// <summary>The budget less every other run, after deleting: the most the new recording may take.</summary>
        public long BytesLeft;
        /// <summary>What every other run takes after deleting, counting a run still in use at the room it reserved.</summary>
        public long BytesUsedByOtherRuns;
        /// <summary>Whether the disk's free space, rather than the budget, set the most recordings may take.</summary>
        public bool LimitedByTheDisk;
        public int TestVideosDeleted;
        public int WaitingRecordingsDeleted;
        public readonly List<string> CouldNotDelete = new List<string>();
    }

    /// <summary>The run folders under Playtest/ and TestVideos/: what a later launch does with ended ones, and the disk budget they share.</summary>
    internal static class ProtokitePlaytestRecordingsFolder
    {
        internal const string PlaytestFolderName = "Playtest";
        internal const string TestVideosFolderName = "TestVideos";

        /// <summary>No recording starts with less room than this.</summary>
        internal const long SmallestRoomForARecording = 1024L * 1024;

        private static readonly ProtokitePlaytestRecordingKind[] Kinds = { ProtokitePlaytestRecordingKind.Playtest, ProtokitePlaytestRecordingKind.TestVideo };

        /// <summary>Runs once, as the next making of room begins, where a test starts a second game at the same moment.</summary>
        internal static Action BeforeNextMakingRoomForTesting;

        /// <summary>The folder a kind's runs are kept in.</summary>
        internal static string KindFolder(string recordingsFolder, ProtokitePlaytestRecordingKind kind)
            => Path.Combine(recordingsFolder, kind == ProtokitePlaytestRecordingKind.TestVideo ? TestVideosFolderName : PlaytestFolderName);

        /// <summary>The run folders of a kind, as full paths, oldest name first. A folder with no lock file is not a run.</summary>
        internal static List<string> FindRuns(string recordingsFolder, ProtokitePlaytestRecordingKind kind)
        {
            List<string> runs = new List<string>();
            string folder = KindFolder(recordingsFolder, kind);
            try
            {
                if (!Directory.Exists(folder))
                    return runs;
                foreach (string run in Directory.EnumerateDirectories(folder))
                {
                    if (File.Exists(Path.Combine(run, ProtokitePlaytestRecordingRun.LockFileName)))
                        runs.Add(Path.GetFullPath(run));
                }
            }
            catch (Exception ex) when (ProtokitePlaytestRecordingRun.IsFileProblem(ex))
            {
            }
            runs.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            return runs;
        }

        /// <summary>Finishes, keeps or deletes every run whose launch has ended, each held only while it is worked on; the hook runs first, with it held.</summary>
        internal static ProtokitePlaytestEarlierRecordings FinishEndedRuns(string recordingsFolder, Action<string> beforeFinishingEachRun = null)
        {
            ProtokitePlaytestEarlierRecordings found = new ProtokitePlaytestEarlierRecordings();
            foreach (ProtokitePlaytestRecordingKind kind in Kinds)
            {
                foreach (string folder in FindRuns(recordingsFolder, kind))
                {
                    using (ProtokitePlaytestRecordingRun run = ProtokitePlaytestRecordingRun.ClaimEnded(folder, kind))
                    {
                        if (run == null)
                            continue;
                        beforeFinishingEachRun?.Invoke(run.FolderPath);
                        FinishEndedRun(run, found);
                    }
                }
            }
            return found;
        }

        private static void FinishEndedRun(ProtokitePlaytestRecordingRun run, ProtokitePlaytestEarlierRecordings found)
        {
            if (run.Kind == ProtokitePlaytestRecordingKind.Playtest)
            {
                ProtokitePlaytestSavedSession session = ProtokitePlaytestRecordingRun.ReadSession(run.FolderPath, out string couldNotRead);
                if (couldNotRead != null)
                {
                    found.LeftForTheNextLaunch.Add(couldNotRead);
                    return;
                }
                // Nothing to upload it to: deleted as it is, cut off or not, without finishing it first.
                if (session == null)
                {
                    bool heldAVideo = ProtokitePlaytestRecordingRun.VideoFiles(run.FolderPath).Count > 0;
                    if (!run.DeleteEverything())
                        found.LeftForTheNextLaunch.Add($"{run.FolderPath} holds a recording no Protokite session started for, and not all of it could be deleted.");
                    else if (heldAVideo)
                        found.RecordingsDeletedWithNoSession++;
                    else
                        found.EmptyRunsRemoved++;
                    return;
                }
            }

            run.DeleteTemporaryFiles();
            bool leftSomething = false;
            foreach (string video in ProtokitePlaytestRecordingRun.VideoFiles(run.FolderPath))
            {
                if (!video.EndsWith(ProtokitePlaytestVideoRecording.PartSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!FinishCutOffVideo(video, found))
                    leftSomething = true;
            }
            if (leftSomething)
                return;

            if (ProtokitePlaytestRecordingRun.VideoFiles(run.FolderPath).Count == 0)
            {
                if (run.DeleteEverything())
                    found.EmptyRunsRemoved++;
                else
                    found.LeftForTheNextLaunch.Add($"{run.FolderPath} holds no video, and could not be deleted.");
            }
            else if (run.Kind == ProtokitePlaytestRecordingKind.Playtest)
            {
                found.RecordingsWaitingToUpload++;
            }
            else
            {
                found.TestVideosKept++;
            }
        }

        // False when the video is left as it was, for the next launch.
        private static bool FinishCutOffVideo(string unfinishedPath, ProtokitePlaytestEarlierRecordings found)
        {
            string finishedPath = unfinishedPath.Substring(0, unfinishedPath.Length - ProtokitePlaytestVideoRecording.PartSuffix.Length);
            // A finished name is only ever given to a whole file, so an unfinished copy beside it is what a rename left.
            if (File.Exists(finishedPath))
            {
                try
                {
                    File.Delete(unfinishedPath);
                    return true;
                }
                catch (Exception ex) when (ProtokitePlaytestRecordingRun.IsFileProblem(ex))
                {
                    found.LeftForTheNextLaunch.Add($"{unfinishedPath} could not be deleted, though its finished copy is there: {ex.Message}");
                    return false;
                }
            }

            IProtokitePlaytestRecordingFinisher finisher = ProtokitePlaytestRecordingFiles.ForFinishing(finishedPath);
            if (finisher == null)
            {
                found.LeftForTheNextLaunch.Add($"{unfinishedPath} is a kind of recording this package never wrote, so it is left as it was.");
                return false;
            }
            switch (finisher.FinishInterruptedRecording(unfinishedPath, finishedPath, out int framesKept, out string error))
            {
                case ProtokitePlaytestInterruptedRecordingResult.Finished:
                    found.CutOffVideosFinished++;
                    found.FramesKeptInFinishedVideos += framesKept;
                    return true;
                case ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame:
                    return true;
                default:
                    found.LeftForTheNextLaunch.Add(error);
                    return false;
            }
        }

        /// <summary>Deletes ended runs, test videos first and oldest first, until every other run and the wanted room fit the budget.</summary>
        // Room for a test video comes from other test videos only: a recording waiting to upload is never deleted for one.
        // The new run is made, with its reservation, before this is called, so a game starting at the same moment counts it. A run
        // in use, or holding an unfinished video, is never deleted and counts at its reservation (runInUseWithNoReservation if none).
        // freeBytesRecordingsMayTake, when known, is the disk's free space recordings may still fill: the budget is never more than the
        // other runs' files and that. Deleting a run frees what it takes, so that sum is the same before and after.
        internal static ProtokitePlaytestRoomMade MakeRoom(string recordingsFolder, ProtokitePlaytestRecordingRun newRun, long budgetBytes, long wantedBytes,
            long runInUseWithNoReservation, long? freeBytesRecordingsMayTake = null)
        {
            Action hook = BeforeNextMakingRoomForTesting;
            BeforeNextMakingRoomForTesting = null;
            hook?.Invoke();

            ProtokitePlaytestRoomMade room = new ProtokitePlaytestRoomMade();
            List<(string Folder, ProtokitePlaytestRecordingKind Kind, long Bytes)> deletable = new List<(string, ProtokitePlaytestRecordingKind, long)>();
            long used = 0;
            long othersOnDisk = 0;
            foreach (ProtokitePlaytestRecordingKind kind in Kinds)
            {
                foreach (string folder in FindRuns(recordingsFolder, kind))
                {
                    if (newRun != null && string.Equals(folder, newRun.FolderPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    long onDisk = ProtokitePlaytestRecordingRun.BytesOnDisk(folder);
                    othersOnDisk += onDisk;
                    // An unfinished video is the finishing pass's to finish, so its run is never claimed here.
                    bool unfinished = ProtokitePlaytestRecordingRun.HasUnfinishedVideo(folder);
                    bool ended = false;
                    if (!unfinished)
                    {
                        using (ProtokitePlaytestRecordingRun claimed = ProtokitePlaytestRecordingRun.ClaimEnded(folder, kind))
                            ended = claimed != null;
                    }

                    if (ended)
                    {
                        used += onDisk;
                        deletable.Add((folder, kind, onDisk));
                    }
                    else if (!unfinished && ProtokitePlaytestRecordingRun.FinishedVideoPath(folder) != null)
                    {
                        // Held with its video finished (being uploaded, or its launch still running): it grows no more, so it counts at what it takes.
                        used += onDisk;
                    }
                    else
                    {
                        used += Math.Max(onDisk, ProtokitePlaytestRecordingRun.ReadReservedBytes(folder) ?? runInUseWithNoReservation);
                    }
                }
            }

            if (freeBytesRecordingsMayTake.HasValue && othersOnDisk + Math.Max(0L, freeBytesRecordingsMayTake.Value) < budgetBytes)
            {
                budgetBytes = othersOnDisk + Math.Max(0L, freeBytesRecordingsMayTake.Value);
                room.LimitedByTheDisk = true;
            }

            // Test videos go first, then recordings waiting to upload; oldest first within each, as FindRuns lists them.
            deletable.Sort((a, b) => a.Kind != b.Kind
                ? (a.Kind == ProtokitePlaytestRecordingKind.TestVideo ? -1 : 1)
                : string.CompareOrdinal(Path.GetFileName(a.Folder), Path.GetFileName(b.Folder)));
            bool forATestVideo = newRun != null && newRun.Kind == ProtokitePlaytestRecordingKind.TestVideo;
            foreach ((string folder, ProtokitePlaytestRecordingKind kind, long bytes) in deletable)
            {
                if (used + wantedBytes <= budgetBytes)
                    break;
                if (forATestVideo && kind == ProtokitePlaytestRecordingKind.Playtest)
                    break;
                // Claimed again only now: holding every run while deciding would stop other launches finishing theirs.
                using (ProtokitePlaytestRecordingRun run = ProtokitePlaytestRecordingRun.ClaimEnded(folder, kind))
                {
                    // Another launch took it meanwhile; what it holds stays counted.
                    if (run == null)
                        continue;
                    bool deleted = run.DeleteEverything();
                    used -= bytes - (deleted ? 0 : ProtokitePlaytestRecordingRun.BytesOnDisk(folder));
                    if (!deleted)
                        room.CouldNotDelete.Add(folder);
                    else if (kind == ProtokitePlaytestRecordingKind.TestVideo)
                        room.TestVideosDeleted++;
                    else
                        room.WaitingRecordingsDeleted++;
                }
            }

            room.BytesUsedByOtherRuns = used;
            room.BytesLeft = Math.Max(0L, budgetBytes - used);
            return room;
        }
    }
}
