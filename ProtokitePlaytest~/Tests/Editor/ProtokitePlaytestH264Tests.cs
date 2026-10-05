using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestH264Tests
    {
        private static readonly byte[] Sequence = { 0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9 };
        private static readonly byte[] Picture = { 0x68, 0xEB, 0xE3 };
        private static readonly byte[] Keyframe = { 0x65, 0x88, 0x84, 0x21 };
        private static readonly byte[] OtherFrame = { 0x41, 0x9A, 0x02 };
        private static readonly byte[] Marker = { 0x09, 0x10 };
        private static readonly byte[] Extra = { 0x06, 0x05, 0x11 };

        private static byte[] Stream(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

        private static readonly byte[] FourByteStart = { 0, 0, 0, 1 };
        private static readonly byte[] ThreeByteStart = { 0, 0, 1 };

        private static List<byte[]> Units(byte[] stream) =>
            ProtokitePlaytestH264.SplitUnits(stream, stream.Length).Select(unit => unit.ToArray()).ToList();

        [Test]
        public void SplitsAnEncodersOutputAtEitherStartCode()
        {
            byte[] stream = Stream(FourByteStart, Marker, FourByteStart, Sequence, ThreeByteStart, Picture, FourByteStart, Keyframe);
            List<byte[]> units = Units(stream);
            Assert.AreEqual(4, units.Count);
            CollectionAssert.AreEqual(Marker, units[0]);
            CollectionAssert.AreEqual(Sequence, units[1], "A four-byte start code's leading zero is not left on the unit before it");
            CollectionAssert.AreEqual(Picture, units[2]);
            CollectionAssert.AreEqual(Keyframe, units[3]);
        }

        [Test]
        public void ZerosPaddingTheEndAndBytesBeforeTheFirstStartCodeAreNotUnits()
        {
            byte[] stream = Stream(new byte[] { 7, 7 }, ThreeByteStart, Keyframe, new byte[] { 0, 0, 0 });
            List<byte[]> units = Units(stream);
            Assert.AreEqual(1, units.Count);
            CollectionAssert.AreEqual(Keyframe, units[0]);
            CollectionAssert.IsEmpty(Units(new byte[] { 1, 2, 3 }), "No start code, no unit");
            CollectionAssert.IsEmpty(Units(new byte[0]));
        }

        [Test]
        public void OnlyOutputThatStartsWithAStartCodeIsAByteStream()
        {
            Assert.IsTrue(ProtokitePlaytestH264.StartsWithAStartCode(Stream(FourByteStart, Keyframe), 8));
            Assert.IsTrue(ProtokitePlaytestH264.StartsWithAStartCode(Stream(ThreeByteStart, Keyframe), 7));
            // The same frame with its unit after a length, as an MP4 file keeps it: an encoder handing this back would otherwise lose every frame unsaid.
            byte[] afterALength = Stream(new byte[] { 0, 0, 0, (byte)Keyframe.Length }, Keyframe);
            Assert.IsFalse(ProtokitePlaytestH264.StartsWithAStartCode(afterALength, afterALength.Length));
            Assert.IsFalse(ProtokitePlaytestH264.StartsWithAStartCode(Stream(new byte[] { 7, 7 }, ThreeByteStart, Keyframe), 9), "Bytes before the first start code");
            Assert.IsFalse(ProtokitePlaytestH264.StartsWithAStartCode(new byte[] { 0, 0 }, 2));
            Assert.IsFalse(ProtokitePlaytestH264.StartsWithAStartCode(new byte[] { 0, 0, 1 }, 2), "Only the length given is read");
        }

        [Test]
        public void AStoredFrameKeepsItsPictureAfterItsLengthAndLeavesTheSettingsAndMarkerOut()
        {
            byte[] stream = Stream(FourByteStart, Marker, FourByteStart, Sequence, FourByteStart, Picture, FourByteStart, Extra, FourByteStart, Keyframe);
            byte[] stored = ProtokitePlaytestH264.FrameAsStored(ProtokitePlaytestH264.SplitUnits(stream, stream.Length));
            byte[] expected = Stream(new byte[] { 0, 0, 0, (byte)Extra.Length }, Extra, new byte[] { 0, 0, 0, (byte)Keyframe.Length }, Keyframe);
            CollectionAssert.AreEqual(expected, stored);
        }

        [Test]
        public void OnlyAKeyframesPictureMakesAKeyframe()
        {
            byte[] key = Stream(ThreeByteStart, Sequence, ThreeByteStart, Keyframe);
            Assert.IsTrue(ProtokitePlaytestH264.HoldsKeyframe(ProtokitePlaytestH264.SplitUnits(key, key.Length)));
            byte[] other = Stream(ThreeByteStart, Sequence, ThreeByteStart, OtherFrame);
            Assert.IsFalse(ProtokitePlaytestH264.HoldsKeyframe(ProtokitePlaytestH264.SplitUnits(other, other.Length)), "Settings alone do not make a keyframe");
        }

        [Test]
        public void TheDecoderSettingsRecordCarriesProfileLevelAndBothSettings()
        {
            byte[] record = ProtokitePlaytestH264.DecoderSettingsRecord(Sequence, Picture);
            byte[] expected = Stream(
                new byte[] { 1, 0x64, 0x00, 0x1F, 0xFF, 0xE1, 0, (byte)Sequence.Length }, Sequence,
                new byte[] { 1, 0, (byte)Picture.Length }, Picture,
                // The High profile also says 4:2:0 at eight bits, with no extension settings.
                new byte[] { 0xFD, 0xF8, 0xF8, 0 });
            CollectionAssert.AreEqual(expected, record);
        }

        [Test]
        public void TheMainProfilesRecordHasNoChromaFormat()
        {
            byte[] main = { 0x67, 0x4D, 0x40, 0x1F, 0x96 };
            byte[] record = ProtokitePlaytestH264.DecoderSettingsRecord(main, Picture);
            Assert.AreEqual(6 + 2 + main.Length + 1 + 2 + Picture.Length, record.Length);
            Assert.AreEqual(0x4D, record[1]);
            Assert.AreEqual(0xFF, record[4], "Four-byte unit lengths");
        }

        [Test]
        public void NoRecordWithoutBothSettings()
        {
            Assert.IsNull(ProtokitePlaytestH264.DecoderSettingsRecord(null, Picture));
            Assert.IsNull(ProtokitePlaytestH264.DecoderSettingsRecord(Sequence, null));
            Assert.IsNull(ProtokitePlaytestH264.DecoderSettingsRecord(new byte[] { 0x67, 0x64 }, Picture), "Sequence settings too short to say a profile and level");
        }

        [TestCase(new byte[] { 0, 0, 0, 4, 0x65, 1, 2, 3 }, 8, true, TestName = "A keyframe's picture whose length fills the frame")]
        [TestCase(new byte[] { 0, 0, 0, 2, 0x41, 1, 0, 0, 0, 1, 0x41 }, 11, true, TestName = "A first unit shorter than the frame")]
        [TestCase(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, 8, false, TestName = "Zeros the disk never filled in")]
        [TestCase(new byte[] { 0, 0, 0, 9, 0x65, 1, 2, 3 }, 8, false, TestName = "A length past the end of the frame")]
        [TestCase(new byte[] { 0, 0, 0, 4, 0xE5, 1, 2, 3 }, 8, false, TestName = "A unit whose top bit is set")]
        [TestCase(new byte[] { 0, 0, 0, 4, 0x18, 1, 2, 3 }, 8, false, TestName = "A unit of a type no stream holds")]
        [TestCase(new byte[] { 0, 0, 0, 4 }, 4, false, TestName = "Too short to hold a unit")]
        public void AStoredFrameIsRecognisedByItsFirstUnit(byte[] frame, int frameLength, bool expected)
        {
            Assert.AreEqual(expected, ProtokitePlaytestH264.LooksLikeAStoredFrame(frame, 0, Math.Min(frame.Length, 5), frameLength));
        }
    }
}
