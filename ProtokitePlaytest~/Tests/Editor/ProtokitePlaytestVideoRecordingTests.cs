using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestVideoRecordingTests
    {
        private const double SixtyFps = 1.0 / 60.0;
        private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(20);
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "protokite-recording-tests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private string FinishedPath => Path.Combine(_folder, "recording.mp4");

        private static ProtokitePlaytestVideoSettings Settings(Action<ProtokitePlaytestVideoSettings> change = null)
        {
            ProtokitePlaytestVideoSettings settings = new ProtokitePlaytestVideoSettings { FramesPerSecond = 15 };
            change?.Invoke(settings);
            return settings;
        }

        private ProtokitePlaytestVideoRecording Start(FakeFrameSource source, IProtokitePlaytestVideoEncoder encoder, ProtokitePlaytestVideoSettings settings,
            Action beforeEachEncode = null, Func<bool> beforeEachWrite = null)
        {
            ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, encoder, new ProtokitePlaytestMp4File(), settings,
                FinishedPath, out string error, beforeEachEncode, beforeEachWrite);
            Assert.IsNotNull(recording, error);
            // Frames are captured once the encoder has started, on the recording's own thread; a test counting them waits for it.
            Assert.IsTrue(recording.WaitUntilTheEncoderHasStartedForTesting(TimeSpan.FromSeconds(20)), "The encoder started");
            return recording;
        }

        // Frames of the game until the recording asks to stop or the frames run out; answers why it stopped. Paced as a game's
        // frames are, each a little apart, so the encoder has had each frame before the next: a test feeding thirty in a row faster
        // than any game draws fills the encoder's queue, which only the test of falling behind wants.
        private static ProtokitePlaytestVideoStopReason? Play(ProtokitePlaytestVideoRecording recording, int frames, double frameSeconds = SixtyFps,
            FakeFrameSource pacedBy = null)
        {
            for (int i = 0; i < frames; i++)
            {
                ProtokitePlaytestVideoStopReason? stop = recording.AddFrame(frameSeconds);
                if (stop.HasValue)
                {
                    recording.StopCapturing(stop.Value);
                    return stop;
                }
                pacedBy?.WaitForTheEncoderToCatchUp();
            }
            return null;
        }

        private static ProtokitePlaytestVideoRecordingSummary StopAndWait(ProtokitePlaytestVideoRecording recording)
        {
            recording.StopCapturing(ProtokitePlaytestVideoStopReason.StoppedByGame);
            Assert.IsTrue(recording.WaitUntilWritten(Plenty), "The file is finished");
            Assert.IsTrue(recording.HasFinishedWriting);
            return recording.Summary();
        }

        // Capturing

        [Test]
        public void FramesAreCapturedAtTheRecordingsRateAndWrittenInOrder()
        {
            FakeFrameSource source = new FakeFrameSource();
            FakeH264Encoder encoder = new FakeH264Encoder();
            ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings());
            Assert.IsTrue(File.Exists(FinishedPath + ".part"), "Written as a .part file while it runs");
            Assert.IsNull(Play(recording, 120, pacedBy: source), "Two seconds of a 60 fps game");
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);

            Assert.AreEqual(30, source.Asked.Count, "15 frames a second");
            Assert.AreEqual(0, source.Asked[0]);
            Assert.AreEqual(67, source.Asked[1], "Every fourth 60 fps frame");
            Assert.IsTrue(source.Stopped && source.Disposed, "The source is stopped and let go");
            Assert.AreEqual(30, source.BlocksReturned, "Every frame's block is handed back");
            Assert.IsTrue(encoder.Finished && encoder.Disposed, "The encoder is finished and let go");
            Assert.AreEqual(64, encoder.Configured.Width, "Configured at the source's size");
            Assert.AreEqual(48, encoder.Configured.Height);
            CollectionAssert.AreEqual(Enumerable.Repeat(67L, 30), encoder.DurationsMs, "Each frame lasts the recording's frame time");

            Assert.AreEqual(FinishedPath, summary.FilePath);
            Assert.IsFalse(File.Exists(FinishedPath + ".part"), "Renamed once finished");
            Assert.IsNull(summary.Error);
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.StoppedByGame, summary.StopReason);
            Assert.AreEqual(30, summary.FramesWritten);
            Assert.AreEqual((source.Asked.Last() + 67) / 1000.0, summary.VideoSeconds, 1e-9);
            Assert.AreEqual("the test's own encoder", summary.EncodedBy, "The summary names what encoded the video");
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(FinishedPath, out Mp4FileRead read));
            Assert.AreEqual(30, read.Frames.Count);
            CollectionAssert.AreEqual(source.Asked, read.Frames.Select(frame => frame.TimestampMs), "Shown at the times they were captured");
            Assert.AreEqual(source.Asked.Last() + 67, read.DurationMs, "Its length stamped in on close");
            Assert.AreEqual("avc1", read.SampleEntry);
        }

        [Test]
        public void EveryFramesBlockGoesBackOnceItIsEncoded()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings());
            Play(recording, 20);
            Assert.AreEqual(5, source.Asked.Count, "Precondition: fewer frames than may wait, so none is dropped");
            StopAndWait(recording);
            Assert.AreEqual(5, source.BlocksReturned, "Each block goes back to be filled again");
        }

        [Test]
        public void AFileThatThrowsWhenClosedStillFinishesTheRecording()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, new FakeH264Encoder(), new FileThatThrowsWhenClosed(),
                Settings(), FinishedPath, out string error);
            Assert.IsNotNull(recording, error);
            Play(recording, 20);
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            StringAssert.Contains("the disk went away", summary.Error, "The failure is reported, and nothing waits for ever");
        }

        private sealed class FileThatThrowsWhenClosed : IProtokitePlaytestRecordingFile
        {
            private readonly FakeRecordingFile _inner = new FakeRecordingFile();
            public string ContentType => _inner.ContentType;
            public string FileExtension => _inner.FileExtension;
            public int BytesAddedToEachFrame => 0;
            public long BytesFor(ProtokitePlaytestEncodedFrame frame) => _inner.BytesFor(frame);
            public long BytesWritten => _inner.BytesWritten;
            public int FramesWritten => _inner.FramesWritten;
            public long LastTimestampMs => _inner.LastTimestampMs;
            public bool Open(string path, int width, int height, long frameDurationMs, out string error) => _inner.Open(path, width, height, frameDurationMs, out error);
            public bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error) => _inner.WriteFrame(frame, out error);
            public bool Close(out string error) => throw new IOException("the disk went away");
            public ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error)
                => _inner.FinishInterruptedRecording(unfinishedPath, finishedPath, out framesKept, out error);
            public void Dispose() { }
        }

        [Test]
        public void FramesStillOnTheirWayWhenItStopsAreWritten()
        {
            FakeFrameSource source = new FakeFrameSource { HoldFrames = true };
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings());
            Play(recording, 20);
            Assert.AreEqual(5, source.Asked.Count, "Precondition: frames were asked for and none has arrived");
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(5, summary.FramesWritten, "The source is waited for when the recording stops");
        }

        [Test]
        public void ACaptureTimeThatFindsTheSourceFullIsCounted()
        {
            FakeFrameSource source = new FakeFrameSource { Ready = false };
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings());
            Play(recording, 60, pacedBy: source);
            source.Ready = true;
            Play(recording, 60, pacedBy: source);
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(15, summary.FramesNotReadyInTime, "A second's capture times passed while the source was full");
            Assert.AreEqual(15, summary.FramesWritten, "The next second was recorded");
        }

        [Test]
        public void TheFrameCarryingTimeAwayIsLeftOut()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings());
            Play(recording, 60);
            recording.LeaveOutNextFrame();
            recording.AddFrame(300.0);
            Play(recording, 1);
            StopAndWait(recording);
            Assert.AreEqual(1000, source.Asked.Last(), "The frame after the five minutes away follows the second before it");
        }

        // Going to the background

        // The frames in the unfinished file as it is on disk now: what a game Android ended now would keep.
        private int FramesOnDiskNow()
        {
            string copy = Path.Combine(_folder, "as-it-is-now.mp4");
            using (FileStream part = new FileStream(FinishedPath + ".part", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (FileStream to = File.Create(copy))
                part.CopyTo(to);
            ProtokitePlaytestMp4TestFiles.Read(copy, out Mp4FileRead read);
            return read.Frames.Count;
        }

        [Test]
        public void GoingToTheBackgroundWritesOutTheFramesOnTheirWayAndThoseTheEncoderHolds()
        {
            FakeFrameSource source = new FakeFrameSource { HoldFrames = true };
            FakeH264Encoder encoder = new FakeH264Encoder { HoldsEveryFrame = true };
            ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings());
            Play(recording, 20);
            Assert.AreEqual(5, source.Asked.Count, "Precondition: five frames asked for, none arrived yet");
            Assert.AreEqual(0, FramesOnDiskNow(), "Precondition: nothing on disk while the frames are on their way and in the encoder");

            recording.WriteOutEverythingHeld();
            Assert.IsTrue(SpinWait.SpinUntil(() => FramesOnDiskNow() == 5, Plenty), $"Every frame captured before the game left is on disk while it is away ({FramesOnDiskNow()} of 5)");
            Assert.IsTrue(recording.IsCapturing, "The recording goes on when the game comes back");
            lock (encoder.Calls)
                CollectionAssert.AreEqual(new[] { "encode 0", "encode 67", "encode 133", "encode 200", "encode 267", "let go" }, encoder.Calls,
                    "The frames on their way are encoded before the encoder lets go");
            StopAndWait(recording);
        }

        [Test]
        public void ComingBackGoesOnInTheSameFileFromAKeyframe()
        {
            FakeFrameSource source = new FakeFrameSource();
            FakeH264Encoder encoder = new FakeH264Encoder();
            ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings());
            Play(recording, 60, pacedBy: source);
            recording.WriteOutEverythingHeld();
            recording.LeaveOutNextFrame();
            recording.AddFrame(300.0);
            Play(recording, 60, pacedBy: source);
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);

            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(FinishedPath, out Mp4FileRead read), read.Problem);
            Assert.AreEqual(30, read.Frames.Count, "One file, a second before and a second after");
            Assert.AreEqual(1, encoder.TimesLetGo);
            Assert.IsTrue(read.Frames[15].IsKeyframe, "The first frame after the game came back starts a new stream");
            Assert.IsFalse(read.Frames[14].IsKeyframe, "Control: the frame before it does not");
            Assert.Less(read.Frames[15].TimestampMs - read.Frames[14].TimestampMs, 200, "The five minutes away are not in the video");
            Assert.AreEqual(1, summary.TimesInTheBackground);
            Assert.IsNull(summary.Error);
            StringAssert.Contains("The game went to the background 1 time(s)", summary.DescribeTimesInTheBackground());
        }

        [Test]
        public void GoingToTheBackgroundAfterCapturingStoppedDoesNothing()
        {
            FakeH264Encoder encoder = new FakeH264Encoder();
            ProtokitePlaytestVideoRecording recording = Start(new FakeFrameSource(), encoder, Settings());
            Play(recording, 20);
            recording.StopCapturing(ProtokitePlaytestVideoStopReason.StoppedByGame);
            Assert.DoesNotThrow(() => recording.WriteOutEverythingHeld(), "Nothing more is handed to a recording that is finishing");
            Assert.IsTrue(recording.WaitUntilWritten(Plenty));
            Assert.AreEqual(0, encoder.TimesLetGo);
            Assert.AreEqual(0, recording.Summary().TimesInTheBackground);
        }

        [Test]
        public void GoingToTheBackgroundOftenTakesNoRoomFromTheFrames()
        {
            FakeFrameSource source = new FakeFrameSource();
            FakeH264Encoder encoder = new FakeH264Encoder();
            ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings());
            // More times away than frames may wait to be encoded, so a time away held as a waiting frame would drop frames.
            for (int i = 0; i < 3 * ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode; i++)
            {
                Play(recording, 4, pacedBy: source);
                recording.WriteOutEverythingHeld();
            }
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(24, source.Asked.Count, "Precondition: a frame asked for between each time away");
            Assert.AreEqual(0, summary.FramesDroppedBecauseEncodingFellBehind);
            Assert.AreEqual(24, summary.FramesWritten);
            Assert.AreEqual(24, source.BlocksReturned, "A block goes back for each frame, and none for a time away");
            Assert.AreEqual(24, encoder.TimesLetGo);
        }

        [Test]
        public void FramesEncodedBeforeAnEncodingFailureAreStillWritten()
        {
            FakeFrameSource source = new FakeFrameSource();
            FakeH264Encoder encoder = new FakeH264Encoder { FailAtFrame = 5 };
            using (ManualResetEventSlim letWrite = new ManualResetEventSlim(false))
            {
                ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings(), beforeEachWrite: () =>
                {
                    letWrite.Wait(Plenty);
                    return true;
                });
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.CouldNotWrite, Play(recording, 40, pacedBy: source),
                    "Precondition: the sixth frame failed to encode while the five before it waited to be written");
                letWrite.Set();
                ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
                Assert.AreEqual(5, summary.FramesWritten, "Every frame encoded before the failure is in the file");
                StringAssert.Contains("the encoder refused the frame", summary.Error);
            }
        }

        [Test]
        public void AnEncoderThatCannotLetGoEndsTheRecordingKeepingWhatWasWritten()
        {
            FakeFrameSource source = new FakeFrameSource();
            FakeH264Encoder encoder = new FakeH264Encoder { RefuseToLetGo = "the phone's encoder would not end its stream" };
            ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings());
            Play(recording, 60, pacedBy: source);
            recording.WriteOutEverythingHeld();
            Assert.IsTrue(SpinWait.SpinUntil(() => recording.AddFrame(SixtyFps) == ProtokitePlaytestVideoStopReason.CouldNotWrite, Plenty), "The recording stops");
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            StringAssert.Contains("the phone's encoder would not end its stream", summary.Error);
            Assert.AreEqual(FinishedPath, summary.FilePath, "What was written before is kept");
            Assert.AreEqual(15, summary.FramesWritten);
        }

        [Test]
        public void TheSourcesLossesAreReported()
        {
            FakeFrameSource source = new FakeFrameSource { FramesLostOnTheGraphicsCard = 2, FramesDroppedForWantOfABlock = 3 };
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings());
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(2, summary.FramesLostOnTheGraphicsCard);
            Assert.AreEqual(3, summary.FramesDroppedForWantOfABlock);
        }

        [Test]
        public void TheEncodingThreadGivesWayToTheGame()
        {
            FakeFrameSource source = new FakeFrameSource();
            ThreadPriority? encodingPriority = null;
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(), () => encodingPriority = Thread.CurrentThread.Priority);
            Play(recording, 4, pacedBy: source);
            StopAndWait(recording);
            Assert.AreEqual(ThreadPriority.BelowNormal, encodingPriority, "A busy processor runs the game first");
        }

        // Limits

        [Test]
        public void TheLengthLimitStopsItForGood()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(s => s.MaxSeconds = 1.0));
            // Paced like a game's frames: unpaced, a loaded machine drops some and the count below is short (12 of 15, measured).
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.ReachedLengthLimit, Play(recording, 600, pacedBy: source));
            Assert.IsFalse(recording.IsCapturing);
            Assert.IsNull(recording.AddFrame(SixtyFps), "Nothing is captured after it stopped");
            Assert.IsTrue(recording.WaitUntilWritten(Plenty));
            ProtokitePlaytestVideoRecordingSummary summary = recording.Summary();
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.ReachedLengthLimit, summary.StopReason);
            Assert.AreEqual(15, summary.FramesWritten, "One second at 15 frames a second");
            Assert.Less(source.Asked.Last(), 1000);
        }

        [Test]
        public void TheSizeLimitIsNeverPassed()
        {
            FakeFrameSource source = new FakeFrameSource();
            // The file's type, the movie's header the first frame carries, then room for ten frames of 40 bytes and their fragment headers, and a little more.
            long fileType = ProtokitePlaytestMp4TestFiles.MakeVideoBytes(Path.GetTempPath(), 0, 0, false).Length;
            long movieHeader = new ProtokitePlaytestMp4File().BytesFor(ProtokitePlaytestMp4TestFiles.MakeEncodedFrame(0, 40)) - ProtokitePlaytestMp4File.FrameHeaderBytes - 40;
            long limit = fileType + movieHeader + 10 * (ProtokitePlaytestMp4File.FrameHeaderBytes + 40) + 30;
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(s => s.MaxBytes = limit));
            ProtokitePlaytestVideoStopReason? stop = null;
            for (int i = 0; i < 600 && stop == null; i++)
            {
                stop = recording.AddFrame(SixtyFps);
                // The encoding thread decides; give it the time a frame would.
                Thread.Sleep(2);
            }
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.ReachedSizeLimit, stop);
            recording.StopCapturing(stop.Value);
            Assert.IsTrue(recording.WaitUntilWritten(Plenty));
            ProtokitePlaytestVideoRecordingSummary summary = recording.Summary();
            Assert.AreEqual(10, summary.FramesWritten, "Every frame that fits, and none after the first that does not");
            Assert.LessOrEqual(new FileInfo(FinishedPath).Length, limit);
            Assert.Greater(new FileInfo(FinishedPath).Length, limit - ProtokitePlaytestMp4File.FrameHeaderBytes - 40, "And no frame that fitted was left out");
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(FinishedPath, out Mp4FileRead read));
            Assert.AreEqual(10, read.Frames.Count);
        }

        // Falling behind

        [Test]
        public void FramesAreDroppedBeforeEncodingWhenEncodingFallsBehind()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdEncoding = new ManualResetEventSlim(false);
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(), () => holdEncoding.Wait(Plenty));
            Play(recording, 4 * 30);
            Assert.AreEqual(30, source.Asked.Count, "Precondition: 30 frames captured while the encoder is held");
            holdEncoding.Set();
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);

            Assert.AreEqual(ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode, summary.FramesWritten, "As many as may wait, and no more");
            Assert.AreEqual(30 - ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode, summary.FramesDroppedBecauseEncodingFellBehind);
            Assert.AreEqual(30, source.BlocksReturned, "A dropped frame's block goes back too");
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(FinishedPath, out Mp4FileRead read), "What was written still reads");
        }

        [Test]
        public void FramesAreDroppedBeforeEncodingWhenWritingFallsBehind()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdWriting = new ManualResetEventSlim(false);
            int frames = ProtokitePlaytestVideoRecording.MostFramesWaitingToWrite + 20;
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(s => s.FramesPerSecond = 60),
                beforeEachWrite: () => holdWriting.Wait(Plenty));
            for (int i = 0; i < frames; i++)
            {
                recording.AddFrame(SixtyFps);
                // Frames go to the encoder one at a time, as a game's frames would, so the queue in front of it never fills.
                source.WaitForTheEncoderToCatchUp(1000);
            }
            holdWriting.Set();
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);

            Assert.AreEqual(0, summary.FramesDroppedBecauseEncodingFellBehind, "Precondition: the encoder kept up");
            // One frame is taken off the queue to be written before the hold, so the queue holds the limit behind it.
            Assert.AreEqual(ProtokitePlaytestVideoRecording.MostFramesWaitingToWrite, summary.FramesWritten, "As many as may wait to be written");
            Assert.AreEqual(source.Asked.Count - ProtokitePlaytestVideoRecording.MostFramesWaitingToWrite, summary.FramesDroppedBecauseWritingFellBehind);
        }

        // Failures

        [Test]
        public void AFailedWriteStopsItAndKeepsTheFramesWrittenBefore()
        {
            FakeFrameSource source = new FakeFrameSource();
            int writes = 0;
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(), beforeEachWrite: () => Interlocked.Increment(ref writes) <= 5);
            ProtokitePlaytestVideoStopReason? stop = null;
            for (int i = 0; i < 600 && stop == null; i++)
            {
                stop = recording.AddFrame(SixtyFps);
                Thread.Sleep(2);
            }
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.CouldNotWrite, stop);
            recording.StopCapturing(stop.Value);
            Assert.IsTrue(recording.WaitUntilWritten(Plenty));
            ProtokitePlaytestVideoRecordingSummary summary = recording.Summary();
            StringAssert.Contains("is the disk full", summary.Error);
            Assert.AreEqual(FinishedPath, summary.FilePath, "The frames before are kept");
            Assert.AreEqual(5, summary.FramesWritten);
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(FinishedPath, out Mp4FileRead read));
            Assert.AreEqual(5, read.Frames.Count);
            Assert.IsFalse(File.Exists(FinishedPath + ".part"));
        }

        [Test]
        public void AFailedEncodeStopsItAndKeepsTheFramesEncodedBefore()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder { FailAtFrame = 3 }, Settings());
            ProtokitePlaytestVideoStopReason? stop = null;
            for (int i = 0; i < 600 && stop == null; i++)
            {
                stop = recording.AddFrame(SixtyFps);
                Thread.Sleep(2);
            }
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.CouldNotWrite, stop);
            recording.StopCapturing(stop.Value);
            Assert.IsTrue(recording.WaitUntilWritten(Plenty));
            ProtokitePlaytestVideoRecordingSummary summary = recording.Summary();
            StringAssert.Contains("the encoder refused the frame", summary.Error);
            Assert.AreEqual(3, summary.FramesWritten);
            Assert.AreEqual(FinishedPath, summary.FilePath);
        }

        [Test]
        public void NothingIsCapturedUntilTheEncoderHasStartedAndTheVideoStartsThen()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdTheStart = new ManualResetEventSlim(false);
            ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, new SlowToStartEncoder(holdTheStart), new ProtokitePlaytestMp4File(),
                Settings(), FinishedPath, out string error);
            Assert.IsNotNull(recording, error);
            Play(recording, 60);
            Assert.IsFalse(recording.EncoderHasStarted, "Precondition: the encoder is still starting");
            CollectionAssert.IsEmpty(source.Asked, "No frame is captured while the encoder starts, so none is dropped for want of room");
            Assert.IsTrue(recording.IsCapturing, "Still recording: a game can stop it");

            holdTheStart.Set();
            Assert.IsTrue(recording.WaitUntilTheEncoderHasStartedForTesting(Plenty));
            Play(recording, 60, pacedBy: source);
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(0, source.Asked[0], "The video starts at the first frame after the encoder started");
            Assert.AreEqual(15, summary.FramesWritten, "A second of play once started, none dropped");
            Assert.AreEqual(0, summary.FramesDroppedBecauseEncodingFellBehind);
        }

        [Test]
        public void AnEncoderThatCannotStartEndsTheRecordingWithWhy()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, new FakeH264Encoder { RefuseToStart = "the graphics card's encoder is busy" },
                new ProtokitePlaytestMp4File(), Settings(), FinishedPath, out string error);
            Assert.IsNotNull(recording, error);
            ProtokitePlaytestVideoStopReason? stop = null;
            for (int i = 0; i < 600 && stop == null; i++)
            {
                stop = recording.AddFrame(SixtyFps);
                Thread.Sleep(2);
            }
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.CouldNotWrite, stop);
            recording.StopCapturing(stop.Value);
            Assert.IsTrue(recording.WaitUntilWritten(Plenty));
            ProtokitePlaytestVideoRecordingSummary summary = recording.Summary();
            StringAssert.Contains("the graphics card's encoder is busy", summary.Error);
            Assert.IsNull(summary.FilePath, "Nothing was recorded, so nothing is kept");
            Assert.IsFalse(File.Exists(FinishedPath + ".part"));
            CollectionAssert.IsEmpty(source.Asked);
        }

        /// <summary>An encoder whose start waits for the test, as a graphics card's can take seconds.</summary>
        private sealed class SlowToStartEncoder : IProtokitePlaytestVideoEncoder
        {
            private readonly FakeH264Encoder _inner = new FakeH264Encoder();
            private readonly ManualResetEventSlim _hold;

            public SlowToStartEncoder(ManualResetEventSlim hold) => _hold = hold;

            public ProtokitePlaytestPixelFormat InputPixelFormat => _inner.InputPixelFormat;
            public string Description => _inner.Description;
            public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error) => _inner.Configure(settings, out error);

            public bool Start(out string error)
            {
                _hold.Wait(TimeSpan.FromSeconds(20));
                return _inner.Start(out error);
            }

            public bool Encode(byte[] pixels, long timestampMs, long durationMs, List<ProtokitePlaytestEncodedFrame> output, out string error)
                => _inner.Encode(pixels, timestampMs, durationMs, output, out error);

            public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error) => _inner.Finish(output, out error);
            public bool HandOverEverythingAndLetGo(List<ProtokitePlaytestEncodedFrame> output, out string error) => _inner.HandOverEverythingAndLetGo(output, out error);
            public void Dispose() => _inner.Dispose();
        }

        [Test]
        public void ARecordingThatCapturedNothingLeavesNoFile()
        {
            ProtokitePlaytestVideoRecording recording = Start(new FakeFrameSource(), new FakeH264Encoder(), Settings());
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.IsNull(summary.FilePath);
            Assert.IsNull(summary.Error);
            Assert.IsFalse(File.Exists(FinishedPath) || File.Exists(FinishedPath + ".part"), "Nothing is left on disk");
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void AnEncoderThatCannotBeConfiguredStartsNothing()
        {
            // H.264 codes even sizes only.
            FakeFrameSource source = new FakeFrameSource(63, 48);
            using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder())
            {
                ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, encoder, new ProtokitePlaytestMp4File(), Settings(), FinishedPath, out string error);
                Assert.IsNull(recording);
                StringAssert.Contains("even", error);
                Assert.IsFalse(File.Exists(FinishedPath + ".part"), "No file is opened for a recording that cannot start");
            }
        }
