using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestMp4FileTests
    {
        private const int Width = ProtokitePlaytestMp4TestFiles.Width;
        private const int Height = ProtokitePlaytestMp4TestFiles.Height;
        private const long FrameDurationMs = ProtokitePlaytestMp4TestFiles.FrameDurationMs;
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "protokite-mp4-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            // A test that failed with a file still open must report its own failure, not this one.
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private static byte[] Frame(int index, int bytes) => ProtokitePlaytestMp4TestFiles.MakeFrame(index, bytes, index == 0);

        private static ProtokitePlaytestEncodedFrame Encoded(int index, int bytes) => ProtokitePlaytestMp4TestFiles.MakeEncodedFrame(index, bytes);

        private static long HeaderBytes(string folder) => ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 0, 0, false).Length;

        private static long MovieHeaderBytes() => new ProtokitePlaytestMp4File().BytesFor(Encoded(0, 50)) - ProtokitePlaytestMp4File.FrameHeaderBytes - 50;

        // Writing

        [Test]
        public void WritesAnMp4FileAPlayerCanRead()
        {
            string path = Path.Combine(_folder, "frames.mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            Assert.IsTrue(file.Open(path, Width, Height, FrameDurationMs, out string error), error);
            long fileType = file.BytesWritten;
            Assert.AreEqual(32, fileType, "Only the file's type is written before the first frame");

            Assert.IsTrue(file.WriteFrame(Encoded(0, 50), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(1, 60), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(2, 70), out error), error);
            long expectedBytes = fileType + MovieHeaderBytes() + 3 * ProtokitePlaytestMp4File.FrameHeaderBytes + 50 + 60 + 70;
            Assert.AreEqual(expectedBytes, file.BytesWritten, "Bytes counted");
            Assert.AreEqual(3, file.FramesWritten);
            Assert.AreEqual(66, file.LastTimestampMs);
            Assert.IsTrue(file.Close(out error), error);

            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(path, out Mp4FileRead read), "It reads back");
            CollectionAssert.AreEqual(new[] { "ftyp", "moov", "moof", "mdat", "moof", "mdat", "moof", "mdat" }, read.TopLevelBoxes);
            Assert.AreEqual("isom", read.MajorBrand);
            CollectionAssert.IsSupersetOf(read.CompatibleBrands, new[] { "iso6", "avc1" }, "Fragments with decode times, and H.264");
            Assert.AreEqual(1000, read.MovieTimescale, "Times are in milliseconds");
            Assert.AreEqual(1000, read.TrackTimescale);
            Assert.AreEqual("avc1", read.SampleEntry, "H.264 with its settings kept before the first frame");
            Assert.AreEqual((64, 36), (read.Width, read.Height));
            CollectionAssert.AreEqual(ProtokitePlaytestMp4TestFiles.DecoderSettings, read.DecoderSettings, "The first frame's settings are the stream's");
            Assert.AreEqual(("nclx", 1, 1, 1, false), (read.ColourType, read.ColourPrimaries, read.ColourTransfer, read.ColourMatrix, read.FullRange),
                "BT.709 in the limited range, as the capture converts");
            Assert.AreEqual(99, read.DurationMs, "The length is the last frame's time and how long it shows");
            Assert.AreEqual(33, read.DefaultFrameDurationMs);
            Assert.AreEqual(expectedBytes, read.FileBytes, "The file is the size counted");
            Assert.AreEqual(3, read.Frames.Count);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, read.Frames.Select(frame => frame.Sequence).ToArray(), "Fragments are numbered in order");
            CollectionAssert.AreEqual(new long[] { 0, 33, 66 }, read.Frames.Select(frame => frame.TimestampMs).ToArray());
            CollectionAssert.AreEqual(new long[] { 33, 33, 33 }, read.Frames.Select(frame => frame.DurationMs).ToArray());
            CollectionAssert.AreEqual(Frame(1, 60), read.Frames[1].Bytes);
            Assert.IsTrue(read.Frames[0].IsKeyframe, "The first frame is one a player may start decoding at");
            Assert.IsFalse(read.Frames[1].IsKeyframe);
        }

        [Test]
        public void BytesForSaysExactlyWhatEachFrameAddsHeaderIncluded()
        {
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            Assert.IsTrue(file.Open(Path.Combine(_folder, "counted.mp4"), Width, Height, FrameDurationMs, out string error), error);
            for (int index = 0; index < 4; index++)
            {
                ProtokitePlaytestEncodedFrame frame = Encoded(index, 40 + index * 10);
                long before = file.BytesWritten;
                long said = file.BytesFor(frame);
                Assert.IsTrue(file.WriteFrame(frame, out error), error);
                Assert.AreEqual(said, file.BytesWritten - before, $"Frame {index}" + (index == 0 ? ", which carries the movie's header" : ""));
            }
            // A later frame carrying settings again is written without a second header, so what it was counted at is never too little.
            ProtokitePlaytestEncodedFrame again = new ProtokitePlaytestEncodedFrame(Frame(4, 40), 4 * 33, true, ProtokitePlaytestMp4TestFiles.DecoderSettings);
            long beforeAgain = file.BytesWritten;
            Assert.IsTrue(file.WriteFrame(again, out error), error);
            Assert.AreEqual(ProtokitePlaytestMp4File.FrameHeaderBytes + 40, file.BytesWritten - beforeAgain);
            Assert.GreaterOrEqual(file.BytesFor(again), file.BytesWritten - beforeAgain);
            file.Close(out _);
        }

        [Test]
        public void EveryFrameReachesTheOperatingSystemAsItIsWritten()
        {
            string path = Path.Combine(_folder, "live.mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            Assert.IsTrue(file.Open(path, Width, Height, FrameDurationMs, out string error), error);
            for (int index = 0; index < 3; index++)
                Assert.IsTrue(file.WriteFrame(Encoded(index, 100), out error), error);
            // Read through a second handle while the writer is open: what a killed process would leave behind.
            using (FileStream reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.AreEqual(file.BytesWritten, reader.Length, "Nothing is waiting in the writer's own buffer");
            file.Close(out _);
        }

        [Test]
        public void AFileThatWasNeverClosedStillReadsUpToWhereItStopped()
        {
            string path = Path.Combine(_folder, "abandoned.mp4");
            File.WriteAllBytes(path, ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 4, 100, false));
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(path, out Mp4FileRead read));
            Assert.AreEqual(0, read.DurationMs, "Its length is not stamped yet, which a fragmented file is allowed");
            Assert.AreEqual(4, read.Frames.Count);
        }

        [Test]
        public void OpenRefusesWhatAnMp4FileCannotHold()
        {
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            string path = Path.Combine(_folder, "refused.mp4");
            Assert.IsFalse(file.Open(path, 1, 36, FrameDurationMs, out string error), "A side under 2 pixels");
            StringAssert.Contains("1x36", error);
            Assert.IsFalse(file.Open(path, 70000, 36, FrameDurationMs, out error), "A side past what the header's two bytes hold");
            Assert.IsFalse(file.Open(path, Width, Height, 0, out error), "A frame shown for no time");
            StringAssert.Contains("0 ms", error);
            Assert.IsFalse(File.Exists(path), "Nothing is created for a refusal");
            Assert.IsTrue(file.Open(path, 2, 2, 1, out error), "Control: the smallest size opens. " + error);
            file.Close(out _);
        }

        [Test]
        public void WriteFrameRefusesAFrameTheFileCouldNotKeep()
        {
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            Assert.IsFalse(file.WriteFrame(Encoded(0, 20), out string error), "Nothing is written before the file is open");
            StringAssert.Contains("not open", error);
            Assert.IsTrue(file.Open(Path.Combine(_folder, "refuses.mp4"), Width, Height, FrameDurationMs, out error), error);
            byte[] settings = ProtokitePlaytestMp4TestFiles.DecoderSettings;

            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(null, 0, true, settings), out error));
            StringAssert.Contains("at least one byte", error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(new byte[0], 0, true, settings), out error), "An empty frame");
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(0, 20), -1, true, settings), out error), "A time before the start");
            StringAssert.Contains("before 0 ms", error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(0, 20), 0, true), out error), "A first frame without the stream's settings");
            StringAssert.Contains("settings", error);
            // A frame the finishing pass would stop at loses every frame after it once the game dies, so it is never written.
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(new byte[40], 0, true, settings), out error), "A frame of zeros");
            StringAssert.Contains("stored H.264 frame", error);
            byte[] tooLong = Frame(0, 20);
            tooLong[3] = 99;
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(tooLong, 0, true, settings), out error), "A first unit longer than the frame");
            Assert.AreEqual(0, file.FramesWritten, "Nothing refused was written");
            Assert.AreEqual(32, file.BytesWritten, "Nor any of the movie's header");

            Assert.IsTrue(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(0, 20), 100, true, settings), out error), "Control: a good frame. " + error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(1, 20), 100, false), out error), "The same time again");
            StringAssert.Contains("not later than the last", error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(1, 20), 99, false), out error), "An earlier time");
            Assert.IsTrue(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(1, 20), 5000000000L, false), out error), "Control: a time past four bytes of milliseconds. " + error);
            Assert.AreEqual(2, file.FramesWritten);
            file.Close(out _);
        }

        [Test]
        public void AFailedWriteTakesNoMoreFramesAndClosingCutsOffTheTornOne()
        {
            string path = Path.Combine(_folder, "full-disk.mp4");
            // The file's type is one write and each frame one more, so the fourth write is the third frame's.
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File(p => new StreamThatFailsPartWay(p, 3));
            Assert.IsTrue(file.Open(path, Width, Height, FrameDurationMs, out string error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(0, 100), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(1, 100), out error), error);
            Assert.IsFalse(file.WriteFrame(Encoded(2, 100), out error), "The write the disk refused");
            StringAssert.Contains("not enough space", error);
            Assert.IsFalse(file.WriteFrame(Encoded(3, 100), out error), "Nothing is written after a failed write");
            StringAssert.Contains("earlier write failed", error);
            Assert.AreEqual(2, file.FramesWritten);
            Assert.Greater(new FileInfo(path).Length, file.BytesWritten, "Precondition: part of the refused frame is on disk");
            Assert.IsTrue(file.Close(out error), error);

            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(path, out Mp4FileRead read), "The file reads back");
            Assert.AreEqual(2, read.Frames.Count, "With the two whole frames");
            Assert.AreEqual(file.BytesWritten, read.FileBytes, "And nothing of the torn one");
            Assert.AreEqual(66, read.DurationMs);
        }

        [Test]
        public void AFirstFrameTheDiskRefusedLeavesNoMovieHeaderBehind()
        {
            string path = Path.Combine(_folder, "full-at-once.mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File(p => new StreamThatFailsPartWay(p, 1));
            Assert.IsTrue(file.Open(path, Width, Height, FrameDurationMs, out string error), error);
            Assert.IsFalse(file.WriteFrame(Encoded(0, 100), out _));
            Assert.IsTrue(file.Close(out error), error);
            Assert.AreEqual(32, new FileInfo(path).Length, "Only the file's type is left: a recording with no frame, which the recording deletes");
        }

        // Finishing a file cut off part-way

        private static IEnumerable<TestCaseData> CutOffCases()
        {
            yield return Case("Whole frames with no length yet", folder => ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 4, 100, false), 4);
            yield return Case("A frame only partly written", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 4, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 5, 4 * 33, 100, false);
                bytes.AddRange(Frame(4, 100).Take(60));
                return bytes.ToArray();
            }, 4);
            yield return Case("A fragment header only partly written", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 4, 100, false));
                List<byte> header = new List<byte>();
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(header, 5, 4 * 33, 100, false);
                bytes.AddRange(header.Take(50));
                return bytes.ToArray();
            }, 4);
            yield return Case("Zeros after the last whole frame", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false));
                bytes.AddRange(new byte[300]);
                return bytes.ToArray();
            }, 2);
            yield return Case("A whole-sized frame of zeros", folder =>
            {
                // The header was written and the frame's bytes never were: the disk kept the length and filled it with zeros.
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 3, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 4, 3 * 33, 100, false);
                bytes.AddRange(new byte[100]);
                return bytes.ToArray();
            }, 3);
            yield return Case("A sample whose size is not its box's", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 3, 2 * 33, 100, false, claimedBoxBytes: 58);
                bytes.AddRange(Frame(2, 100));
                return bytes.ToArray();
            }, 2);
            yield return Case("A fragment laid out another way", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 3, 2 * 33, 100, false, runFlags: 0x000301);
                bytes.AddRange(Frame(2, 100));
                return bytes.ToArray();
            }, 2);
            yield return Case("A fragment whose frame is neither a keyframe nor another frame the writer marks", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 3, 2 * 33, 100, false, sampleFlags: 0x00000000);
                bytes.AddRange(Frame(2, 100));
                return bytes.ToArray();
            }, 2);
            yield return Case("A fragment claiming a frame of zero bytes, then a whole one", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 3, 2 * 33, 0, false);
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 4, 3 * 33, 100, false);
                bytes.AddRange(Frame(3, 100));
                return bytes.ToArray();
            }, 2);
            yield return Case("A fragment whose frame is not where its data offset says", folder =>
            {
                List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false));
                ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, 3, 2 * 33, 100, false, dataOffset: 100);
                bytes.AddRange(Frame(2, 100));
                return bytes.ToArray();
            }, 2);
            yield return Case("The file's type and no frame", folder => ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 0, 0, false), 0);
            yield return Case("The movie's header cut off part-way", folder => ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 3, 100, false).Take(200).ToArray(), 0);
            yield return Case("No video at all", folder => Encoding.ASCII.GetBytes("Only some words, and no video header before them."), 0);
            yield return Case("Frames behind another kind of file's header", folder =>
            {
                byte[] bytes = ProtokitePlaytestMp4TestFiles.MakeVideoBytes(folder, 2, 100, false);
                Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 4);
                return bytes;
            }, 0);
            yield return Case("An empty file", folder => new byte[0], 0);
        }

        private static TestCaseData Case(string name, Func<string, byte[]> makeBytes, int wholeFrames) =>
            new TestCaseData(makeBytes, wholeFrames).SetName(name);

        [TestCaseSource(nameof(CutOffCases))]
        public void FinishesAFileCutOffPartWay(Func<string, byte[]> makeBytes, int wholeFrames)
        {
            string unfinished = Path.Combine(_folder, "case.mp4.part");
            string finished = Path.Combine(_folder, "case.mp4");
            File.WriteAllBytes(unfinished, makeBytes(_folder));

            ProtokitePlaytestInterruptedRecordingResult result = new ProtokitePlaytestMp4File().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error);

            Assert.IsFalse(File.Exists(unfinished), "No unfinished file is left");
            if (wholeFrames == 0)
            {
                Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, result, error);
                Assert.IsFalse(File.Exists(finished), "And no finished one is made");
                return;
            }
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, result, error);
            Assert.AreEqual(wholeFrames, framesKept);
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(finished, out Mp4FileRead read), "The finished file reads back");
            Assert.AreEqual(wholeFrames, read.Frames.Count, "Every whole frame");
            Assert.AreEqual(wholeFrames * 33, read.DurationMs, "And the last whole frame's time and showing as its length");
            Assert.AreEqual(HeaderBytes(_folder) + MovieHeaderBytes() + wholeFrames * (ProtokitePlaytestMp4File.FrameHeaderBytes + 100), read.FileBytes, "And nothing after them");
        }

        [Test]
        public void LeavesAFileAnotherProgramHasOpen()
        {
            string held = Path.Combine(_folder, "held.mp4.part");
            byte[] heldBytes = ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 4, 100, false);
            File.WriteAllBytes(held, heldBytes);
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                ProtokitePlaytestInterruptedRecordingResult result = new ProtokitePlaytestMp4File().FinishInterruptedRecording(held, Path.Combine(_folder, "held.mp4"), out int framesKept, out string error);
                Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish, result);
                StringAssert.Contains("another program", error);
                Assert.AreEqual(0, framesKept);
            }
            // Byte for byte, not by length: stamping the length changes bytes without changing the file's length.
            CollectionAssert.AreEqual(heldBytes, File.ReadAllBytes(held), "It is left as it was");
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "held.mp4")));
        }

        [Test]
        public void AFileNotLaidOutTheWayThisPackageWritesIsLeftAsItWas()
        {
            string unfinished = Path.Combine(_folder, "other.mp4.part");
            byte[] bytes = ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 3, 100, false);
            // The box the length is stamped into, made a box of padding of the same size: another program's MP4.
            int lengthBox = ProtokitePlaytestMp4TestFiles.FindBoxType(bytes, 0, "mehd");
            Assert.Greater(lengthBox, 0, "Precondition: the length's box is in the file");
            Encoding.ASCII.GetBytes("free").CopyTo(bytes, lengthBox);
            File.WriteAllBytes(unfinished, bytes);

            ProtokitePlaytestInterruptedRecordingResult result = new ProtokitePlaytestMp4File().FinishInterruptedRecording(unfinished, Path.Combine(_folder, "other.mp4"), out _, out string error);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish, result, "Neither deleted nor cut: it is not one of this package's");
            StringAssert.Contains("not laid out the way", error);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(unfinished), "It is left as it was");
        }

        [Test]
        public void FinishingAFileThatWasClosedKeepsItByteForByte()
        {
            string unfinished = Path.Combine(_folder, "closed.mp4.part");
            string finished = Path.Combine(_folder, "closed.mp4");
            byte[] closed = ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 3, 100, true);
            File.WriteAllBytes(unfinished, closed);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, new ProtokitePlaytestMp4File().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error), error);
            Assert.AreEqual(3, framesKept);
            CollectionAssert.AreEqual(closed, File.ReadAllBytes(finished), "Byte for byte what closing wrote");
        }

        [Test]
        public void FinishingReplacesAFinishedFileOfTheSameName()
        {
            string unfinished = Path.Combine(_folder, "again.mp4.part");
            string finished = Path.Combine(_folder, "again.mp4");
            File.WriteAllBytes(finished, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(unfinished, ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 2, 100, false));
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, new ProtokitePlaytestMp4File().FinishInterruptedRecording(unfinished, finished, out _, out string error), error);
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(finished, out Mp4FileRead read));
            Assert.AreEqual(2, read.Frames.Count);
        }

        [Test]
        public void FinishingAFileInPlaceKeepsItWhereItIs()
        {
            string path = Path.Combine(_folder, "in-place.mp4");
            File.WriteAllBytes(path, ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 2, 100, false));
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, new ProtokitePlaytestMp4File().FinishInterruptedRecording(path, path, out int framesKept, out string error), error);
            Assert.AreEqual(2, framesKept);
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(path, out Mp4FileRead read));
            Assert.AreEqual(66, read.DurationMs);
        }

        [Test]
        public void WhenClosingCannotCutOffATornFrameItSaysSoAndFinishingDoes()
        {
            string path = Path.Combine(_folder, "full-disk-twice.mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File(p => new StreamThatFailsPartWay(p, 3, true));
            Assert.IsTrue(file.Open(path, Width, Height, FrameDurationMs, out string error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(0, 100), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(1, 100), out error), error);
            Assert.IsFalse(file.WriteFrame(Encoded(2, 100), out _), "The write the disk refused");
            Assert.IsFalse(file.Close(out error), "Closing could not cut the torn frame off");
            StringAssert.Contains("left for finishing later", error);

            string finished = Path.Combine(_folder, "full-disk-twice-finished.mp4");
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, new ProtokitePlaytestMp4File().FinishInterruptedRecording(path, finished, out int framesKept, out error), error);
            Assert.AreEqual(2, framesKept, "Finishing keeps the two whole frames");
            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(finished, out Mp4FileRead read), "The finished file reads back");
            Assert.AreEqual(2, read.Frames.Count);
            Assert.AreEqual(file.BytesWritten, read.FileBytes, "And nothing of the torn one");
        }

        [Test]
        public void AFinishedPathThatIsNoPathLeavesTheRecordingAsItWas()
        {
            string unfinished = Path.Combine(_folder, "bad-name.mp4.part");
            File.WriteAllBytes(unfinished, ProtokitePlaytestMp4TestFiles.MakeVideoBytes(_folder, 2, 100, false));
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish,
                new ProtokitePlaytestMp4File().FinishInterruptedRecording(unfinished, Path.Combine(_folder, "bad") + "\0name.mp4", out _, out string error), "Returned, not thrown");
            StringAssert.Contains("could not be", error);
            Assert.IsTrue(File.Exists(unfinished), "The recording is kept for another try");
        }

        [Test]
        public void AMissingFileHoldsNoFrame()
        {
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame,
                new ProtokitePlaytestMp4File().FinishInterruptedRecording(Path.Combine(_folder, "gone.mp4.part"), Path.Combine(_folder, "gone.mp4"), out int framesKept, out _));
            Assert.AreEqual(0, framesKept);
        }

        // A phone's recording: frames of thousands of bytes, captured at uneven times, with a keyframe after the first

        [Test]
        public void APhonesRecordingIsWrittenWithEachFrameWhereItsFragmentSaysAndItsRealLength()
        {
            (long TimestampMs, int Bytes, bool Keyframe)[] phone = ProtokitePlaytestMp4TestFiles.PhoneFrames;
            Assert.IsTrue(phone.Any(frame => frame.Bytes > ushort.MaxValue), "Precondition: a frame whose size two bytes cannot hold");
            Assert.Greater(phone.Skip(1).Select((frame, index) => frame.TimestampMs - phone[index].TimestampMs).Distinct().Count(), 1,
                "Precondition: frames captured at uneven times, as a game's are");
            Assert.IsTrue(phone.Skip(1).Any(frame => frame.Keyframe), "Precondition: a keyframe after the first");
            string path = Path.Combine(_folder, "phone.mp4");
            File.WriteAllBytes(path, ProtokitePlaytestMp4TestFiles.MakePhoneRecordingBytes(_folder, closed: true));

            Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(path, out Mp4FileRead read), "It reads back with every frame where its fragment says: " + read.Problem);
            Assert.AreEqual((1280, 592), (read.Width, read.Height));
            CollectionAssert.AreEqual(ProtokitePlaytestMp4TestFiles.PhoneDecoderSettings, read.DecoderSettings, "The phone's own settings");
            CollectionAssert.AreEqual(phone.Select(frame => frame.TimestampMs).ToArray(), read.Frames.Select(frame => frame.TimestampMs).ToArray(), "Each frame at the time it was captured");
            CollectionAssert.AreEqual(phone.Select(frame => (long)frame.Bytes).ToArray(), read.Frames.Select(frame => frame.SampleBytes).ToArray(), "Each frame's size as its fragment states it");
            CollectionAssert.AreEqual(phone.Select(frame => frame.Keyframe).ToArray(), read.Frames.Select(frame => frame.IsKeyframe).ToArray(), "Each keyframe marked as one");
            for (int index = 0; index < phone.Length; index++)
                CollectionAssert.AreEqual(ProtokitePlaytestMp4TestFiles.MakePhoneFrame(index).Data, read.Frames[index].Bytes, $"Frame {index}'s bytes");
            Assert.AreEqual(phone[phone.Length - 1].TimestampMs + ProtokitePlaytestMp4TestFiles.PhoneFrameDurationMs, read.DurationMs,
                "The length is the last frame's time and how long it shows, not the frames counted at one length");
        }

        [Test]
        public void APhonesRecordingCutOffAnywhereIsFinishedUpToItsLastWholeFragment()
        {
            (long TimestampMs, int Bytes, bool Keyframe)[] phone = ProtokitePlaytestMp4TestFiles.PhoneFrames;
            byte[] leftByAKill = ProtokitePlaytestMp4TestFiles.MakePhoneRecordingBytes(_folder, closed: false);
            List<long> fragmentStarts = ProtokitePlaytestMp4TestFiles.FragmentStarts(leftByAKill);
            Assert.AreEqual(phone.Length, fragmentStarts.Count, "Precondition: one fragment a frame");
            List<long> fragmentEnds = fragmentStarts.Skip(1).Concat(new[] { (long)leftByAKill.Length }).ToList();
            int lengthValueAt = ProtokitePlaytestMp4TestFiles.FindBoxType(leftByAKill, 0, "mehd") + 8;
            Assert.Greater(lengthValueAt, 8, "Precondition: the length's box is in the file");

            // Cut inside the file's type and the movie's header, then through every part of every fragment: header, frame start, frame, end.
            SortedSet<long> cuts = new SortedSet<long> { 0, 10, 32, 100, fragmentStarts[0] - 1, fragmentStarts[0] };
            for (int fragment = 0; fragment < fragmentStarts.Count; fragment++)
            {
                foreach (long bytesIn in new long[] { 1, 4, 8, 24, 50, 84, 100, 107, 108, 111, 112, 113 })
                    cuts.Add(fragmentStarts[fragment] + bytesIn);
                cuts.Add((fragmentStarts[fragment] + fragmentEnds[fragment]) / 2);
                cuts.Add(fragmentEnds[fragment] - 1);
                cuts.Add(fragmentEnds[fragment]);
            }

            HashSet<int> wholeFragmentCountsSeen = new HashSet<int>();
            foreach (long cut in cuts)
            {
                string at = $"Cut at byte {cut}";
                string unfinished = Path.Combine(_folder, $"cut-{cut}.mp4.part");
                string finished = Path.Combine(_folder, $"cut-{cut}.mp4");
                byte[] cutFile = new byte[cut];
                Array.Copy(leftByAKill, cutFile, cut);
                File.WriteAllBytes(unfinished, cutFile);
                int wholeFragments = fragmentEnds.Count(end => end <= cut);
                wholeFragmentCountsSeen.Add(wholeFragments);

                ProtokitePlaytestInterruptedRecordingResult result = new ProtokitePlaytestMp4File().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error);

                Assert.IsFalse(File.Exists(unfinished), at + ": no unfinished file is left");
                if (wholeFragments == 0)
                {
                    Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, result, at + ": " + error);
                    Assert.IsFalse(File.Exists(finished), at + ": and no finished one is made");
                    continue;
                }
                Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, result, at + ": " + error);
                Assert.AreEqual(wholeFragments, framesKept, at);
                byte[] kept = File.ReadAllBytes(finished);
                Assert.AreEqual(fragmentEnds[wholeFragments - 1], kept.Length, at + ": nothing after the last whole fragment");
                for (int index = 0; index < kept.Length; index++)
                {
                    if ((index < lengthValueAt || index >= lengthValueAt + 8) && kept[index] != leftByAKill[index])
                        Assert.Fail($"{at}: byte {index} is not what the cut file held; only the length is stamped in");
                }
                Assert.IsTrue(ProtokitePlaytestMp4TestFiles.Read(finished, out Mp4FileRead read), at + ": it reads back: " + read.Problem);
                CollectionAssert.AreEqual(phone.Take(wholeFragments).Select(frame => frame.TimestampMs).ToArray(), read.Frames.Select(frame => frame.TimestampMs).ToArray(), at);
                Assert.AreEqual(phone[wholeFragments - 1].TimestampMs + ProtokitePlaytestMp4TestFiles.PhoneFrameDurationMs, read.DurationMs,
                    at + ": its length is its last whole frame's time and how long that shows");
                File.Delete(finished);
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(0, phone.Length + 1).ToArray(), wholeFragmentCountsSeen.ToArray(),
                "Positive control: the cuts left every number of whole fragments, from none to all");
        }

        [Test]
        public void AFragmentThatSendsAPlayerToTheWrongBytesIsCaughtByReadingTheFileBack()
        {
            byte[] closed = ProtokitePlaytestMp4TestFiles.MakePhoneRecordingBytes(_folder, closed: true);
            long thirdFragment = ProtokitePlaytestMp4TestFiles.FragmentStarts(closed)[2];
            // Each field found by its box's name, not by the writer's own offsets.
            int runBox = ProtokitePlaytestMp4TestFiles.FindBoxType(closed, thirdFragment, "trun");
            int fragmentHeaderBox = ProtokitePlaytestMp4TestFiles.FindBoxType(closed, thirdFragment, "tfhd");
            int dataBox = ProtokitePlaytestMp4TestFiles.FindBoxType(closed, thirdFragment, "mdat");
            Assert.IsTrue(runBox > thirdFragment && fragmentHeaderBox > thirdFragment && dataBox > runBox, "Precondition: the third fragment's boxes are found");
            Assert.IsTrue(ReadWithChange(closed, bytes => { }, out Mp4FileRead control), "Control: the file as written reads back. " + control.Problem);
            Assert.IsFalse(control.Frames[2].IsKeyframe, "Precondition: the third frame is not a keyframe as written");

            Assert.IsFalse(ReadWithChange(closed, bytes => ProtokitePlaytestMp4TestFiles.AddToNumber(bytes, runBox + 20, 1), out Mp4FileRead read), "A frame stated a byte longer than its data box holds");
            StringAssert.Contains("fragment 3 says its frame is 6084 bytes, and its data box holds 6083", read.Problem);
            Assert.IsFalse(ReadWithChange(closed, bytes => ProtokitePlaytestMp4TestFiles.AddToNumber(bytes, runBox + 12, -8), out read), "A data offset pointing at the data box's own header");
            StringAssert.Contains($"fragment 3 says its frame starts at byte {thirdFragment + 100}, where its data box puts it at byte {thirdFragment + 108}", read.Problem);
            Assert.IsFalse(ReadWithChange(closed, bytes => bytes[fragmentHeaderBox + 7] |= 1, out read), "A fragment counting its frame's position from a base of its own");
            StringAssert.Contains("fragment 3 names a base or default flags of its own", read.Problem);
            Assert.IsFalse(ReadWithChange(closed, bytes => bytes[runBox + 6] |= 0x08, out read), "A run box whose flags state a field it does not hold");
            StringAssert.Contains("fragment 3's run box is shorter than its flags say", read.Problem);
            Assert.IsFalse(ReadWithChange(closed, bytes => ProtokitePlaytestMp4TestFiles.AddToNumber(bytes, dataBox - 4, 1), out read), "A data box a byte longer than its frame");
            StringAssert.Contains("fragment 3 says its frame is 6083 bytes, and its data box holds 6084", read.Problem);

            // A fragment stating no frame flags takes the movie's defaults (here, a keyframe), as a player does.
            Assert.IsTrue(ReadWithChange(closed, bytes => bytes[runBox + 6] &= 0xFB, out read), "Leaving the frame's flags out moves nothing. " + read.Problem);
            Assert.IsTrue(read.Frames[2].IsKeyframe, "The movie's default flags mark every frame a keyframe");
        }

        private bool ReadWithChange(byte[] original, Action<byte[]> change, out Mp4FileRead read)
        {
            byte[] bytes = (byte[])original.Clone();
            change(bytes);
            string path = Path.Combine(_folder, "changed-" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(path, bytes);
            return ProtokitePlaytestMp4TestFiles.Read(path, out read);
        }

#if UNITY_EDITOR_WIN
        // Real frames from Windows' encoder, read back by Windows' own decoder

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void ARealRecordingDecodesFrameForFrameAndSurvivesACut()
        {
            RealEncoding.AssumeAGraphicsCardEncoder();
            const int frames = 40;
            List<ProtokitePlaytestEncodedFrame> encoded = new List<ProtokitePlaytestEncodedFrame>();
            RealEncoding.OnItsOwnThread(() =>
            {
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder())
                {
                    ProtokitePlaytestVideoEncoderSettings settings = new ProtokitePlaytestVideoEncoderSettings { Width = RealEncoding.Width, Height = RealEncoding.Height };
                    Assert.IsTrue(encoder.Configure(settings, out string error), error);
                    for (int i = 0; i < frames; i++)
                        Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(i), i * 67L, 67, encoded, out error), error);
                    Assert.IsTrue(encoder.Finish(encoded, out error), error);
                }
            });
            Assert.AreEqual(frames, encoded.Count, "Precondition: one frame out for every frame in");
            Assert.IsTrue(encoded.Skip(1).Any(frame => !frame.IsKeyframe), "Precondition: the recording holds frames that are not keyframes");

            string closedPath = Path.Combine(_folder, "real.mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            Assert.IsTrue(file.Open(closedPath, RealEncoding.Width, RealEncoding.Height, 67, out string writeError), writeError);
            foreach (ProtokitePlaytestEncodedFrame frame in encoded)
                Assert.IsTrue(file.WriteFrame(frame, out writeError), writeError);
            Assert.IsTrue(file.Close(out writeError), writeError);
            AssertWindowsDecodesEveryFrame(closedPath, encoded, frames);

            // The game dies while the last frame is half written.
            string unfinished = Path.Combine(_folder, "real-cut.mp4.part");
            ProtokitePlaytestMp4File abandoned = new ProtokitePlaytestMp4File();
            Assert.IsTrue(abandoned.Open(unfinished, RealEncoding.Width, RealEncoding.Height, 67, out writeError), writeError);
            for (int i = 0; i < frames - 1; i++)
                Assert.IsTrue(abandoned.WriteFrame(encoded[i], out writeError), writeError);
            long wholeBytes = abandoned.BytesWritten;
            abandoned.AbandonForTesting();
            List<byte> cut = new List<byte>(File.ReadAllBytes(unfinished));
            ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(cut, frames, (frames - 1) * 67, encoded[frames - 1].Data.Length, false);
            cut.AddRange(encoded[frames - 1].Data.Take(encoded[frames - 1].Data.Length / 2));
            File.WriteAllBytes(unfinished, cut.ToArray());

            string finished = Path.Combine(_folder, "real-cut.mp4");
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, file.FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error), error);
            Assert.AreEqual(frames - 1, framesKept, "Every whole frame is kept");
            Assert.AreEqual(wholeBytes, new FileInfo(finished).Length, "The half frame is cut off");
            AssertWindowsDecodesEveryFrame(finished, encoded, frames - 1);
        }

        private static void AssertWindowsDecodesEveryFrame(string path, List<ProtokitePlaytestEncodedFrame> written, int frames)
        {
            ProtokitePlaytestWindowsMp4Reader.Result read = ProtokitePlaytestWindowsMp4Reader.Read(path, index => true);
            Assert.IsNull(read.Error, "Windows reads the file");
            Assert.AreEqual((RealEncoding.Width, RealEncoding.Height), (read.Width, read.Height));
            Assert.AreEqual(frames, read.TimesMs.Count, "Windows decodes every frame");
            Assert.AreEqual(written[frames - 1].TimestampMs + 67, read.DurationMs, "And reads the length stamped in");
            for (int i = 0; i < frames; i++)
            {
                Assert.AreEqual(written[i].TimestampMs, read.TimesMs[i], $"Frame {i}'s time");
                Assert.That(RealEncoding.LumaPsnr(RealEncoding.MovingPicture(i), read.Pictures[i]), Is.GreaterThan(28), $"Frame {i} decodes to the picture that was sent");
            }
        }

