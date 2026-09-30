using System;
using System.Globalization;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Where the test video asked for last has got to.</summary>
    internal enum ProtokitePlaytestTestVideoState
    {
        /// <summary>None was asked for since the launch began.</summary>
        None,
        /// <summary>Asked for, and starting at the end of a frame once earlier launches' recordings are gone through.</summary>
        WaitingToStart,
        Recording,
        /// <summary>Stopped, and its file being finished.</summary>
        Finishing,
        /// <summary>Its file is written, or it stopped before a frame was captured: see <see cref="ProtokitePlaytest.FinishedTestVideo"/>.</summary>
        Finished,
        /// <summary>It was never recorded: see <see cref="ProtokitePlaytest.TestVideoProblem"/>.</summary>
        NotRecorded
    }

    public static partial class ProtokitePlaytest
    {
        private static ProtokitePlaytestVideoRecording _testVideo;
        private static ProtokitePlaytestRecordingRun _testVideoRun;
        // Above 0 while a test video of this many seconds waits to start.
        private static double _testVideoSecondsAskedFor;

        /// <summary>What became of the last test video once its file is written, or null.</summary>
        internal static ProtokitePlaytestVideoRecordingSummary FinishedTestVideo { get; private set; }

        /// <summary>Why the last test video asked for was never recorded, or null.</summary>
        internal static string TestVideoProblem { get; private set; }

        /// <summary>The test video while it runs, for tests.</summary>
        internal static ProtokitePlaytestVideoRecording TestVideoForTesting => _testVideo;

        /// <summary>The folder test videos are kept in.</summary>
        internal static string TestVideosFolder => ProtokitePlaytestRecordingsFolder.KindFolder(RecordingsFolder, ProtokitePlaytestRecordingKind.TestVideo);

        /// <summary>The file the test video is being written to, or null when none is recording.</summary>
        internal static string TestVideoPartPath => _testVideo?.PartPath;

        /// <summary>Where the test video asked for last has got to.</summary>
        internal static ProtokitePlaytestTestVideoState TestVideoState
        {
            get
            {
                if (_testVideoSecondsAskedFor > 0.0)
                    return ProtokitePlaytestTestVideoState.WaitingToStart;
                if (_testVideo != null)
                    return _testVideo.IsCapturing ? ProtokitePlaytestTestVideoState.Recording : ProtokitePlaytestTestVideoState.Finishing;
                if (TestVideoProblem != null)
                    return ProtokitePlaytestTestVideoState.NotRecorded;
                return FinishedTestVideo != null ? ProtokitePlaytestTestVideoState.Finished : ProtokitePlaytestTestVideoState.None;
            }
        }

        /// <summary>Asks the running game for a test video of this many seconds, needing no playtest and never uploaded; false, with why, when it cannot be asked for now.</summary>
        // It starts at the end of a frame once earlier launches' recordings are gone through, and gives way to the playtest's own recording.
        internal static bool RecordTestVideo(double seconds, out string whyNot)
        {
            whyNot = null;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0.0)
                whyNot = $"a test video needs a length above 0 seconds, and {seconds.ToString(CultureInfo.InvariantCulture)} was asked for.";
            else if (_testVideo != null || _testVideoSecondsAskedFor > 0.0)
                whyNot = "a test video is already being recorded.";
            else if (_videoRecording != null || (!_videoStartedThisLaunch && VideoIsOnInTheLoadedConfig()))
                whyNot = "this launch records video for its playtest, and a launch records one video at a time.";
            if (whyNot != null)
            {
                Debug.Log(LogPrefix + "No test video is recorded: " + whyNot);
                return false;
            }

            _testVideoSecondsAskedFor = seconds;
            FinishedTestVideo = null;
            TestVideoProblem = null;
            return true;
        }

        // Once a frame, from UpdateVideo: starts a test video asked for, captures its frame, and reports it once its file is written.
        private static void UpdateTestVideo(double frameSeconds)
        {
            if (_testVideo == null)
            {
                if (_testVideoSecondsAskedFor > 0.0 && EarlierRecordingsGoneThrough())
                {
                    double seconds = _testVideoSecondsAskedFor;
                    _testVideoSecondsAskedFor = 0.0;
                    StartTestVideo(seconds);
                }
                return;
            }

            ProtokitePlaytestVideoStopReason? stop = _testVideo.AddFrame(frameSeconds);
            if (stop.HasValue)
                _testVideo.StopCapturing(stop.Value);
            if (_testVideo.HasFinishedWriting)
                ReportFinishedTestVideo();
        }

        private static void StartTestVideo(double secondsAskedFor)
        {
            ProtokitePlaytestVideoSettings settings = ProtokitePlaytestVideoSettings.From(ProtokitePlaytestSettings.Load());
            double lengthLimit = settings.MaxSeconds;
            settings.MaxSeconds = Math.Min(secondsAskedFor, lengthLimit);
            if (!TryStartRecording(ProtokitePlaytestRecordingKind.TestVideo, settings, out ProtokitePlaytestVideoRecording recording, out ProtokitePlaytestRecordingRun run,
                    out _, out _, out string whyNot))
            {
                SayTestVideoNotRecorded(whyNot);
                return;
            }

            _testVideo = recording;
            _testVideoRun = run;
            string cutToTheLimit = secondsAskedFor > lengthLimit ? $" ({secondsAskedFor:0.#} were asked for, and Max Recording Minutes allows no more)" : "";
            Debug.Log(LogPrefix + $"Recording a test video to {_testVideo.PartPath}, at {_testVideo.Width}x{_testVideo.Height} and {settings.FramesPerSecond} " +
                $"frames a second ({settings.Codec}). It stops after {settings.MaxSeconds:0.#} seconds of play{cutToTheLimit}, or before the file passes " +
                $"{settings.MaxBytes / BytesPerMegabyte:0.#} MB. It is never uploaded, and is kept until making room for a later recording deletes it.");
        }

        private static void ReportFinishedTestVideo()
        {
            ProtokitePlaytestVideoRecordingSummary summary = _testVideo.Summary();
            _testVideo = null;
            FinishedTestVideo = summary;
            // Nothing more is saved into a test video's run, so it is let go at once: from here it is an ended run, which making room may delete.
            _testVideoRun?.Dispose();
            _testVideoRun = null;

            string why = ProtokitePlaytestVideoRecordingSummary.Describe(summary.StopReason);
            if (summary.Error != null && summary.FilePath == null)
                Debug.LogWarning(LogPrefix + $"The test video could not be written, so no finished file was kept: {summary.Error}. It had stopped because {why}.");
            else if (summary.Error != null)
                Debug.LogWarning(LogPrefix + $"The test video could not be written to the end: {summary.Error}. The {summary.FramesWritten} frames written before, " +
                    $"{summary.VideoSeconds:0.0} seconds, are kept in {summary.FilePath}.");
            else if (summary.FilePath == null)
                Debug.Log(LogPrefix + $"The test video stopped because {why} before any frame was captured, so no file was kept.");
            else
                Debug.Log(LogPrefix + $"Test video saved to {summary.FilePath}: {summary.VideoSeconds:0.0} seconds, {summary.FramesWritten} frames, " +
                    $"{summary.BytesWritten / BytesPerMegabyte:0.0} MB. It stopped because {why}.");
        }

        // The launch's one recording at a time belongs to the playtest: a test video waiting to start is dropped, and one recording stops.
        private static void MakeTheTestVideoGiveWay()
        {
            if (_testVideoSecondsAskedFor > 0.0)
            {
                _testVideoSecondsAskedFor = 0.0;
                SayTestVideoNotRecorded("this launch's playtest records video, and a launch records one video at a time.");
            }
            if (_testVideo != null && _testVideo.IsCapturing)
                _testVideo.StopCapturing(ProtokitePlaytestVideoStopReason.PlaytestRecordingStarts);
        }

        private static void SayTestVideoNotRecorded(string whyNot)
        {
            TestVideoProblem = whyNot;
            Debug.LogWarning(LogPrefix + "No test video is recorded: " + whyNot);
        }

        private static void StopTestVideoForQuitting()
        {
            if (_testVideoSecondsAskedFor > 0.0)
            {
                _testVideoSecondsAskedFor = 0.0;
                SayTestVideoNotRecorded("the game closed before it started.");
            }
            if (_testVideo != null && _testVideo.IsCapturing)
                _testVideo.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
        }

        // One not finished in time stays as its ".part" file, and its run held, until the next launch finishes it.
        private static void WaitForTestVideoAtQuit(TimeSpan timeLeft)
        {
            if (_testVideo == null)
                return;
            if (_testVideo.WaitUntilWritten(timeLeft))
                ReportFinishedTestVideo();
            else
                Debug.LogWarning(LogPrefix + $"The test video could not be finished before the game closed; what was recorded stays in {_testVideo.PartPath}, and a later launch finishes it.");
        }

        // A second Play with domain reload off is a new launch: the last one's test video is stopped and waited for first.
        private static void ResetTestVideoForNewLaunch()
        {
            if (_testVideo != null)
            {
                _testVideo.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
                _testVideo.WaitUntilWritten(TimeSpan.FromSeconds(5));
            }
            _testVideo = null;
            _testVideoRun?.Dispose();
            _testVideoRun = null;
            _testVideoSecondsAskedFor = 0.0;
            FinishedTestVideo = null;
            TestVideoProblem = null;
        }
    }
}