#endif

        [Test]
        public void WaitingForTheFileIsBounded()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdWriting = new ManualResetEventSlim(false);
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeH264Encoder(), Settings(), beforeEachWrite: () => holdWriting.Wait(Plenty));
            Play(recording, 20);
            recording.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsFalse(recording.WaitUntilWritten(TimeSpan.FromMilliseconds(300)), "A disk that holds the write is not waited for past the bound");
            Assert.Less(clock.ElapsedMilliseconds, 3000);
            Assert.IsTrue(File.Exists(FinishedPath + ".part"), "What was recorded stays in the .part file");
            holdWriting.Set();
            Assert.IsTrue(recording.WaitUntilWritten(Plenty), "And it is finished once the disk lets go");
        }

#if UNITY_EDITOR_WIN
        // A real encoder, end to end: started on the recording's own encoding thread, and the file read back by Windows.

        [Test]
        public void ARealEncoderRecordsAFileWindowsDecodesFrameForFrame()
        {
            RealEncoding.AssumeAGraphicsCardEncoder();
            FakeFrameSource source = new FakeFrameSource(RealEncoding.Width, RealEncoding.Height);
            ProtokitePlaytestVideoRecording recording = Start(source, new ProtokitePlaytestWindowsVideoEncoder(), Settings());
            // Paced, so each frame goes through before the next, as in a game.
            Play(recording, 120, pacedBy: source);
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(30, summary.FramesWritten, summary.Error);
            StringAssert.Contains("(on the graphics card)", summary.EncodedBy);
            ProtokitePlaytestWindowsMp4Reader.Result read = ProtokitePlaytestWindowsMp4Reader.Read(FinishedPath);
            Assert.IsNull(read.Error);
            CollectionAssert.AreEqual(source.Asked, read.TimesMs, "Windows decodes every frame, at the time it was captured");
            Assert.AreEqual(source.Asked.Last() + 67, read.DurationMs);
        }
#endif
    }
}