#endif

        // The seam a second kind of file plugs into

        [Test]
        public void TheFakeAndTheMp4FileKeepTheSameContract()
        {
            foreach (IProtokitePlaytestRecordingFile file in new IProtokitePlaytestRecordingFile[] { new FakeRecordingFile(), new ProtokitePlaytestMp4File() })
            {
                string name = file.GetType().Name;
                string unfinished = Path.Combine(_folder, name + ".part");
                string finished = Path.Combine(_folder, name + file.FileExtension);
                using (file)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(file.ContentType), name);
                    StringAssert.StartsWith(".", file.FileExtension, name);
                    Assert.AreEqual(-1, file.LastTimestampMs, name);
                    Assert.IsTrue(file.Open(unfinished, Width, Height, FrameDurationMs, out string error), name + ": " + error);
                    for (int i = 0; i < 3; i++)
                    {
                        long before = file.BytesWritten;
                        long said = file.BytesFor(Encoded(i, 40));
                        Assert.IsTrue(file.WriteFrame(Encoded(i, 40), out error), name + ": " + error);
                        Assert.AreEqual(said, file.BytesWritten - before, name + " says what a frame adds");
                    }
                    Assert.IsFalse(file.WriteFrame(Encoded(1, 40), out _), name + " refuses a time that is not later");
                    Assert.AreEqual(3, file.FramesWritten, name);
                    Assert.AreEqual(66, file.LastTimestampMs, name);
                    Assert.IsTrue(file.Close(out error), name + ": " + error);
                    Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, file.FinishInterruptedRecording(unfinished, finished, out int framesKept, out error), name + ": " + error);
                    Assert.AreEqual(3, framesKept, name);
                }
            }
            Assert.AreEqual("video/mp4", new ProtokitePlaytestMp4File().ContentType, "What the upload sends a recording as");
            Assert.AreEqual(".mp4", new ProtokitePlaytestMp4File().FileExtension);
            // A later launch knows a recording only by its ending, so the two must name the same kind.
            Assert.AreEqual(new ProtokitePlaytestMp4File().ContentType, ProtokitePlaytestRecordingFiles.ContentTypeFor("recording" + new ProtokitePlaytestMp4File().FileExtension),
                "Sent as the same kind by the launch that wrote it and by a later one");
            Assert.AreEqual("video/mp4", ProtokitePlaytestRecordingFiles.ContentTypeWritten);
            Assert.IsInstanceOf<ProtokitePlaytestMp4File>(ProtokitePlaytestRecordingFiles.ForFinishing("recording.mp4"));
            Assert.IsNull(ProtokitePlaytestRecordingFiles.ContentTypeFor("recording.mp4.part"), "Never one still being written");
        }

        [Test]
        public void NothingButTheMp4FileNamesItsBoxes()
        {
            string runtime = Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly).resolvedPath, "Runtime");
            string[] naming = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) != "ProtokitePlaytestMp4File.cs")
                .Where(file => Regex.IsMatch(File.ReadAllText(file), "moof|mdat|ftyp|mehd|trex|tfdt"))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Video", "ProtokitePlaytestMp4File.cs")), "Positive control: the MP4 file is where this scan expects");
            CollectionAssert.IsEmpty(naming, "Everything else goes through IProtokitePlaytestRecordingFile");
        }
    }
}
