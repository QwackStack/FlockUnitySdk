using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protokite.Playtest
{
    public static partial class ProtokitePlaytest
    {
        /// <summary>Frame blocks a recording's frames wait in between arriving from the graphics card and being encoded.</summary>
        private const int VideoFrameBlocks = ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode + ProtokitePlaytestScreenFrameSource.MostFramesOnTheirWay;

        private static ProtokitePlaytestVideoRecording _videoRecording;
        private static bool _videoStartedThisLaunch;

        /// <summary>Stands in for the screen, so tests record frames of their own without a window.</summary>
        internal static Func<ProtokitePlaytestVideoSettings, ProtokitePlaytestPixelFormat, IProtokitePlaytestFrameSource> VideoFrameSourceForTesting;

        /// <summary>Stands in for the encoder the platform has.</summary>
        internal static Func<IProtokitePlaytestVideoEncoder> VideoEncoderForTesting;

        /// <summary>Where recordings go instead of the game's own folder.</summary>
        internal static string RecordingsFolderForTesting;

        /// <summary>What became of this launch's recording once its file is written, or null until then.</summary>
        internal static ProtokitePlaytestVideoRecordingSummary FinishedVideo { get; private set; }

        /// <summary>Whether this launch's recording is capturing frames right now.</summary>
        internal static bool IsRecordingVideo => _videoRecording != null && _videoRecording.IsCapturing;

        /// <summary>The recording while it runs, for tests.</summary>
        internal static ProtokitePlaytestVideoRecording VideoRecordingForTesting => _videoRecording;

        /// <summary>The folder a launch's recordings are kept in.</summary>
        internal static string RecordingsFolder => RecordingsFolderForTesting ?? Path.Combine(Application.persistentDataPath, "ProtokitePlaytest", "Recordings");

        /// <summary>Stops this launch's recording for good and has its file finished. False when none is capturing.</summary>
        internal static bool StopVideoRecording()
        {
            if (!IsRecordingVideo)
                return false;
            _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.StoppedByGame);
            return true;
        }

        /// <summary>
        /// Once a frame, at its end, with the frame's time. Starts the launch's one recording when the playtest config turns video
        /// on, even before sign-in, and captures the frame when the recording's schedule wants it.
        /// </summary>
        internal static void UpdateVideo(double frameSeconds)
        {
            if (_videoRecording == null)
            {
                if (!_videoStartedThisLaunch && VideoIsOnInTheLoadedConfig())
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

        /// <summary>The game left for the background or came back: the frame that carries the time away is not recorded.</summary>
        internal static void HandleGameLeftOrCameBack() => _videoRecording?.LeaveOutNextFrame();

        // A config being fetched again (after a Flock restart) is no reason to end the launch's only recording; a loaded config
        // with video off, or a playtest that closed, is.
        private static bool VideoTurnedOffForGood()
            => _playtestNoLongerCollecting || (ConfigIsLoaded() && !_config.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording));

        // Asked every frame, so read off the fetched config rather than through Status, which loads the settings asset.
        private static bool VideoIsOnInTheLoadedConfig()
            => !_playtestNoLongerCollecting && ConfigIsLoaded() && _config.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording);

        private static bool ConfigIsLoaded() => _config != null && ConfigStateForRunningFlock() == ProtokitePlaytestConfigState.Loaded;

        private static void StartVideoRecording()
        {
            // One attempt a launch, whatever becomes of it.
            _videoStartedThisLaunch = true;
            ProtokitePlaytestVideoSettings settings = ProtokitePlaytestVideoSettings.From(ProtokitePlaytestSettings.Load());

            string whyNot = null;
            IProtokitePlaytestVideoEncoder encoder = VideoEncoderForTesting != null ? VideoEncoderForTesting() : ProtokitePlaytestVideoEncoders.Create(out whyNot);
            // The encoder has said why it is missing, once.
            if (encoder == null)
                return;

            IProtokitePlaytestFrameSource source = VideoFrameSourceForTesting != null
                ? VideoFrameSourceForTesting(settings, encoder.InputPixelFormat)
                : ProtokitePlaytestScreenFrameSource.Create(settings, encoder.InputPixelFormat, VideoFrameBlocks, out whyNot);
            if (source == null)
            {
                encoder.Dispose();
                string line = LogPrefix + "This launch records no playtest video: " + (whyNot ?? "no frames can be captured.") + " Everything else in the playtest still runs.";
                // Expected in a build that draws nothing; loud where a studio's players should have been recorded.
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    Debug.Log(line);
                else
                    Debug.LogWarning(line);
                return;
            }

            IProtokitePlaytestRecordingFile file = ProtokitePlaytestRecordingFiles.Create();
            string path = Path.Combine(RecordingsFolder, "recording-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + file.FileExtension);
            _videoRecording = ProtokitePlaytestVideoRecording.Start(source, encoder, file, settings, path, out string error);
            if (_videoRecording == null)
            {
                source.Dispose();
                encoder.Dispose();
                Debug.LogWarning(LogPrefix + "This launch records no playtest video: " + error + " Everything else in the playtest still runs.");
                return;
            }
            Debug.Log(LogPrefix + $"Recording video for the playtest to {_videoRecording.PartPath}, at {_videoRecording.Width}x{_videoRecording.Height} and " +
                $"{settings.FramesPerSecond} frames a second ({settings.Codec}). It stops for good after {settings.MaxSeconds / 60.0:0.#} minutes of play, " +
                $"before the file passes {settings.MaxBytes / (1024.0 * 1024.0):0.#} MB, or when the game stops it.");
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
        }

        /// <summary>Stops capturing, so the recording's threads finish the file while the rest of quitting goes on.</summary>
        private static void StopVideoForQuitting()
        {
            if (IsRecordingVideo)
                _videoRecording.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
        }

        /// <summary>Waits what is left of the quit's time for the file; one not finished by then stays as its ".part" file.</summary>
        private static void WaitForVideoAtQuit(TimeSpan timeLeft)
        {
            if (_videoRecording == null)
                return;
            if (_videoRecording.WaitUntilWritten(timeLeft))
                ReportFinishedVideo();
            else
                Debug.LogWarning(LogPrefix + $"The playtest video could not be finished before the game closed; what was recorded stays in {_videoRecording.PartPath}.");
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
            _videoStartedThisLaunch = false;
            FinishedVideo = null;
        }
    }
}
