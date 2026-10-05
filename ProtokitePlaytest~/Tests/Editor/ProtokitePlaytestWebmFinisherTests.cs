using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    // Earlier versions of the package recorded WebM; a recording one of them was writing when its game ended is still finished and uploaded.
    public class ProtokitePlaytestWebmFinisherTests
    {
        private const ProtokitePlaytestWebmFinisher.Codec Vp8 = ProtokitePlaytestWebmFinisher.Codec.Vp8;
        private const ProtokitePlaytestWebmFinisher.Codec Vp9 = ProtokitePlaytestWebmFinisher.Codec.Vp9;
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

        private static byte[] Frame(ProtokitePlaytestWebmFinisher.Codec codec, int index, int bytes) => ProtokitePlaytestWebmTestFiles.MakeFrame(codec, index, bytes, index == 0);

        private static ProtokitePlaytestWebmFinisher Finisher() => new ProtokitePlaytestWebmFinisher();

        private static IEnumerable<TestCaseData> CutOffCases()
        {
            foreach (ProtokitePlaytestWebmFinisher.Codec codec in new[] { Vp8, Vp9 })
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
                    // A whole frame of the codec, but its block claims another size than its cluster: not a frame a recording wrote.
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
                // A VP8 key frame without its start code, and an inter frame whose first part is longer than the frame.
                List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(folder, Vp8, 2, 100, false));
                byte[] firstPartTooLong = Frame(Vp8, 2, 100);
                firstPartTooLong[1] = 0xFF;
                ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, 2, 100);
                bytes.AddRange(firstPartTooLong);
                return bytes.ToArray();
            }), (int)ProtokitePlaytestInterruptedRecordingResult.Finished, 2).SetName("Vp8: a frame whose first part is longer than the frame");
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
            ProtokitePlaytestWebmFinisher.Codec codec = (ProtokitePlaytestWebmFinisher.Codec)codecNumber;
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
            Assert.AreEqual(headerBytes + wholeFrames * (ProtokitePlaytestWebmFinisher.FrameHeaderBytes + 100), read.FileBytes, "And nothing after them");
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
            // Another codec, the same length as this one's name, so nothing else in the file moves.
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

        // Where the length and the duration sit depends on the name written into the file, so both are read off the file.
        [TestCase("Flock")]
        [TestCase("ProtokitePlaytest")]
        [TestCase("Protokite Playtest for a much longer product name")]
        public void FinishesAFileWhateverNameWroteIt(string writtenBy)
        {
            string unfinished = Path.Combine(_folder, "renamed.webm.part");
            string finished = Path.Combine(_folder, "renamed.webm");
            File.WriteAllBytes(unfinished, ProtokitePlaytestWebmTestFiles.MakeVideoBytes(_folder, Vp8, 5, 100, false, writtenBy));

            ProtokitePlaytestInterruptedRecordingResult result = Finisher().FinishInterruptedRecording(unfinished, finished, out int framesKept, out string error);
            Assert.AreEqual(ProtokitePlaytestInterruptedRecordingResult.Finished, result, error);
            Assert.AreEqual(5, framesKept);
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(finished, out WebmFileRead read), "A finished file reads back");
            Assert.AreEqual(writtenBy, read.MuxingApp, "Its name was not written over by the stamp");
            Assert.AreEqual(132.0, read.DurationMs, 1e-9, "The duration is stamped where the duration is");
            Assert.IsTrue(read.SegmentSizeWritten);
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

        [Test]
        public void ALeftoverWebmIsFinishedAndSentAsWebm()
        {
            Assert.IsInstanceOf<ProtokitePlaytestWebmFinisher>(ProtokitePlaytestRecordingFiles.ForFinishing(Path.Combine(_folder, "recording.webm")));
            Assert.AreEqual("video/webm", ProtokitePlaytestRecordingFiles.ContentTypeFor("recording.webm"), "Sent as what it is, though this version writes MP4");
            Assert.IsNull(ProtokitePlaytestRecordingFiles.ForFinishing(Path.Combine(_folder, "recording.mkv")), "A kind the package never wrote is left alone");
        }

        [Test]
        public void NothingButTheWebmFinisherAndTheKindsOfFileNameWebm()
        {
            string runtime = Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly).resolvedPath, "Runtime");
            string[] allowed = { "ProtokitePlaytestWebmFinisher.cs", "ProtokitePlaytestRecordingFiles.cs" };
            string[] naming = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
                .Where(file => !allowed.Contains(Path.GetFileName(file)))
                .Where(file => Regex.IsMatch(File.ReadAllText(file), "webm|matroska|ebml", RegexOptions.IgnoreCase))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Video", allowed[0])), "Positive control: the WebM finisher is where this scan expects");
            CollectionAssert.IsEmpty(naming, "Everything else goes through IProtokitePlaytestRecordingFinisher");
        }
    }
}
