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

        private string FinishedPath => Path.Combine(_folder, "recording.webm");

        private static ProtokitePlaytestVideoSettings Settings(Action<ProtokitePlaytestVideoSettings> change = null)
        {
            ProtokitePlaytestVideoSettings settings = new ProtokitePlaytestVideoSettings { FramesPerSecond = 15 };
            change?.Invoke(settings);
            return settings;
        }

        private ProtokitePlaytestVideoRecording Start(FakeFrameSource source, IProtokitePlaytestVideoEncoder encoder, ProtokitePlaytestVideoSettings settings,
            Action beforeEachEncode = null, Func<bool> beforeEachWrite = null)
        {
            ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, encoder, new ProtokitePlaytestWebmFile(), settings,
                FinishedPath, out string error, beforeEachEncode, beforeEachWrite);
            Assert.IsNotNull(recording, error);
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
                if (pacedBy != null)
                    SpinWait.SpinUntil(() => pacedBy.BlocksReturned >= pacedBy.Asked.Count - 1, 500);
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
            FakeVp8Encoder encoder = new FakeVp8Encoder();
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
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(FinishedPath, out WebmFileRead read));
            Assert.AreEqual(30, read.Frames.Count);
            CollectionAssert.AreEqual(source.Asked, read.Frames.Select(frame => frame.TimestampMs), "Shown at the times they were captured");
            Assert.AreEqual("V_VP8", read.Codec);
        }

        [Test]
        public void EveryFramesBlockGoesBackOnceItIsEncoded()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings());
            Play(recording, 20);
            Assert.AreEqual(5, source.Asked.Count, "Precondition: fewer frames than may wait, so none is dropped");
            StopAndWait(recording);
            Assert.AreEqual(5, source.BlocksReturned, "Each block goes back to be filled again");
        }

        [Test]
        public void AFileThatThrowsWhenClosedStillFinishesTheRecording()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, new FakeVp8Encoder(), new FileThatThrowsWhenClosed(),
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
            public long BytesWritten => _inner.BytesWritten;
            public int FramesWritten => _inner.FramesWritten;
            public long LastTimestampMs => _inner.LastTimestampMs;
            public bool Open(string path, ProtokitePlaytestVideoCodec codec, int width, int height, out string error) => _inner.Open(path, codec, width, height, out error);
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
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings());
            Play(recording, 20);
            Assert.AreEqual(5, source.Asked.Count, "Precondition: frames were asked for and none has arrived");
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(5, summary.FramesWritten, "The source is waited for when the recording stops");
        }

        [Test]
        public void ACaptureTimeThatFindsTheSourceFullIsCounted()
        {
            FakeFrameSource source = new FakeFrameSource { Ready = false };
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings());
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
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings());
            Play(recording, 60);
            recording.LeaveOutNextFrame();
            recording.AddFrame(300.0);
            Play(recording, 1);
            StopAndWait(recording);
            Assert.AreEqual(1000, source.Asked.Last(), "The frame after the five minutes away follows the second before it");
        }

        [Test]
        public void TheSourcesLossesAreReported()
        {
            FakeFrameSource source = new FakeFrameSource { FramesLostOnTheGraphicsCard = 2, FramesDroppedForWantOfABlock = 3 };
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings());
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(2, summary.FramesLostOnTheGraphicsCard);
            Assert.AreEqual(3, summary.FramesDroppedForWantOfABlock);
        }

        [TestCase(true, System.Threading.ThreadPriority.BelowNormal)]
        [TestCase(false, System.Threading.ThreadPriority.Normal)]
        public void TheEncoderGivesWayToTheGameWhenTheSettingSaysSo(bool belowTheGame, System.Threading.ThreadPriority expected)
        {
            ProtokitePlaytestVideoRecording recording = Start(new FakeFrameSource(), new FakeVp8Encoder(), Settings(s => s.EncoderBelowGamePriority = belowTheGame));
            Assert.AreEqual(expected, recording.EncoderThreadPriority);
            StopAndWait(recording);
        }

        // Limits

        [Test]
        public void TheLengthLimitStopsItForGood()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings(s => s.MaxSeconds = 1.0));
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.ReachedLengthLimit, Play(recording, 600));
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
            // The header, then room for ten frames of 40 bytes and their cluster headers, and a little more.
            long header = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(Path.GetTempPath(), ProtokitePlaytestVideoCodec.Vp8, 0, 0, false).Length;
            long limit = header + 10 * (ProtokitePlaytestWebmFile.FrameHeaderBytes + 40) + 30;
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings(s => s.MaxBytes = limit));
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
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(FinishedPath, out WebmFileRead read));
            Assert.AreEqual(10, read.Frames.Count);
        }

        // Falling behind

        [Test]
        public void FramesAreDroppedBeforeEncodingWhenEncodingFallsBehind()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdEncoding = new ManualResetEventSlim(false);
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings(), () => holdEncoding.Wait(Plenty));
            Play(recording, 4 * 30);
            Assert.AreEqual(30, source.Asked.Count, "Precondition: 30 frames captured while the encoder is held");
            holdEncoding.Set();
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);

            Assert.AreEqual(ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode, summary.FramesWritten, "As many as may wait, and no more");
            Assert.AreEqual(30 - ProtokitePlaytestVideoRecording.MostFramesWaitingToEncode, summary.FramesDroppedBecauseEncodingFellBehind);
            Assert.AreEqual(30, source.BlocksReturned, "A dropped frame's block goes back too");
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(FinishedPath, out WebmFileRead read), "What was written still reads");
        }

        [Test]
        public void FramesAreDroppedBeforeEncodingWhenWritingFallsBehind()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdWriting = new ManualResetEventSlim(false);
            int frames = ProtokitePlaytestVideoRecording.MostFramesWaitingToWrite + 20;
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings(s => s.FramesPerSecond = 60),
                beforeEachWrite: () => holdWriting.Wait(Plenty));
            for (int i = 0; i < frames; i++)
            {
                recording.AddFrame(SixtyFps);
                // Frames go to the encoder one at a time, as a game's frames would, so the queue in front of it never fills.
                SpinWait.SpinUntil(() => source.BlocksReturned >= source.Asked.Count - 1, 1000);
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
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings(), beforeEachWrite: () => Interlocked.Increment(ref writes) <= 5);
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
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(FinishedPath, out WebmFileRead read));
            Assert.AreEqual(5, read.Frames.Count);
            Assert.IsFalse(File.Exists(FinishedPath + ".part"));
        }

        [Test]
        public void AFailedEncodeStopsItAndKeepsTheFramesEncodedBefore()
        {
            FakeFrameSource source = new FakeFrameSource();
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder { FailAtFrame = 3 }, Settings());
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
        public void ARecordingThatCapturedNothingLeavesNoFile()
        {
            ProtokitePlaytestVideoRecording recording = Start(new FakeFrameSource(), new FakeVp8Encoder(), Settings());
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.IsNull(summary.FilePath);
            Assert.IsNull(summary.Error);
            Assert.IsFalse(File.Exists(FinishedPath) || File.Exists(FinishedPath + ".part"), "Nothing is left on disk");
        }

        [Test]
        public void AnEncoderThatCannotBeConfiguredStartsNothing()
        {
            FakeFrameSource source = new FakeFrameSource();
            IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(out string whyNot);
            Assert.IsNotNull(encoder, "Precondition: this editor carries the encoder. " + whyNot);
            using (encoder)
            {
                ProtokitePlaytestVideoRecording recording = ProtokitePlaytestVideoRecording.Start(source, encoder, new ProtokitePlaytestWebmFile(),
                    Settings(s => { s.Codec = ProtokitePlaytestVideoCodec.Vp9; s.Speed = 12; }), FinishedPath, out string error);
                Assert.IsNull(recording);
                StringAssert.Contains("speed", error);
                Assert.IsFalse(File.Exists(FinishedPath + ".part"), "No file is opened for a recording that cannot start");
            }
        }

        [Test]
        public void WaitingForTheFileIsBounded()
        {
            FakeFrameSource source = new FakeFrameSource();
            ManualResetEventSlim holdWriting = new ManualResetEventSlim(false);
            ProtokitePlaytestVideoRecording recording = Start(source, new FakeVp8Encoder(), Settings(), beforeEachWrite: () => holdWriting.Wait(Plenty));
            Play(recording, 20);
            recording.StopCapturing(ProtokitePlaytestVideoStopReason.GameQuitting);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsFalse(recording.WaitUntilWritten(TimeSpan.FromMilliseconds(300)), "A disk that holds the write is not waited for past the bound");
            Assert.Less(clock.ElapsedMilliseconds, 3000);
            Assert.IsTrue(File.Exists(FinishedPath + ".part"), "What was recorded stays in the .part file");
            holdWriting.Set();
            Assert.IsTrue(recording.WaitUntilWritten(Plenty), "And it is finished once the disk lets go");
        }

        // A real encoder, end to end

        [TestCase(8)]
        [TestCase(9)]
        public void ARealEncoderRecordsAFileThatDecodesFrameForFrame(int codecNumber)
        {
            ProtokitePlaytestVideoCodec codec = (ProtokitePlaytestVideoCodec)codecNumber;
            FakeFrameSource source = new FakeFrameSource(64, 48);
            IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(out string whyNot);
            Assert.IsNotNull(encoder, "Precondition: this editor carries the encoder. " + whyNot);
            ProtokitePlaytestVideoRecording recording = Start(source, encoder, Settings(s => s.Codec = codec));
            Play(recording, 120, pacedBy: source);
            ProtokitePlaytestVideoRecordingSummary summary = StopAndWait(recording);
            Assert.AreEqual(30, summary.FramesWritten, summary.Error);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(FinishedPath, out WebmFileRead read));
            Assert.AreEqual(codec == ProtokitePlaytestVideoCodec.Vp8 ? "V_VP8" : "V_VP9", read.Codec);
            using (ProtokitePlaytestLibVpx.Decoder decoder = new ProtokitePlaytestLibVpx.Decoder(codec))
            {
                foreach (WebmFrameRead frame in read.Frames)
                    Assert.IsNotNull(decoder.Decode(frame.Bytes, 64, 48, out string error), error);
            }
        }
    }
}
