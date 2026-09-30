using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protokite.Playtest
{
    public static partial class ProtokitePlaytest
    {
        /// <summary>Frame blocks a recording's frames wait in between arriving from the graphics card and being encoded.</summary>
        private const int VideoFrameBlocks = ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode + ProtokitePlaytestScreenFrameSource.MostFramesOnTheirWay;

        private const double BytesPerMegabyte = 1024.0 * 1024.0;

        private static ProtokitePlaytestVideoRecording _videoRecording;
        private static bool _videoStartedThisLaunch;

        // This launch's run folder, held for the launch's whole life so no other launch touches its recording.
        private static ProtokitePlaytestRecordingRun _recordingRun;
        private static Task _earlierRecordings;
        private static DateTime _stopWaitingForEarlierRecordingsAt;

        /// <summary>Runs on the finishing thread with each earlier launch's run held, just before it is finished; tests hold it there.</summary>
        internal static Action<string> BeforeFinishingEachEarlierRecordingForTesting;

        /// <summary>How long a recording waits for the earlier launches' recordings to be gone through, when a test sets it; 10 seconds otherwise.</summary>
        internal static TimeSpan? LongestWaitForEarlierRecordingsForTesting;

        /// <summary>Stands in for the screen, so tests record frames of their own without a window.</summary>
        internal static Func<ProtokitePlaytestVideoSettings, ProtokitePlaytestPixelFormat, IProtokitePlaytestFrameSource> VideoFrameSourceForTesting;

        /// <summary>Stands in for the encoder the platform has.</summary>
        internal static Func<IProtokitePlaytestVideoEncoder> VideoEncoderForTesting;

        /// <summary>Stands in for the file a recording is written to, so a test can give it another kind.</summary>
        internal static Func<IProtokitePlaytestRecordingFile> RecordingFileForTesting;

        /// <summary>Where recordings go instead of the game's own folder.</summary>
        internal static string RecordingsFolderForTesting;

        /// <summary>What became of this launch's recording once its file is written, or null until then.</summary>
        internal static ProtokitePlaytestVideoRecordingSummary FinishedVideo { get; private set; }

        /// <summary>Whether this launch's playtest recording is capturing frames now; a test video does not count. Main thread only.</summary>
        public static bool IsRecordingVideo => _videoRecording != null && _videoRecording.IsCapturing;

        /// <summary>The recording while it runs, for tests.</summary>
        internal static ProtokitePlaytestVideoRecording VideoRecordingForTesting => _videoRecording;

        /// <summary>This launch's run folder, for tests; null when no recording started this launch.</summary>
        internal static ProtokitePlaytestRecordingRun RecordingRunForTesting => _recordingRun;

        /// <summary>The folder a launch's recordings are kept in.</summary>
        internal static string RecordingsFolder => RecordingsFolderForTesting ?? Path.Combine(Application.persistentDataPath, "ProtokitePlaytest", "Recordings");

        /// <summary>Finishes, keeps or deletes what earlier launches recorded, on a thread of its own: an hour's cut-off file takes a second or more.</summary>
        internal static void StartFinishingEarlierRecordings()
        {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            // Read here: the folder asks Unity for its data path, which only the main thread may.
            string folder = RecordingsFolder;
            Action<string> beforeEachRun = BeforeFinishingEachEarlierRecordingForTesting;
            _stopWaitingForEarlierRecordingsAt = DateTime.UtcNow + (LongestWaitForEarlierRecordingsForTesting ?? TimeSpan.FromSeconds(10));
            _earlierRecordings = Task.Run(() => FinishEarlierRecordings(folder, beforeEachRun));
#endif
        }

        // Making room counts a run the pass has not finished at its whole reservation, so a recording waits for the pass, a while at most.
        private static bool EarlierRecordingsGoneThrough()
            => _earlierRecordings == null || _earlierRecordings.IsCompleted || DateTime.UtcNow >= _stopWaitingForEarlierRecordingsAt;

        /// <summary>Waits up to the timeout for the earlier launches' recordings to be gone through; true once they are, or when none were started.</summary>
        internal static bool WaitForEarlierRecordingsForTesting(TimeSpan timeout) => _earlierRecordings == null || _earlierRecordings.Wait(timeout);

        private static void FinishEarlierRecordings(string folder, Action<string> beforeEachRun)
        {
            try
            {
                ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(folder, beforeEachRun);
                foreach (string left in found.LeftForTheNextLaunch)
                    Debug.LogWarning(LogPrefix + "A recording an earlier launch left is kept as it is, for the next launch to try again: " + left);
                string said = DescribeEarlierRecordings(found);
                if (said != null)
                    Debug.Log(LogPrefix + said);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + $"The recordings earlier launches left in {folder} could not all be gone through; the next launch tries again. {ex.Message}");
            }
        }

        private static string DescribeEarlierRecordings(ProtokitePlaytestEarlierRecordings found)
        {
            List<string> parts = new List<string>();
            if (found.CutOffVideosFinished > 0)
                parts.Add($"cut off when their game ended and now finished: {found.CutOffVideosFinished}, keeping {found.FramesKeptInFinishedVideos} whole frames");
            if (found.RecordingsWaitingToUpload > 0)
                parts.Add($"kept to be uploaded to their Protokite session: {found.RecordingsWaitingToUpload}");
            if (found.RecordingsDeletedWithNoSession > 0)
                parts.Add($"deleted because no Protokite session started for them: {found.RecordingsDeletedWithNoSession}");
            if (found.TestVideosKept > 0)
                parts.Add($"test videos kept: {found.TestVideosKept}");
            return parts.Count == 0 ? null : "Recordings earlier launches left, " + string.Join("; ", parts) + ".";
        }

        /// <summary>Stops this launch's playtest recording for good, for a game that ends play on its own schedule; its file is uploaded like any finished recording. False when none is capturing. Main thread only.</summary>
        public static bool StopVideoRecording()
        {
            if (!IsRecordingVideo)
                return false;
            _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.StoppedByGame);
            return true;
        }

        /// <summary>
        /// Once a frame, at its end, with the frame's time. Starts the launch's one recording when the playtest config turns video
        /// on, even before sign-in, once earlier launches' recordings are gone through, and captures the frame when the schedule wants it.
        /// </summary>
        internal static void UpdateVideo(double frameSeconds)
        {
            // The launch's recording belongs to the playtest: a test video gives way on the frame the playtest's could start, and the
            // playtest's starts once the test video's file is written.
            bool playtestRecordingDue = _videoRecording == null && !_videoStartedThisLaunch && VideoIsOnInTheLoadedConfig() && EarlierRecordingsGoneThrough();
            if (playtestRecordingDue)
                MakeTheTestVideoGiveWay();
            UpdateTestVideo(frameSeconds);

            if (_videoRecording == null)
            {
                if (playtestRecordingDue && _testVideo == null)
                    StartVideoRecording();
                return;
            }

            if (_videoRecording.IsCapturing && VideoTurnedOffForGood())
                _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.PlaytestStopped);
            ProtokitePlaytestVideoStopReason? stop = _videoRecording.AddFrame(frameSeconds);
            if (stop.HasValue)
                _videoRecording.StopCapturing(stop.Value);
            if (_videoRecording.HasFinishedWriting)
                ReportFinishedVideo();
        }

        /// <summary>The game left for the background or came back: the frame that carries the time away is neither recorded nor measured.</summary>
        internal static void HandleGameLeftOrCameBack()
        {
            _videoRecording?.LeaveOutNextFrame();
            _testVideo?.LeaveOutNextFrame();
            _performanceTimeline?.LeaveOutNextFrame();
        }

        // A config being fetched again (after a Flock restart) is no reason to end the launch's only recording; a loaded config
        // with video off, or a playtest that closed, is.
        private static bool VideoTurnedOffForGood()
            => _playtestNoLongerCollecting || (ConfigIsLoaded() && !_config.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));

        private static bool VideoIsOnInTheLoadedConfig() => FeatureIsOnInTheLoadedConfig(ProtokitePlaytestFeatures.VideoRecording);

        // Asked every frame, so read off the fetched config rather than through Status, which loads the settings asset. The player's
        // answer is the one place video and heavy analytics learn what they may do.
        private static bool FeatureIsOnInTheLoadedConfig(string feature)
            => !_playtestNoLongerCollecting && ConfigIsLoaded() && _config.IsFeatureEnabled(feature)
               && ProtokitePlaytestConsent.AllowsFeature(EffectiveConsent(), feature);

        private static bool ConfigIsLoaded() => _config != null && ConfigStateForRunningFlock() == ProtokitePlaytestConfigState.Loaded;

        private static void StartVideoRecording()
        {
            // One attempt a launch, whatever becomes of it.
            _videoStartedThisLaunch = true;
            ProtokitePlaytestVideoSettings settings = ProtokitePlaytestVideoSettings.From(ProtokitePlaytestSettings.Load());
            long sizeLimit = settings.MaxBytes;
            if (!TryStartRecording(ProtokitePlaytestRecordingKind.Playtest, settings, out ProtokitePlaytestVideoRecording recording, out ProtokitePlaytestRecordingRun run,
                    out string contentType, out RecordingNotStarted notStarted, out string whyNot))
            {
                // The encoder has said why it is missing, once.
                if (notStarted == RecordingNotStarted.NoEncoder)
                    return;
                string line = LogPrefix + "This launch records no playtest video: " + whyNot + " Everything else in the playtest still runs.";
                // No capture is expected in a build that draws nothing; loud where a studio's players should have been recorded.
                if (notStarted == RecordingNotStarted.NoCapture && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    Debug.Log(line);
                else
                    Debug.LogWarning(line);
                return;
            }

            _videoRecording = recording;
            _recordingRun = run;
            // Uploaded as the kind of file it is written as, so a platform writing another kind sends its own.
            _recordingContentType = contentType;
            SaveSessionBesideRecording();
            string cutShort = settings.MaxBytes < sizeLimit
                ? $" (Max Recording Size Mb is {sizeLimit / BytesPerMegabyte:0.#} MB, but Recordings Disk Budget Mb has only this much left)"
                : "";
            Debug.Log(LogPrefix + $"Recording video for the playtest to {_videoRecording.PartPath}, at {_videoRecording.Width}x{_videoRecording.Height} and " +
                $"{settings.FramesPerSecond} frames a second ({settings.Codec}). It stops for good after {settings.MaxSeconds / 60.0:0.#} minutes of play, " +
                $"before the file passes {settings.MaxBytes / BytesPerMegabyte:0.#} MB{cutShort}, or when the game stops it.");
        }

        /// <summary>What stopped a recording from starting.</summary>
        private enum RecordingNotStarted
        {
            NoEncoder,
            NoCapture,
            NoRunOrRoom,
            CouldNotStart
        }

        /// <summary>Sets up a recording of this kind, its run and room included; false, with why and at which step, leaving nothing behind.</summary>
        private static bool TryStartRecording(ProtokitePlaytestRecordingKind kind, ProtokitePlaytestVideoSettings settings, out ProtokitePlaytestVideoRecording recording,
            out ProtokitePlaytestRecordingRun run, out string contentType, out RecordingNotStarted notStarted, out string whyNot)
        {
            recording = null;
            run = null;
            contentType = null;
            notStarted = RecordingNotStarted.NoEncoder;
            whyNot = null;
            IProtokitePlaytestVideoEncoder encoder = VideoEncoderForTesting != null ? VideoEncoderForTesting() : ProtokitePlaytestVideoEncoders.Create(out whyNot);
            if (encoder == null)
            {
                whyNot = whyNot ?? "this build has no video encoder.";
                return false;
            }

            IProtokitePlaytestFrameSource source = VideoFrameSourceForTesting != null
                ? VideoFrameSourceForTesting(settings, encoder.InputPixelFormat)
                : ProtokitePlaytestScreenFrameSource.Create(settings, encoder.InputPixelFormat, VideoFrameBlocks, out whyNot);
            if (source == null)
            {
                encoder.Dispose();
                notStarted = RecordingNotStarted.NoCapture;
                whyNot = whyNot ?? "no frames can be captured.";
                return false;
            }

            IProtokitePlaytestRecordingFile file = RecordingFileForTesting != null ? RecordingFileForTesting() : ProtokitePlaytestRecordingFiles.Create();
            if (!StartRecordingRun(kind, settings, file, out run, out whyNot))
            {
                file.Dispose();
                source.Dispose();
                encoder.Dispose();
                notStarted = RecordingNotStarted.NoRunOrRoom;
                return false;
            }

            recording = ProtokitePlaytestVideoRecording.Start(source, encoder, file, settings, run.VideoPath(file.FileExtension), out whyNot);
            if (recording == null)
            {
                run.DeleteEverything();
                run = null;
                source.Dispose();
                encoder.Dispose();
                notStarted = RecordingNotStarted.CouldNotStart;
                return false;
            }
            contentType = file.ContentType;
            return true;
        }

        /// <summary>The room a recording of this kind reserves when it starts.</summary>
        // A test video's first frame alone can pass what a second at the bitrate comes to, so it reserves at least what any recording starts with.
        private static long RoomToReserve(ProtokitePlaytestRecordingKind kind, ProtokitePlaytestVideoSettings settings, IProtokitePlaytestRecordingFile file)
        {
            long wanted = settings.BytesToMakeRoomFor(file.BytesAddedToEachFrame);
            return kind == ProtokitePlaytestRecordingKind.TestVideo
                ? Math.Min(settings.MaxBytes, Math.Max(wanted, ProtokitePlaytestRecordingsFolder.SmallestRoomForARecording))
                : wanted;
        }

        /// <summary>Makes a run of this kind and room for it in the budget, and cuts the size limit to the room left; false, with why, under a megabyte.</summary>
        private static bool StartRecordingRun(ProtokitePlaytestRecordingKind kind, ProtokitePlaytestVideoSettings settings, IProtokitePlaytestRecordingFile file,
            out ProtokitePlaytestRecordingRun startedRun, out string error)
        {
            startedRun = null;
            string folder = RecordingsFolder;
            bool testVideo = kind == ProtokitePlaytestRecordingKind.TestVideo;
            long wanted = RoomToReserve(kind, settings, file);
            // The run and its reservation come first, so a game starting at the same moment counts this one before making room of its own.
            ProtokitePlaytestRecordingRun run = ProtokitePlaytestRecordingRun.Start(folder, kind, wanted, out error);
            if (run == null)
                return false;

            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(folder, run, settings.DiskBudgetBytes, wanted, settings.MaxBytes);
            string budget = $"Recordings Disk Budget Mb ({settings.DiskBudgetBytes / BytesPerMegabyte:0.#} MB)";
            string forWhat = testVideo ? "a test video" : "this launch's recording";
            if (room.WaitingRecordingsDeleted > 0)
                Debug.LogWarning(LogPrefix + $"Deleted {room.WaitingRecordingsDeleted} recording(s) earlier launches kept to be uploaded, the oldest first, to make room in {budget} for {forWhat}.");
            if (room.TestVideosDeleted > 0)
                Debug.Log(LogPrefix + $"Deleted {room.TestVideosDeleted} test video(s), the oldest first, to make room in {budget} for {forWhat}.");
            foreach (string notDeleted in room.CouldNotDelete)
                Debug.LogWarning(LogPrefix + $"{notDeleted} could not all be deleted to make room; the next launch tries again.");

            if (room.BytesLeft < ProtokitePlaytestRecordingsFolder.SmallestRoomForARecording)
            {
                run.DeleteEverything();
                error = $"the recordings in {folder} take {room.BytesUsedByOtherRuns / BytesPerMegabyte:0.#} MB of {budget}, and " +
                        (testVideo
                            ? "a test video makes room by deleting older test videos only, never a recording waiting to upload."
                            : "none of them can be deleted now, because their games are still running or another program is using them.") +
                        " Raise Recordings Disk Budget Mb in Protokite > Playtest > Settings.";
                return false;
            }

            // A recording may grow into all the room left, a test video only into what it reserved; others count either at what it saved.
            long maxBytes = Math.Min(testVideo ? wanted : settings.MaxBytes, room.BytesLeft);
            if (maxBytes != wanted && !run.SaveReservedBytes(maxBytes, out string saveError))
            {
                maxBytes = Math.Min(maxBytes, wanted);
                Debug.LogWarning(LogPrefix + $"The room {forWhat} may take could not be saved, so it stops at the {maxBytes / BytesPerMegabyte:0.#} MB saved before: {saveError}");
            }
            settings.MaxBytes = maxBytes;
            startedRun = run;
            return true;
        }

        /// <summary>Saves this launch's Protokite session beside its recording, so a later launch uploads the recording to it rather than delete it.</summary>
        private static void SaveSessionBesideRecording()
        {
            // A recording the player took the screen back from is never given a session, or a later launch would send it.
            if (_recordingRun == null || _sessionState != ProtokitePlaytestSessionState.Started
                || !ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent()))
                return;
            string gameVersionId = null;
            _sessionHeaders?.TryGetValue(GameVersionHeader, out gameVersionId);
            ProtokitePlaytestSavedSession session = new ProtokitePlaytestSavedSession
            {
                PlaytestSessionId = _playtestSessionId,
                ProtokiteApiUrl = _sessionApiUrl,
                FlockGameVersionId = gameVersionId
            };
            if (!_recordingRun.SaveSession(session, out string error))
                Debug.LogWarning(LogPrefix + $"Protokite session {_playtestSessionId} could not be saved beside this launch's recording, so a later launch deletes the recording rather than upload it. {error}");
        }

        private static void ReportFinishedVideo()
        {
            ProtokitePlaytestVideoRecordingSummary summary = _videoRecording.Summary();
            _videoRecording = null;
            FinishedVideo = summary;
            string why = ProtokitePlaytestVideoRecordingSummary.Describe(summary.StopReason);
            if (summary.Error != null && summary.FilePath == null)
            {
                Debug.LogWarning(LogPrefix + $"The playtest video could not be written, so no finished file was kept: {summary.Error}. It had stopped because {why}.");
            }
            else if (summary.Error != null)
            {
                Debug.LogWarning(LogPrefix + $"The playtest video could not be written to the end: {summary.Error}. The {summary.FramesWritten} frames written before, " +
                    $"{summary.VideoSeconds:0.0} seconds, are kept in {summary.FilePath}.");
            }
            else if (summary.FilePath == null)
            {
                Debug.Log(LogPrefix + $"The playtest video stopped because {why} before any frame was captured, so no file was kept.");
            }
            else
            {
                Debug.Log(LogPrefix + $"Playtest video saved to {summary.FilePath}: {summary.VideoSeconds:0.0} seconds, {summary.FramesWritten} frames, " +
                    $"{summary.BytesWritten / (1024.0 * 1024.0):0.0} MB. It stopped because {why}. Encoding took {summary.AverageEncodeMs:0.00} ms a frame on average " +
                    $"and {summary.LongestEncodeMs:0.00} ms at most, and one write took at most {summary.LongestWriteMs:0.00} ms. Frames dropped: " +
                    $"{summary.FramesDroppedBecauseEncodingFellBehind} because encoding fell behind, {summary.FramesDroppedBecauseWritingFellBehind} because writing " +
                    $"fell behind, {summary.FramesNotReadyInTime} because earlier frames were still on their way, {summary.FramesLostOnTheGraphicsCard} lost on the " +
                    $"graphics card and {summary.FramesDroppedForWantOfABlock} for want of room.");
            }
            UploadThisLaunchsRecordingWhenReady();
        }

        /// <summary>Stops capturing, so the recordings' threads finish their files while the rest of quitting goes on.</summary>
        private static void StopVideoForQuitting()
        {
            if (IsRecordingVideo)
                _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
            StopTestVideoForQuitting();
        }

        /// <summary>Waits what is left of the quit's time for the files; one not finished by then stays as its ".part" file.</summary>
        private static void WaitForVideoAtQuit(TimeSpan timeLeft)
        {
            DateTime until = DateTime.UtcNow + timeLeft;
            if (_videoRecording != null)
            {
                if (_videoRecording.WaitUntilWritten(timeLeft))
                    ReportFinishedVideo();
                else
                    Debug.LogWarning(LogPrefix + $"The playtest video could not be finished before the game closed; what was recorded stays in {_videoRecording.PartPath}.");
            }
            WaitForTestVideoAtQuit(Remaining(until));
        }

        // At quit nothing more is saved into the run, so a written one is let go now: the Editor stays open after Play Mode ends.
        // A file still being written keeps its run held until a new launch has waited for it.
        private static void LetGoOfRecordingRunOnceWritten()
        {
            if (_videoRecording != null)
                return;
            _recordingRun?.Dispose();
            _recordingRun = null;
        }

        // A second Play with domain reload off is a new launch: the last one's recording is stopped and waited for first.
        private static void ResetVideoForNewLaunch()
        {
            if (_videoRecording != null)
            {
                _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
                _videoRecording.WaitUntilWritten(TimeSpan.FromSeconds(5));
            }
            _videoRecording = null;
            // Let go once the last launch's recording has been waited for, so this launch's pass may finish or keep it.
            _recordingRun?.Dispose();
            _recordingRun = null;
            ResetTestVideoForNewLaunch();
            // The last launch's pass is let finish (5 s at most) before this launch's driver starts its own, then forgotten.
            _earlierRecordings?.Wait(TimeSpan.FromSeconds(5));
            _earlierRecordings = null;
            _videoStartedThisLaunch = false;
            FinishedVideo = null;
        }
    }
}
