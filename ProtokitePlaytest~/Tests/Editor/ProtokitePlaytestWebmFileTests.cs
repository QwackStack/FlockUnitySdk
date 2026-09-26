using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestWebmFileTests
    {
        private const ProtokitePlaytestVideoCodec Vp8 = ProtokitePlaytestVideoCodec.Vp8;
        private const ProtokitePlaytestVideoCodec Vp9 = ProtokitePlaytestVideoCodec.Vp9;
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "protokite-webm-tests", Guid.NewGuid().ToString("N"));
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

        private static byte[] Frame(ProtokitePlaytestVideoCodec codec, int index, int bytes) => ProtokitePlaytestWebmTestFiles.MakeFrame(codec, index, bytes, index == 0);

        private static ProtokitePlaytestEncodedFrame Encoded(ProtokitePlaytestVideoCodec codec, int index, int bytes) =>
            ProtokitePlaytestWebmTestFiles.MakeEncodedFrame(codec, index, bytes);

        private static ProtokitePlaytestWebmFile Finisher() => new ProtokitePlaytestWebmFile();

        // Writing

        [TestCase(8, "V_VP8")]
        [TestCase(9, "V_VP9")]
        public void WritesAWebmFileAPlayerCanRead(int codecNumber, string codecName)
        {
            ProtokitePlaytestVideoCodec codec = (ProtokitePlaytestVideoCodec)codecNumber;
            string path = Path.Combine(_folder, "frames.webm");
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile();
            Assert.IsTrue(file.Open(path, codec, 64, 36, out string error), error);
            Assert.AreEqual(file.HeaderBytes, file.BytesWritten, "The header is written");

            Assert.IsTrue(file.WriteFrame(Encoded(codec, 0, 50), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(codec, 1, 60), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(codec, 2, 70), out error), error);
            long expectedBytes = file.HeaderBytes + 3 * ProtokitePlaytestWebmFile.FrameHeaderBytes + 50 + 60 + 70;
            Assert.AreEqual(expectedBytes, file.BytesWritten, "Bytes counted");
            Assert.AreEqual(3, file.FramesWritten);
            Assert.AreEqual(66, file.LastTimestampMs);
            Assert.IsTrue(file.Close(out error), error);

            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(path, out WebmFileRead read), "It reads back");
            Assert.AreEqual("webm", read.DocType);
            Assert.AreEqual(codecName, read.Codec, "The codec asked for is the one named");
            Assert.AreEqual(ProtokitePlaytestWebmFile.WrittenBy, read.MuxingApp);
            Assert.AreEqual(64, read.Width);
            Assert.AreEqual(36, read.Height);
            Assert.AreEqual(1000000UL, read.TimecodeScaleNanoseconds, "Times are in milliseconds");
            Assert.IsTrue(read.SegmentSizeWritten, "A closed file states how long its segment is");
            Assert.AreEqual(66.0, read.DurationMs, 1e-9, "The duration is the last frame's time");
            Assert.AreEqual(expectedBytes, read.FileBytes, "The file is the size counted");
            Assert.AreEqual(3, read.Frames.Count);
            Assert.AreEqual(33, read.Frames[1].TimestampMs);
            CollectionAssert.AreEqual(Frame(codec, 1, 60), read.Frames[1].Bytes);
            Assert.IsTrue(read.Frames[0].IsKeyframe, "The first frame is one a player may start decoding at");
            Assert.IsFalse(read.Frames[1].IsKeyframe);
        }

        [Test]
        public void EveryFrameReachesTheOperatingSystemAsItIsWritten()
        {
            string path = Path.Combine(_folder, "live.webm");
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile();
            Assert.IsTrue(file.Open(path, Vp8, 64, 36, out string error), error);
            for (int index = 0; index < 3; index++)
                Assert.IsTrue(file.WriteFrame(Encoded(Vp8, index, 100), out error), error);
            // Read through a second handle while the writer is open: what a killed process would leave behind.
            using (FileStream reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.AreEqual(file.BytesWritten, reader.Length, "Nothing is waiting in the writer's own buffer");
            file.Close(out _);
        }

        [Test]
        public void AFileThatWasNeverClosedStillReadsUpToWhereItStopped()
        {
            string path = Path.Combine(_folder, "abandoned.webm");
            File.WriteAllBytes(path, ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 4, 100, false));
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(path, out WebmFileRead read));
            Assert.IsFalse(read.SegmentSizeWritten, "It keeps the size-unknown marker, which is legal WebM");
            Assert.AreEqual(4, read.Frames.Count);
        }

        [Test]
        public void OpenRefusesWhatAWebmFileCannotHold()
        {
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile();
            string path = Path.Combine(_folder, "refused.webm");
            Assert.IsFalse(file.Open(path, (ProtokitePlaytestVideoCodec)7, 64, 36, out string error));
            StringAssert.Contains("VP8 or VP9", error);
            Assert.IsFalse(file.Open(path, Vp8, 1, 36, out error), "A side under 2 pixels");
            StringAssert.Contains("1x36", error);
            Assert.IsFalse(File.Exists(path), "Nothing is created for a refusal");
            Assert.IsTrue(file.Open(path, Vp8, 2, 2, out error), "Control: the smallest size opens. " + error);
            file.Close(out _);
        }

        [Test]
        public void WriteFrameRefusesAFrameTheFileCouldNotKeep()
        {
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile();
            Assert.IsFalse(file.WriteFrame(Encoded(Vp8, 0, 20), out string error), "Nothing is written before the file is open");
            StringAssert.Contains("not open", error);
            Assert.IsTrue(file.Open(Path.Combine(_folder, "refuses.webm"), Vp8, 64, 36, out error), error);

            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(null, 0, true), out error));
            StringAssert.Contains("at least one byte", error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(new byte[0], 0, true), out error), "An empty frame");
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(Vp8, 0, 20), -1, true), out error), "A time before the start");
            StringAssert.Contains("0 to", error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(Vp8, 0, 20), ProtokitePlaytestWebmFile.MaxTimestampMs + 1, true), out error), "A time past four bytes of milliseconds");
            // A frame the finishing pass would stop at loses every frame after it once the game dies, so it is never written.
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(new byte[40], 0, true), out error), "A frame of zeros");
            StringAssert.Contains("Vp8 frame", error);
            Assert.AreEqual(0, file.FramesWritten, "Nothing refused was written");

            Assert.IsTrue(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(Vp8, 0, 20), 100, true), out error), "Control: a good frame. " + error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(Vp8, 1, 20), 100, false), out error), "The same time again");
            StringAssert.Contains("not later than the last", error);
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(Vp8, 1, 20), 99, false), out error), "An earlier time");
            Assert.IsTrue(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(Vp8, 1, 20), ProtokitePlaytestWebmFile.MaxTimestampMs, false), out error), "Control: the largest time. " + error);
            Assert.AreEqual(2, file.FramesWritten);
            file.Close(out _);
        }

        [TestCase(8)]
        [TestCase(9)]
        public void AFrameWhoseStartIsNotTheCodecsIsRefused(int codecNumber)
        {
            ProtokitePlaytestVideoCodec codec = (ProtokitePlaytestVideoCodec)codecNumber;
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile();
            Assert.IsTrue(file.Open(Path.Combine(_folder, "starts.webm"), codec, 64, 36, out string error), error);
            byte[] zeros = new byte[40];
            Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(zeros, 0, true), out error), "Zeros");
            if (codec == Vp8)
            {
                byte[] keyWithoutStartCode = Frame(Vp8, 0, 40);
                keyWithoutStartCode[3] = 0;
                Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(keyWithoutStartCode, 0, true), out error), "A key frame without its start code");
                byte[] firstPartTooLong = Frame(Vp8, 1, 40);
                firstPartTooLong[1] = 0xFF;
                Assert.IsFalse(file.WriteFrame(new ProtokitePlaytestEncodedFrame(firstPartTooLong, 0, false), out error), "A first part longer than the frame");
            }
            Assert.IsTrue(file.WriteFrame(new ProtokitePlaytestEncodedFrame(Frame(codec, 0, 40), 0, true), out error), "Control: a frame of the codec. " + error);
            file.Close(out _);
        }

        [Test]
        public void AFailedWriteTakesNoMoreFramesAndClosingCutsOffTheTornOne()
        {
            string path = Path.Combine(_folder, "full-disk.webm");
            // The header is one write and each frame two, so the sixth write is the third frame's first half.
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile(ProtokitePlaytestWebmFile.WrittenBy, p => new StreamThatFailsPartWay(p, 5));
            Assert.IsTrue(file.Open(path, Vp8, 64, 36, out string error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(Vp8, 0, 100), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(Vp8, 1, 100), out error), error);
            Assert.IsFalse(file.WriteFrame(Encoded(Vp8, 2, 100), out error), "The write the disk refused");
            StringAssert.Contains("not enough space", error);
            Assert.IsFalse(file.WriteFrame(Encoded(Vp8, 3, 100), out error), "Nothing is written after a failed write");
            StringAssert.Contains("earlier write failed", error);
            Assert.AreEqual(2, file.FramesWritten);
            Assert.Greater(new FileInfo(path).Length, file.BytesWritten, "Precondition: part of the refused frame is on disk");
            Assert.IsTrue(file.Close(out error), error);

            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(path, out WebmFileRead read), "The file reads back");
            Assert.AreEqual(2, read.Frames.Count, "With the two whole frames");
            Assert.AreEqual(file.BytesWritten, read.FileBytes, "And nothing of the torn one");
            Assert.AreEqual(33.0, read.DurationMs, 1e-9);
        }

        // Finishing a file cut off part-way

        private static IEnumerable<TestCaseData> CutOffCases()
        {
            foreach (ProtokitePlaytestVideoCodec codec in new[] { Vp8, Vp9 })
            {
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder => ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 4, 100, false)), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 4)
                    .SetName($"{codec}: whole frames with no length yet");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 4, 100, false));
                    ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 4, 100);
                    bytes.AddRange(Frame(codec, 4, 60));
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 4).SetName($"{codec}: a frame only partly written");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 4, 100, false));
                    bytes.AddRange(new byte[] { 0x1F, 0x43, 0xB6, 0x75, 0x10 });
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 4).SetName($"{codec}: a frame header only partly written");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 2, 100, false));
                    bytes.AddRange(new byte[64]);
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 2).SetName($"{codec}: zeros after the last whole frame");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    // The header was written and the frame's bytes never were: the disk kept the length and filled it with zeros.
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 3, 100, false));
                    ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 3, 100);
                    bytes.AddRange(new byte[100]);
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 3).SetName($"{codec}: a whole-sized frame of zeros");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    // A header saying zero bytes, then a whole frame: the video ends at the empty one.
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 2, 100, false));
                    ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 2, 0);
                    ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 3, 130);
                    bytes.AddRange(Frame(codec, 3, 130));
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 2).SetName($"{codec}: a frame of zero bytes");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder => ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 0, 0, false)), (int)ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, 0)
                    .SetName($"{codec}: a header and no frame");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    // A whole frame of the codec, but its block claims another size than its cluster: not a frame this file wrote.
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 2, 100, false));
                    ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 2, 100, 50);
                    bytes.AddRange(Frame(codec, 2, 100));
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 2).SetName($"{codec}: a block whose size is not its cluster's");
                yield return new TestCaseData((int)codec, (Func<string, byte[]>)(folder =>
                {
                    List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, codec, 2, 100, false));
                    ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 2, 100, -1, 0x82);
                    bytes.AddRange(Frame(codec, 2, 100));
                    return bytes.ToArray();
                }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 2).SetName($"{codec}: a block of a track the file does not have");
            }
            yield return new TestCaseData((int)Vp8, (Func<string, byte[]>)(folder =>
            {
                byte[] bytes = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, Vp8, 3, 100, false);
                return bytes.Take(80).ToArray();
            }), (int)ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, 0).SetName("A header cut off part-way");
            yield return new TestCaseData((int)Vp8, (Func<string, byte[]>)(folder => Encoding.ASCII.GetBytes("Only some words, and no video header before them.")), (int)ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, 0)
                .SetName("No video at all");
            yield return new TestCaseData((int)Vp8, (Func<string, byte[]>)(folder =>
            {
                byte[] bytes = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, Vp8, 2, 100, false);
                bytes[0] = (byte)'R';
                bytes[1] = (byte)'I';
                bytes[2] = (byte)'F';
                bytes[3] = (byte)'F';
                return bytes;
            }), (int)ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, 0).SetName("Frames behind another kind of file's header");
            yield return new TestCaseData((int)Vp8, (Func<string, byte[]>)(folder => new byte[0]), (int)ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame, 0).SetName("An empty file");
        }

        [TestCaseSource(nameof(CutOffCases))]
        public void FinishesAFileCutOffPartWay(int codecNumber, Func<string, byte[]> makeBytes, int expectedNumber, int wholeFrames)
        {
            ProtokitePlaytestVideoCodec codec = (ProtokitePlaytestVideoCodec)codecNumber;
            ProtokitePlaytestInterruptedRecordingResult expected = (ProtokitePlaytestInterruptedRecordingResult)expectedNumber;
            string unfinished = Path.Combine(_folder, "case.webm.part");
            string finished = Path.Combine(_folder, "case.webm");
            File.WriteAllBytes(unfinished, makeBytes(_folder));

            ProtokitePlaytestInterruptedRecordingResult result = Finisher().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error);

            Assert.AreEqual(expected, result, error);
            Assert.IsFalse(File.Exists(unfinished), "No unfinished file is left");
            if (expected != ProtokitePlaytestInterruptedRecordingResult.Finished)
            {
                Assert.IsFalse(File.Exists(finished), "And no finished one is made");
                return;
            }
            Assert.AreEqual(wholeFrames, framesKept);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(finished, out WebmFileRead read), "The finished file reads back");
            Assert.AreEqual(wholeFrames, read.Frames.Count, "Every whole frame");
            Assert.IsTrue(read.SegmentSizeWritten, "With its segment's length stamped in");
            Assert.AreEqual((wholeFrames - 1) * 33.0, read.DurationMs, 1e-9, "And the last whole frame's time as its duration");
            long headerBytes = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, codec, 0, 0, false).Length;
            Assert.AreEqual(headerBytes + wholeFrames * (ProtokitePlaytestWebmFile.FrameHeaderBytes + 100), read.FileBytes, "And nothing after them");
        }

        [Test]
        public void LeavesAFileAnotherProgramHasOpen()
        {
            string held = Path.Combine(_folder, "held.webm.part");
            byte[] heldBytes = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 4, 100, false);
            File.WriteAllBytes(held, heldBytes);
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                ProtokitePlaytestInterruptedRecordingResult result = Finisher().FinishInterruptedRecording(held, Path.Combine(_folder, "held.webm"), out int framesKept, out string error);
                Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish, result);
                StringAssert.Contains("another program", error);
                Assert.AreEqual(0, framesKept);
            }
            // Byte for byte, not by length: stamping the segment's length changes bytes without changing the length.
            CollectionAssert.AreEqual(heldBytes, File.ReadAllBytes(held), "It is left as it was");
            Assert.IsFalse(File.Exists(Path.Combine(_folder, "held.webm")));
        }

        [Test]
        public void AFileOfACodecThisBuildCannotCheckIsLeftAsItWas()
        {
            string unfinished = Path.Combine(_folder, "later.webm.part");
            byte[] bytes = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 3, 100, false);
            // A later build's codec, the same length as this one's name, so nothing else in the file moves.
            string text = Encoding.ASCII.GetString(bytes);
            int codecAt = text.IndexOf("V_VP8", StringComparison.Ordinal);
            Assert.Greater(codecAt, 0, "Precondition: the codec is named in the file");
            Encoding.ASCII.GetBytes("V_AV1").CopyTo(bytes, codecAt);
            File.WriteAllBytes(unfinished, bytes);

            ProtokitePlaytestInterruptedRecordingResult result = Finisher().FinishInterruptedRecording(unfinished, Path.Combine(_folder, "later.webm"), out _, out string error);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish, result, "Neither deleted nor cut: its frames cannot be checked here");
            StringAssert.Contains("V_AV1", error);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(unfinished), "It is left as it was");
        }

        // The other engine hardcodes where the length and the duration sit, and a rename moved them once; here both are read off the file.
        [TestCase("Flock")]
        [TestCase("ProtokitePlaytest")]
        [TestCase("Protokite Playtest for a much longer product name")]
        public void FinishesAndClosesAFileWhateverNameWroteIt(string writtenBy)
        {
            string unfinished = Path.Combine(_folder, "renamed.webm.part");
            string finished = Path.Combine(_folder, "renamed.webm");
            File.WriteAllBytes(unfinished, ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 5, 100, false, writtenBy));

            ProtokitePlaytestInterruptedRecordingResult result = Finisher().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, result, error);
            Assert.AreEqual(5, framesKept);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(finished, out WebmFileRead finishedRead), "A finished file reads back");
            Assert.AreEqual(writtenBy, finishedRead.MuxingApp, "Its name was not written over by the stamp");
            Assert.AreEqual(132.0, finishedRead.DurationMs, 1e-9, "The duration is stamped where the duration is");
            Assert.IsTrue(finishedRead.SegmentSizeWritten);

            string closedPath = Path.Combine(_folder, "closed.webm");
            File.WriteAllBytes(closedPath, ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 5, 100, true, writtenBy));
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(closedPath, out WebmFileRead closedRead), "A closed file reads back");
            Assert.AreEqual(writtenBy, closedRead.MuxingApp);
            Assert.AreEqual(132.0, closedRead.DurationMs, 1e-9);
            Assert.AreEqual(5, closedRead.Frames.Count);
        }

        [Test]
        public void FinishingAFileThatWasClosedKeepsItByteForByte()
        {
            string unfinished = Path.Combine(_folder, "closed.webm.part");
            string finished = Path.Combine(_folder, "closed.webm");
            byte[] closed = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp9, 3, 100, true);
            File.WriteAllBytes(unfinished, closed);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, Finisher().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error), error);
            Assert.AreEqual(3, framesKept);
            CollectionAssert.AreEqual(closed, File.ReadAllBytes(finished), "Byte for byte what closing wrote");
        }

        [Test]
        public void FinishingReplacesAFinishedFileOfTheSameName()
        {
            string unfinished = Path.Combine(_folder, "again.webm.part");
            string finished = Path.Combine(_folder, "again.webm");
            File.WriteAllBytes(finished, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(unfinished, ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 2, 100, false));
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, Finisher().FinishInterruptedRecording(unfinished, finished, out _, out string error), error);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(finished, out WebmFileRead read));
            Assert.AreEqual(2, read.Frames.Count);
        }

        [Test]
        public void FinishingAFileInPlaceKeepsItWhereItIs()
        {
            string path = Path.Combine(_folder, "in-place.webm");
            File.WriteAllBytes(path, ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 2, 100, false));
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, Finisher().FinishInterruptedRecording(path, path, out int framesKept, out string error), error);
            Assert.AreEqual(2, framesKept);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(path, out WebmFileRead read));
            Assert.IsTrue(read.SegmentSizeWritten);
        }

        [Test]
        public void WhenClosingCannotCutOffATornFrameItSaysSoAndFinishingDoes()
        {
            string path = Path.Combine(_folder, "full-disk-twice.webm");
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile(ProtokitePlaytestWebmFile.WrittenBy, p => new StreamThatFailsPartWay(p, 5, true));
            Assert.IsTrue(file.Open(path, Vp8, 64, 36, out string error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(Vp8, 0, 100), out error), error);
            Assert.IsTrue(file.WriteFrame(Encoded(Vp8, 1, 100), out error), error);
            Assert.IsFalse(file.WriteFrame(Encoded(Vp8, 2, 100), out _), "The write the disk refused");
            Assert.IsFalse(file.Close(out error), "Closing could not cut the torn frame off");
            StringAssert.Contains("left for finishing later", error);

            string finished = Path.Combine(_folder, "full-disk-twice-finished.webm");
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, Finisher().FinishInterruptedRecording(path, finished, out int framesKept, out error), error);
            Assert.AreEqual(2, framesKept, "Finishing keeps the two whole frames");
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(finished, out WebmFileRead read), "The finished file reads back");
            Assert.AreEqual(2, read.Frames.Count);
            Assert.AreEqual(file.BytesWritten, read.FileBytes, "And nothing of the torn one");
        }

        [Test]
        public void TheLargestFrameStillHasASizeTheClusterCanSay()
        {
            // A four-byte size of all ones means "size unknown", so the largest cluster is one less.
            Assert.AreEqual(0x0FFFFFFE, ProtokitePlaytestWebmFile.MaxFrameBytes + ProtokitePlaytestWebmFile.FrameHeaderBytes - 8);
        }

        [Test]
        public void AFinishedPathThatIsNoPathLeavesTheRecordingAsItWas()
        {
            string unfinished = Path.Combine(_folder, "bad-name.webm.part");
            byte[] bytes = ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 2, 100, false);
            File.WriteAllBytes(unfinished, bytes);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish,
                Finisher().FinishInterruptedRecording(unfinished, Path.Combine(_folder, "bad") + "\0name.webm", out _, out string error), "Returned, not thrown");
            StringAssert.Contains("could not be", error);
            Assert.IsTrue(File.Exists(unfinished), "The recording is kept for another try");
        }

        [Test]
        public void AMissingFileHoldsNoFrame()
        {
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame,
                Finisher().FinishInterruptedRecording(Path.Combine(_folder, "gone.webm.part"), Path.Combine(_folder, "gone.webm"), out int framesKept, out _));
            Assert.AreEqual(0, framesKept);
        }

        // Real frames from the encoder

        private const int RealWidth = 320;
        private const int RealHeight = 240;

        private static List<ProtokitePlaytestEncodedFrame> EncodeRealFrames(ProtokitePlaytestVideoCodec codec, int frames)
        {
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestLibVpx.CreateEncoder(out string whyNot);
            Assert.IsNotNull(encoder, "Precondition: this editor carries the encoder. " + whyNot);
            using (encoder)
            {
                ProtokitePlaytestVideoEncoderSettings settings = new ProtokitePlaytestVideoEncoderSettings { Codec = codec, Width = RealWidth, Height = RealHeight };
                Assert.IsTrue(encoder.Configure(settings, out string error), error);
                for (int i = 0; i < frames; i++)
                    Assert.IsTrue(encoder.Encode(RealPicture(i), i * 66L, 66, i == frames / 2, output, out error), error);
                Assert.IsTrue(encoder.Finish(output, out error), error);
            }
            Assert.AreEqual(frames, output.Count, "Precondition: one packet a frame");
            return output;
        }

        // A picture that moves every frame, so the encoder writes real inter frames.
        private static byte[] RealPicture(int index)
        {
            byte[] i420 = new byte[ProtokitePlaytestI420.FrameLength(RealWidth, RealHeight)];
            for (int y = 0; y < RealHeight; y++)
            for (int x = 0; x < RealWidth; x++)
                i420[y * RealWidth + x] = (byte)(((x + index * 5) / 20 % 2 == 0 ? 50 : 200) + (y * 30 / RealHeight));
            for (int i = RealWidth * RealHeight; i < i420.Length; i++)
                i420[i] = (byte)(100 + (index * 4 + i / 7) % 60);
            return i420;
        }

        private static void AssertEveryFrameDecodes(ProtokitePlaytestVideoCodec codec, WebmFileRead read, List<ProtokitePlaytestEncodedFrame> written, int frames)
        {
            Assert.AreEqual(frames, read.Frames.Count, "Every frame is in the file");
            using (ProtokitePlaytestLibVpx.Decoder decoder = new ProtokitePlaytestLibVpx.Decoder(codec))
            {
                for (int i = 0; i < frames; i++)
                {
                    CollectionAssert.AreEqual(written[i].Data, read.Frames[i].Bytes, $"Frame {i} is the encoder's bytes");
                    Assert.AreEqual(written[i].TimestampMs, read.Frames[i].TimestampMs, $"Frame {i}'s time");
                    Assert.AreEqual(written[i].IsKeyframe, read.Frames[i].IsKeyframe, $"Frame {i}'s key frame flag");
                    Assert.IsNotNull(decoder.Decode(read.Frames[i].Bytes, RealWidth, RealHeight, out string error), $"Frame {i} decodes: {error}");
                }
            }
        }

        [TestCase(8, "V_VP8")]
        [TestCase(9, "V_VP9")]
        public void ARealRecordingDecodesFrameForFrameAndSurvivesACut(int codecNumber, string codecName)
        {
            ProtokitePlaytestVideoCodec codec = (ProtokitePlaytestVideoCodec)codecNumber;
            const int frames = 40;
            List<ProtokitePlaytestEncodedFrame> encoded = EncodeRealFrames(codec, frames);
            Assert.IsTrue(encoded.Skip(1).Any(frame => !frame.IsKeyframe), "Precondition: the recording holds inter frames");

            string closedPath = Path.Combine(_folder, "real.webm");
            ProtokitePlaytestWebmFile file = new ProtokitePlaytestWebmFile();
            Assert.IsTrue(file.Open(closedPath, codec, RealWidth, RealHeight, out string error), error);
            foreach (ProtokitePlaytestEncodedFrame frame in encoded)
                Assert.IsTrue(file.WriteFrame(frame, out error), error);
            Assert.IsTrue(file.Close(out error), error);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(closedPath, out WebmFileRead closedRead));
            Assert.AreEqual(codecName, closedRead.Codec);
            Assert.AreEqual(encoded[frames - 1].TimestampMs, closedRead.DurationMs, 1e-9);
            AssertEveryFrameDecodes(codec, closedRead, encoded, frames);

            // The game dies while the last frame is half written.
            string unfinished = Path.Combine(_folder, "real-cut.webm.part");
            ProtokitePlaytestWebmFile abandoned = new ProtokitePlaytestWebmFile();
            Assert.IsTrue(abandoned.Open(unfinished, codec, RealWidth, RealHeight, out error), error);
            for (int i = 0; i < frames - 1; i++)
                Assert.IsTrue(abandoned.WriteFrame(encoded[i], out error), error);
            long wholeBytes = abandoned.BytesWritten;
            abandoned.AbandonForTesting();
            List<byte> cut = new List<byte>(File.ReadAllBytes(unfinished));
            ProtokitePlaytestWebmTestFiles.AppendFrameHeader(cut, frames - 1, encoded[frames - 1].Data.Length);
            cut.AddRange(encoded[frames - 1].Data.Take(encoded[frames - 1].Data.Length / 2));
            File.WriteAllBytes(unfinished, cut.ToArray());

            string finished = Path.Combine(_folder, "real-cut.webm");
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, file.FinishInterruptedRecording(unfinished, finished, out int framesKept, out error), error);
            Assert.AreEqual(frames - 1, framesKept, "Every whole frame is kept, whatever the codec's frames start with");
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(finished, out WebmFileRead finishedRead));
            Assert.AreEqual(wholeBytes, finishedRead.FileBytes, "The half frame is cut off");
            Assert.AreEqual(encoded[frames - 2].TimestampMs, finishedRead.DurationMs, 1e-9);
            AssertEveryFrameDecodes(codec, finishedRead, encoded, frames - 1);
        }

        // Writes real recordings where a browser can be pointed at them; run by hand with PROTOKITE_WEBM_CHECK_FOLDER set.
        [Test, Explicit]
        public void WritesRecordingsForABrowserCheck()
        {
            string folder = Environment.GetEnvironmentVariable("PROTOKITE_WEBM_CHECK_FOLDER");
            if (string.IsNullOrEmpty(folder))
                Assert.Ignore("Set PROTOKITE_WEBM_CHECK_FOLDER to the folder to write the recordings into.");
            Directory.CreateDirectory(folder);
            foreach (ProtokitePlaytestVideoCodec codec in new[] { Vp8, Vp9 })
            {
                List<ProtokitePlaytestEncodedFrame> encoded = EncodeRealFrames(codec, 60);
                ProtokitePlaytestWebmFile closed = new ProtokitePlaytestWebmFile();
                Assert.IsTrue(closed.Open(Path.Combine(folder, $"closed-{codec}.webm"), codec, RealWidth, RealHeight, out string error), error);
                encoded.ForEach(frame => Assert.IsTrue(closed.WriteFrame(frame, out _)));
                Assert.IsTrue(closed.Close(out error), error);

                string cutPath = Path.Combine(folder, $"cut-{codec}.webm");
                ProtokitePlaytestWebmFile cut = new ProtokitePlaytestWebmFile();
                Assert.IsTrue(cut.Open(cutPath, codec, RealWidth, RealHeight, out error), error);
                encoded.Take(45).ToList().ForEach(frame => Assert.IsTrue(cut.WriteFrame(frame, out _)));
                cut.AbandonForTesting();

                string finishedFrom = Path.Combine(folder, $"finished-{codec}.webm.part");
                File.Copy(cutPath, finishedFrom, true);
                Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished,
                    cut.FinishInterruptedRecording(finishedFrom, Path.Combine(folder, $"finished-{codec}.webm"), out _, out error), error);
            }
        }

        // The seam a second kind of file plugs into

        [Test]
        public void TheFakeAndTheWebmFileKeepTheSameContract()
        {
            foreach (IProtokitePlaytestRecordingFile file in new IProtokitePlaytestRecordingFile[] { new FakeRecordingFile(), new ProtokitePlaytestWebmFile() })
            {
                string name = file.GetType().Name;
                string unfinished = Path.Combine(_folder, name + ".part");
                string finished = Path.Combine(_folder, name + file.FileExtension);
                using (file)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(file.ContentType), name);
                    StringAssert.StartsWith(".", file.FileExtension, name);
                    Assert.AreEqual(-1, file.LastTimestampMs, name);
                    Assert.IsTrue(file.Open(unfinished, Vp8, 64, 36, out string error), name + ": " + error);
                    for (int i = 0; i < 3; i++)
                        Assert.IsTrue(file.WriteFrame(Encoded(Vp8, i, 40), out error), name + ": " + error);
                    Assert.IsFalse(file.WriteFrame(Encoded(Vp8, 1, 40), out _), name + " refuses a time that is not later");
                    Assert.AreEqual(3, file.FramesWritten, name);
                    Assert.AreEqual(66, file.LastTimestampMs, name);
                    Assert.IsTrue(file.Close(out error), name + ": " + error);
                    Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, file.FinishInterruptedRecording(unfinished, finished, out int framesKept, out error), name + ": " + error);
                    Assert.AreEqual(3, framesKept, name);
                }
            }
            Assert.AreEqual("video/webm", new ProtokitePlaytestWebmFile().ContentType, "What the upload sends a WebM recording as");
            Assert.AreEqual(".webm", new ProtokitePlaytestWebmFile().FileExtension);
        }

        [Test]
        public void NothingButTheWebmFileNamesWebm()
        {
            string runtime = Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly).resolvedPath, "Runtime");
            string[] naming = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) != "ProtokitePlaytestWebmFile.cs")
                .Where(file => Regex.IsMatch(File.ReadAllText(file), "webm|matroska|ebml", RegexOptions.IgnoreCase))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Video", "ProtokitePlaytestWebmFile.cs")), "Positive control: the WebM file is where this scan expects");
            CollectionAssert.IsEmpty(naming, "Everything else goes through IProtokitePlaytestRecordingFile");
        }
    }
}
