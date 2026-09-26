using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Protokite.Playtest
{
    /// <summary>Why a recording stopped.</summary>
    internal enum ProtokitePlaytestVideoStopReason
    {
        StoppedByGame,
        ReachedLengthLimit,
        ReachedSizeLimit,
        PlaytestStopped,
        GameQuitting,
        CouldNotWrite
    }

    /// <summary>What became of a recording, once its file is written.</summary>
    internal sealed class ProtokitePlaytestVideoRecordingSummary
    {
        /// <summary>The finished file, or null when none was kept: no whole frame was written, or the file could not even be finished.</summary>
        public string FilePath;
        public ProtokitePlaytestVideoStopReason StopReason;
        public int FramesWritten;
        public double VideoSeconds;
        public long BytesWritten;
        public int FramesDroppedBecauseEncodingFellBehind;
        public int FramesDroppedBecauseWritingFellBehind;
        public int FramesNotReadyInTime;
        public int FramesLostOnTheGraphicsCard;
        public int FramesDroppedForWantOfABlock;
        public double AverageEncodeMs;
        public double LongestEncodeMs;
        public double LongestWriteMs;
        /// <summary>Why the recording could not be written to the end, or null. The frames written before can still be kept in <see cref="FilePath"/>.</summary>
        public string Error;

        /// <summary>The reason as the end of a sentence, for example "it reached its length limit".</summary>
        public static string Describe(ProtokitePlaytestVideoStopReason reason)
        {
            switch (reason)
            {
                case ProtokitePlaytestVideoStopReason.StoppedByGame: return "the game stopped it";
                case ProtokitePlaytestVideoStopReason.ReachedLengthLimit: return "it reached its length limit";
                case ProtokitePlaytestVideoStopReason.ReachedSizeLimit: return "it reached its size limit";
                case ProtokitePlaytestVideoStopReason.PlaytestStopped: return "the playtest stopped recording";
                case ProtokitePlaytestVideoStopReason.GameQuitting: return "the game quit";
                case ProtokitePlaytestVideoStopReason.CouldNotWrite: return "its file could not be written";
            }
            return "it stopped";
        }
    }

    /// <summary>
    /// One video recording: frames from a source, captured on the schedule, encoded and written to a file as they come.
    /// The file is written as its path plus ".part" and renamed once finished, so a file under its final name is whole; one
    /// holding no frame is deleted. The main thread only captures and hands frames over. A thread of the recording's own encodes
    /// them in order, and another writes them, so a disk that holds a write for seconds never stops the encoding. At most
    /// <see cref="MostFramesWaitingToEncode"/> frames wait to be encoded and <see cref="MostFramesWaitingToWrite"/> to be written;
    /// past either, frames are dropped before they are encoded, and counted, so memory stays bounded and what is written still
    /// decodes. The file never passes the size limit: a frame counts toward it when it is handed to be written.
    /// </summary>
    internal sealed class ProtokitePlaytestVideoRecording
    {
        internal const int MostFramesWaitingToEncode = 8;

        /// <summary>Encoded frames are small, so many may wait for a disk that has stopped for a moment.</summary>
        internal const int MostFramesWaitingToWrite = 300;

        private const string PartSuffix = ".part";

        private readonly IProtokitePlaytestFrameSource _source;
        private readonly IProtokitePlaytestVideoEncoder _encoder;
        private readonly IProtokitePlaytestRecordingFile _file;
        private readonly ProtokitePlaytestVideoSettings _settings;
        private readonly ProtokitePlaytestFrameSchedule _schedule;
        private readonly string _finishedPath;
        private readonly string _partPath;
        private readonly Action _beforeEachEncodeForTesting;
        private readonly Func<bool> _beforeEachWriteForTesting;
        private readonly BlockingCollection<ProtokitePlaytestCapturedFrame> _toEncode = new BlockingCollection<ProtokitePlaytestCapturedFrame>();
        private readonly BlockingCollection<ProtokitePlaytestEncodedFrame> _toWrite = new BlockingCollection<ProtokitePlaytestEncodedFrame>();
        private readonly List<ProtokitePlaytestCapturedFrame> _arrived = new List<ProtokitePlaytestCapturedFrame>();
        private readonly Thread _encodingThread;
        private readonly Thread _writingThread;

        // Read and written by several threads.
        private int _framesWaitingToEncode;
        private int _framesWaitingToWrite;
        private volatile bool _sizeLimitReached;
        private volatile bool _couldNotWrite;
        private volatile bool _written;

        // The main thread's.
        private bool _capturing = true;
        private ProtokitePlaytestVideoStopReason _stopReason;
        private int _framesDroppedBecauseEncodingFellBehind;
        private int _framesNotReadyInTime;

        // The encoding thread's.
        private long _bytesHandedToFile;
        private int _framesEncoded;
        private int _framesDroppedBecauseWritingFellBehind;
        private double _encodeMsTotal;
        private double _longestEncodeMs;
        private string _encodeError;
        private string _keptPath;
        private int _framesInKeptFile;
        private long _bytesInKeptFile;

        // The writing thread's.
        private double _longestWriteMs;
        private string _writeError;

        /// <summary>
        /// Configures the encoder, opens the file and starts the two threads. Null, with why, when either cannot be set up; the
        /// caller still owns the source and the encoder then. A test can hand hooks that hold the encoding or writing thread
        /// before each frame; the write hook answering false makes that frame's write fail, the way a full disk does.
        /// </summary>
        public static ProtokitePlaytestVideoRecording Start(IProtokitePlaytestFrameSource source, IProtokitePlaytestVideoEncoder encoder,
            IProtokitePlaytestRecordingFile file, ProtokitePlaytestVideoSettings settings, string finishedPath, out string error,
            Action beforeEachEncodeForTesting = null, Func<bool> beforeEachWriteForTesting = null)
        {
            if (!encoder.Configure(settings.EncoderSettings(source.Width, source.Height), out error))
                return null;
            string partPath = finishedPath + PartSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finishedPath)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                error = $"the folder for {finishedPath} could not be made: {ex.Message}";
                return null;
            }
            if (!file.Open(partPath, settings.Codec, source.Width, source.Height, out error))
                return null;
            return new ProtokitePlaytestVideoRecording(source, encoder, file, settings, finishedPath, partPath, beforeEachEncodeForTesting, beforeEachWriteForTesting);
        }

        private ProtokitePlaytestVideoRecording(IProtokitePlaytestFrameSource source, IProtokitePlaytestVideoEncoder encoder, IProtokitePlaytestRecordingFile file,
            ProtokitePlaytestVideoSettings settings, string finishedPath, string partPath, Action beforeEachEncodeForTesting, Func<bool> beforeEachWriteForTesting)
        {
            _source = source;
            _encoder = encoder;
            _file = file;
            _settings = settings;
            _schedule = new ProtokitePlaytestFrameSchedule(settings.FramesPerSecond, settings.MaxSeconds);
            _finishedPath = finishedPath;
            _partPath = partPath;
            _beforeEachEncodeForTesting = beforeEachEncodeForTesting;
            _beforeEachWriteForTesting = beforeEachWriteForTesting;
            _bytesHandedToFile = file.BytesWritten;
            _writingThread = new Thread(WriteLoop) { IsBackground = true, Name = "Protokite Playtest video writer" };
            _encodingThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "Protokite Playtest video encoder",
                Priority = settings.EncoderBelowGamePriority ? ThreadPriority.BelowNormal : ThreadPriority.Normal
            };
            _writingThread.Start();
            _encodingThread.Start();
        }

        public bool IsCapturing => _capturing;

        /// <summary>True once the file is finished, after <see cref="StopCapturing"/>.</summary>
        public bool HasFinishedWriting => _written;

        /// <summary>The file the recording is written to while it runs.</summary>
        public string PartPath => _partPath;

        /// <summary>The encoding thread's priority, which the settings can set below the game's.</summary>
        internal ThreadPriority EncoderThreadPriority => _encodingThread.Priority;

        public int Width => _source.Width;
        public int Height => _source.Height;

        /// <summary>
        /// One frame's time, on the main thread at the end of the frame. Hands over the frames that arrived and asks for this one
        /// when the schedule captures it. Answers why the recording has to stop, when it has to; nothing once capturing has stopped.
        /// </summary>
        public ProtokitePlaytestVideoStopReason? AddFrame(double frameSeconds)
        {
            if (!_capturing)
                return null;

            _source.TakeCapturedFrames(_arrived);
            SendToEncoder(_arrived);

            if (_couldNotWrite)
                return ProtokitePlaytestVideoStopReason.CouldNotWrite;
            if (_sizeLimitReached)
                return ProtokitePlaytestVideoStopReason.ReachedSizeLimit;

            switch (_schedule.AddFrame(frameSeconds, out long timestampMs))
            {
                case ProtokitePlaytestFrameDecision.ReachedLengthLimit:
                    return ProtokitePlaytestVideoStopReason.ReachedLengthLimit;
                case ProtokitePlaytestFrameDecision.Capture:
                    if (_source.IsReadyForAnotherFrame)
                        _source.CaptureFrame(timestampMs);
                    else
                        _framesNotReadyInTime++;
                    break;
            }
            return null;
        }

        /// <summary>The next frame's time is not recorded: the frame that carries time spent away from the game.</summary>
        public void LeaveOutNextFrame() => _schedule.LeaveOutNextFrame();

        /// <summary>Stops capturing for good and has the file finished on the recording's threads. Only the first call counts. Main thread.</summary>
        public void StopCapturing(ProtokitePlaytestVideoStopReason reason)
        {
            if (!_capturing)
                return;
            _capturing = false;
            _stopReason = reason;
            _source.Stop(_arrived);
            SendToEncoder(_arrived);
            _source.Dispose();
            _toEncode.CompleteAdding();
        }

        /// <summary>Waits up to <paramref name="timeout"/> for the file to be finished; true when it is.</summary>
        public bool WaitUntilWritten(TimeSpan timeout) => _encodingThread.Join(timeout) && _written;

        /// <summary>What became of the recording. Meaningful once <see cref="HasFinishedWriting"/> is true.</summary>
        public ProtokitePlaytestVideoRecordingSummary Summary()
        {
            ProtokitePlaytestVideoRecordingSummary summary = new ProtokitePlaytestVideoRecordingSummary
            {
                StopReason = _stopReason,
                FramesDroppedBecauseEncodingFellBehind = _framesDroppedBecauseEncodingFellBehind,
                FramesNotReadyInTime = _framesNotReadyInTime,
                FramesLostOnTheGraphicsCard = _source.FramesLostOnTheGraphicsCard,
                FramesDroppedForWantOfABlock = _source.FramesDroppedForWantOfABlock
            };
            if (!_written)
                return summary;
            summary.FilePath = _keptPath;
            summary.Error = _encodeError == null ? _writeError : _writeError == null ? _encodeError : _encodeError + "; " + _writeError;
            summary.FramesWritten = _keptPath == null ? _file.FramesWritten : _framesInKeptFile;
            summary.BytesWritten = _keptPath == null ? _file.BytesWritten : _bytesInKeptFile;
            summary.FramesDroppedBecauseWritingFellBehind = _framesDroppedBecauseWritingFellBehind;
            if (summary.FramesWritten > 0)
                summary.VideoSeconds = (_file.LastTimestampMs + _settings.FrameDurationMs) / 1000.0;
            if (_framesEncoded > 0)
                summary.AverageEncodeMs = _encodeMsTotal / _framesEncoded;
            summary.LongestEncodeMs = _longestEncodeMs;
            summary.LongestWriteMs = _longestWriteMs;
            return summary;
        }

        private void SendToEncoder(List<ProtokitePlaytestCapturedFrame> frames)
        {
            foreach (ProtokitePlaytestCapturedFrame frame in frames)
            {
                if (Volatile.Read(ref _framesWaitingToEncode) >= MostFramesWaitingToEncode)
                {
                    _framesDroppedBecauseEncodingFellBehind++;
                    _source.ReturnBlock(frame.Pixels);
                    continue;
                }
                Interlocked.Increment(ref _framesWaitingToEncode);
                _toEncode.Add(frame);
            }
            frames.Clear();
        }

        private void EncodeLoop()
        {
            try
            {
                List<ProtokitePlaytestEncodedFrame> encoded = new List<ProtokitePlaytestEncodedFrame>();
                foreach (ProtokitePlaytestCapturedFrame frame in _toEncode.GetConsumingEnumerable())
                {
                    try
                    {
                        EncodeFrame(frame, encoded);
                    }
                    finally
                    {
                        _source.ReturnBlock(frame.Pixels);
                        Interlocked.Decrement(ref _framesWaitingToEncode);
                    }
                }
                if (!_couldNotWrite)
                {
                    encoded.Clear();
                    if (_encoder.Finish(encoded, out string error))
                        HandToFile(encoded);
                    else
                        CouldNotEncode(error);
                }
            }
            catch (Exception ex)
            {
                CouldNotEncode(ex.Message);
            }
            FinishFile();
        }

        private void EncodeFrame(ProtokitePlaytestCapturedFrame frame, List<ProtokitePlaytestEncodedFrame> encoded)
        {
            if (_couldNotWrite)
                return;
            _beforeEachEncodeForTesting?.Invoke();
            // A disk that has stopped for a while leaves frames waiting; past the limit they are dropped before encoding, never
            // after, so everything written still decodes.
            if (Volatile.Read(ref _framesWaitingToWrite) >= MostFramesWaitingToWrite)
            {
                _framesDroppedBecauseWritingFellBehind++;
                return;
            }
            Stopwatch clock = Stopwatch.StartNew();
            encoded.Clear();
            if (!_encoder.Encode(frame.Pixels, frame.TimestampMs, _settings.FrameDurationMs, false, encoded, out string error))
            {
                CouldNotEncode(error);
                return;
            }
            double ms = clock.Elapsed.TotalMilliseconds;
            _encodeMsTotal += ms;
            _longestEncodeMs = Math.Max(_longestEncodeMs, ms);
            _framesEncoded++;
            HandToFile(encoded);
        }

        // The one check of the size limit. Once a frame is refused no later one is written, so the video ends there.
        private void HandToFile(List<ProtokitePlaytestEncodedFrame> encoded)
        {
            foreach (ProtokitePlaytestEncodedFrame frame in encoded)
            {
                long bytes = _file.BytesAddedToEachFrame + frame.Data.Length;
                if (_sizeLimitReached || _bytesHandedToFile + bytes > _settings.MaxBytes)
                {
                    _sizeLimitReached = true;
                    return;
                }
                _bytesHandedToFile += bytes;
                Interlocked.Increment(ref _framesWaitingToWrite);
                _toWrite.Add(frame);
            }
        }

        private void CouldNotEncode(string error)
        {
            _encodeError = "the video could not be encoded: " + error;
            _couldNotWrite = true;
        }

        private void WriteLoop()
        {
            foreach (ProtokitePlaytestEncodedFrame frame in _toWrite.GetConsumingEnumerable())
            {
                if (!_couldNotWrite)
                {
                    // A test's hook stands in for a disk that holds or refuses the write, so it is timed as part of the write.
                    Stopwatch clock = Stopwatch.StartNew();
                    string error = null;
                    bool written;
                    try
                    {
                        written = (_beforeEachWriteForTesting == null || _beforeEachWriteForTesting()) && _file.WriteFrame(frame, out error);
                    }
                    catch (Exception ex)
                    {
                        written = false;
                        error = ex.Message;
                    }
                    _longestWriteMs = Math.Max(_longestWriteMs, clock.Elapsed.TotalMilliseconds);
                    if (!written)
                    {
                        _writeError = $"a write to {_partPath} failed (is the disk full?)" + (string.IsNullOrEmpty(error) ? "" : ": " + error);
                        _couldNotWrite = true;
                    }
                }
                Interlocked.Decrement(ref _framesWaitingToWrite);
            }
        }

        // On the encoding thread, as its last work. Whatever happens in it, the recording is then finished, so nothing waits on it for ever.
        private void FinishFile()
        {
            try
            {
                FinishFileNow();
            }
            catch (Exception ex)
            {
                _writeError = (_writeError == null ? "" : _writeError + "; ") + $"{_partPath} could not be finished: {ex.Message}";
            }
            finally
            {
                _written = true;
            }
        }

        private void FinishFileNow()
        {
            _toWrite.CompleteAdding();
            _writingThread.Join();
            try
            {
                _encoder.Dispose();
            }
            catch (Exception ex)
            {
                _encodeError = _encodeError ?? "the encoder could not be closed: " + ex.Message;
            }

            if (!_file.Close(out string closeError) && !_couldNotWrite)
            {
                _writeError = $"{_partPath} could not be saved: {closeError}";
                _couldNotWrite = true;
            }
            try
            {
                if (!_couldNotWrite)
                {
                    if (_file.FramesWritten == 0)
                    {
                        File.Delete(_partPath);
                    }
                    else
                    {
                        File.Move(_partPath, _finishedPath);
                        _keptPath = _finishedPath;
                        _framesInKeptFile = _file.FramesWritten;
                        _bytesInKeptFile = _file.BytesWritten;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _writeError = $"{_partPath} could not be renamed to {_finishedPath}: {ex.Message}";
                _couldNotWrite = true;
            }

            if (_couldNotWrite)
            {
                // What was recorded before the failure is kept: the file is finished with every whole frame it holds. One that
                // cannot even be finished now stays as it is.
                switch (_file.FinishInterruptedRecording(_partPath, _finishedPath, out int framesKept, out string finishError))
                {
                    case ProtokitePlaytestInterruptedRecordingResult.Finished:
                        _keptPath = _finishedPath;
                        _framesInKeptFile = framesKept;
                        _bytesInKeptFile = new FileInfo(_finishedPath).Length;
                        break;
                    case ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish:
                        _writeError = _writeError == null ? finishError : _writeError + "; " + finishError;
                        break;
                }
            }
        }
    }
}
