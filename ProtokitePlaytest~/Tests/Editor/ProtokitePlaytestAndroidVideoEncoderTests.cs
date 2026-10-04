using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    /// <summary>The phone's encoder as a recording drives it, against a stand-in that keeps MediaCodec's rules.</summary>
    public class ProtokitePlaytestAndroidVideoEncoderTests
    {
        private const int Width = 64;
        private const int Height = 32;
        private const long FrameMs = 67;

        private static ProtokitePlaytestEncoderFound PhoneEncoder(bool takesConstantBitrate = true)
            => new ProtokitePlaytestEncoderFound { Name = "c2.stand.in.avc.encoder", InHardware = true, OnAPhone = true, TakesConstantBitrate = takesConstantBitrate };

        private static ProtokitePlaytestAndroidVideoEncoder Over(ProtokitePlaytestStandInAndroidCodec codec, ProtokitePlaytestEncoderFound found = null)
            => new ProtokitePlaytestAndroidVideoEncoder(found ?? PhoneEncoder(), (out string whyNot) =>
            {
                whyNot = null;
                return codec;
            });

        private static ProtokitePlaytestVideoEncoderSettings Settings() => new ProtokitePlaytestVideoEncoderSettings
        {
            Width = Width, Height = Height, FramesPerSecond = 15, BitrateKbps = 1500, KeyframeIntervalSeconds = 10
        };

        // An NV12 frame whose every byte says where it is: row and column in the picture, so a copy to the wrong place shows.
        private static byte[] Frame()
        {
            byte[] pixels = new byte[ProtokitePlaytestNv12.FrameLength(Width, Height)];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(i % 251);
            return pixels;
        }

        // Encodes frames at 15 a second, then finishes; every frame that came out, in order.
        private static List<ProtokitePlaytestEncodedFrame> Record(ProtokitePlaytestAndroidVideoEncoder encoder, int frames, out string error)
        {
            List<ProtokitePlaytestEncodedFrame> all = new List<ProtokitePlaytestEncodedFrame>();
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            Assert.IsTrue(encoder.Configure(Settings(), out error), error);
            byte[] pixels = Frame();
            for (int i = 0; i < frames; i++)
            {
                output.Clear();
                if (!encoder.Encode(pixels, i * FrameMs, FrameMs, output, out error))
                    return all;
                all.AddRange(output);
            }
            output.Clear();
            if (encoder.Finish(output, out error))
                all.AddRange(output);
            return all;
        }

        [Test]
        public void EveryFrameComesOutOnceAtItsTimeWithTheStreamSettingsBeforeTheFirst()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                List<ProtokitePlaytestEncodedFrame> frames = Record(encoder, 5, out string error);
                Assert.IsNull(error);
                Assert.AreEqual(5, frames.Count, "One frame out for each in, the settings buffer not counted as one");
                CollectionAssert.AreEqual(new long[] { 0, 67, 134, 201, 268 }, frames.ConvertAll(frame => frame.TimestampMs), "Each at the time it went in");
                CollectionAssert.AreEqual(new long[] { 0, 67000, 134000, 201000, 268000 }, codec.TimesQueuedUs, "Handed to the codec in microseconds");
                Assert.IsNotNull(frames[0].DecoderSettings, "The first frame carries the stream's settings");
                Assert.AreEqual(100, frames[0].DecoderSettings[1], "From the sequence settings the codec gave (High profile)");
                Assert.IsTrue(frames.GetRange(1, 4).TrueForAll(frame => frame.DecoderSettings == null), "Only the first carries them");
                Assert.IsTrue(frames[0].IsKeyframe);
                Assert.IsFalse(frames[1].IsKeyframe);
                Assert.IsTrue(codec.EndOfStreamQueued, "Finishing hands the codec the end of the stream");
                StringAssert.Contains("c2.stand.in.avc.encoder (the phone's hardware encoder)", encoder.Description);
            }
        }

        [Test]
        public void SettingsGivenOnlyOnTheOutputFormatStillGoOutWithTheFirstFrame()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { SettingsInABufferOfTheirOwn = false, SettingsOnlyOnTheOutputFormat = true };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                List<ProtokitePlaytestEncodedFrame> frames = Record(encoder, 3, out string error);
                Assert.IsNull(error);
                Assert.AreEqual(3, frames.Count);
                Assert.IsNotNull(frames[0].DecoderSettings, "Read off the format change");
            }
        }

        [Test]
        public void SettingsInFrontOfTheFirstPictureGoOutWithItAndTheFrameKeepsOnlyThePicture()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { SettingsInABufferOfTheirOwn = false, SettingsInFrontOfTheFirstPicture = true };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                List<ProtokitePlaytestEncodedFrame> frames = Record(encoder, 3, out string error);
                Assert.IsNull(error);
                Assert.AreEqual(3, frames.Count, "The first buffer holds a picture, so it is a frame, not settings alone");
                Assert.IsNotNull(frames[0].DecoderSettings, "Read from the units in front of the picture");
                Assert.IsTrue(frames[0].IsKeyframe);
                Assert.AreEqual(4 + 3, frames[0].Data.Length, "Stored as the picture's one unit after its length; the settings go in the file's header");
                Assert.IsNull(frames[1].DecoderSettings, "Handed over once");
            }
        }

        [Test]
        public void AStreamWithNoSettingsAnywhereFailsWithWhy()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { SettingsInABufferOfTheirOwn = false };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Record(encoder, 2, out string error);
                StringAssert.Contains("no sequence and picture settings with its first frame", error);
            }
        }

        [Test]
        public void RowsGoInAtTheStrideAndRowCountTheCodecAsksFor()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { Stride = 80, SliceHeight = 40 };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Record(encoder, 1, out string error);
                Assert.IsNull(error);
                byte[] pixels = Frame();
                byte[] queued = codec.FramesQueued[0];
                Assert.AreEqual(80 * 40 + 80 * (Height / 2), queued.Length, "Both planes at the codec's layout");
                for (int row = 0; row < Height; row++)
                    Assert.AreEqual(pixels[row * Width + Width - 1], queued[row * 80 + Width - 1], $"Brightness row {row} at its stride");
                for (int row = 0; row < Height / 2; row++)
                    Assert.AreEqual(pixels[Width * Height + row * Width], queued[80 * 40 + row * 80], $"Colour row {row} after the first plane's 40 rows");
            }
        }

        [Test]
        public void ACodecThatDoesNotSayItsLayoutTakesTheFramePacked()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Record(encoder, 1, out string error);
                Assert.IsNull(error);
                CollectionAssert.AreEqual(Frame(), codec.FramesQueued[0]);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AConstantBitrateIsAskedForOnlyWhereTheEncoderTakesOne(bool takesConstantBitrate)
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec, PhoneEncoder(takesConstantBitrate)))
            {
                Record(encoder, 1, out string error);
                Assert.IsNull(error);
                Assert.AreEqual(takesConstantBitrate, codec.ConstantBitrateAsked);
                Assert.AreEqual(1500000, codec.BitsPerSecond, "Kilobits a second handed over as bits");
                Assert.AreEqual(15, codec.FramesPerSecond);
                Assert.AreEqual(10, codec.KeyframeIntervalSeconds);
                Assert.AreEqual(Width, codec.Width);
                Assert.AreEqual(Height, codec.Height);
            }
        }

        [Test]
        public void ACodecThatRefusesTheSettingsSaysWhichAndIsGivenBackWithoutStopping()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { ConfigureStatus = -10000 };
            ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec);
            Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
            Assert.IsFalse(encoder.Start(out error));
            Assert.AreEqual("c2.stand.in.avc.encoder (the phone's hardware encoder) refused 64x32 at 15 frames a second and 1500 kbps (status -10000).", error);
            encoder.Dispose();
            Assert.AreEqual(0, codec.StopCalls, "Never started, so never stopped");
            Assert.AreEqual(1, codec.DisposeCalls, "But given back");
        }

        [Test]
        public void ACodecThatWillNotStartSaysSo()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { StartStatus = -38 };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsFalse(encoder.Start(out error));
                StringAssert.EndsWith("would not start (status -38).", error);
            }
        }

        [Test]
        public void AStartTriedAgainGivesBackTheCodecTheFailedStartLeft()
        {
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            ProtokitePlaytestAndroidVideoEncoder encoder = new ProtokitePlaytestAndroidVideoEncoder(PhoneEncoder(), (out string whyNot) =>
            {
                whyNot = null;
                // The first refuses its settings; the second starts.
                ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { ConfigureStatus = made.Count == 0 ? -10000 : 0 };
                made.Add(codec);
                return codec;
            });
            Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
            Assert.IsFalse(encoder.Start(out error), "Precondition: the first codec refuses");
            Assert.IsTrue(encoder.Start(out error), error);
            Assert.AreEqual(2, made.Count);
            Assert.AreEqual(1, made[0].DisposeCalls, "The refused codec is given back, not left for the collector");
            Assert.AreEqual(0, made[1].DisposeCalls);
            encoder.Dispose();
            Assert.AreEqual(1, made[0].DisposeCalls, "Given back once");
            Assert.AreEqual(1, made[1].DisposeCalls);
        }

        [Test]
        public void ACodecThePhoneWillNotMakeSaysWhy()
        {
            ProtokitePlaytestAndroidVideoEncoder encoder = new ProtokitePlaytestAndroidVideoEncoder(PhoneEncoder(), (out string whyNot) =>
            {
                whyNot = "the phone would not create its encoder c2.stand.in.avc.encoder.";
                return null;
            });
            Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
            Assert.IsFalse(encoder.Start(out error));
            Assert.AreEqual("the phone would not create its encoder c2.stand.in.avc.encoder.", error);
            encoder.Dispose();
        }

        [Test]
        public void AMediaLibraryThatCannotBeReachedSaysSo()
        {
            ProtokitePlaytestAndroidVideoEncoder encoder = new ProtokitePlaytestAndroidVideoEncoder(PhoneEncoder(), (out string whyNot) => throw new DllNotFoundException("mediandk"));
            Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
            Assert.IsFalse(encoder.Start(out error));
            StringAssert.StartsWith("Android's media library, which encodes the video, could not be reached on this phone", error);
            encoder.Dispose();
        }

        [Test]
        public void TheRealCodecOutsideAPhoneSaysTheLibraryCannotBeReached()
        {
            // The editor has no Android media library, so the real path's failure is the one a broken phone would show.
            IProtokitePlaytestAndroidCodec codec = ProtokitePlaytestMediaCodec.Create("c2.qti.avc.encoder", out string whyNot);
            Assert.IsNull(codec);
            StringAssert.StartsWith("Android's media library, which encodes the video, could not be reached on this phone", whyNot);
        }

        [Test]
        public void FramesAnEncoderHoldsComeOutAtTheEnd()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { FramesHeldUntilTheEnd = 3 };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                List<ProtokitePlaytestEncodedFrame> frames = Record(encoder, 5, out string error);
                Assert.IsNull(error);
                Assert.AreEqual(5, frames.Count, "The held frames are taken once the end of the stream is handed in");
                CollectionAssert.AreEqual(new long[] { 0, 67, 134, 201, 268 }, frames.ConvertAll(frame => frame.TimestampMs));
            }
        }

        [Test]
        public void AnEncoderThatNeverFreesABufferIsGivenUpOnAfterTwoSeconds()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { NeverFreesAnInputBuffer = true };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Stopwatch waited = Stopwatch.StartNew();
                Assert.IsFalse(encoder.Encode(Frame(), 0, FrameMs, new List<ProtokitePlaytestEncodedFrame>(), out error));
                StringAssert.EndsWith("took no frame for 2 seconds.", error);
                Assert.That(waited.Elapsed.TotalSeconds, Is.InRange(1.9, 5.0), "Waited its bound, not for ever and not at once");
            }
        }

        [Test]
        public void OutputThatIsNotAnH264ByteStreamFailsWithWhy()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { SettingsInABufferOfTheirOwn = false, HandsBackNoStartCode = true };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Record(encoder, 1, out string error);
                StringAssert.Contains("in a form other than an H.264 byte stream", error);
            }
        }

        [Test]
        public void OutputPastItsBufferIsNotRead()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec { HandsBackPastItsBuffer = true };
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                Record(encoder, 1, out string error);
                StringAssert.EndsWith("handed back a frame outside its buffer.", error);
            }
        }

        [Test]
        public void AFrameOutOfTimeOrderOrAfterTheEndIsRefused()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec))
            {
                List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(encoder.Encode(Frame(), 100, FrameMs, output, out error), error);
                Assert.IsFalse(encoder.Encode(Frame(), 100, FrameMs, output, out error));
                StringAssert.Contains("came after one at 100 ms", error);
                Assert.IsTrue(encoder.Finish(output, out error), error);
                Assert.IsFalse(encoder.Encode(Frame(), 500, FrameMs, output, out error));
                Assert.AreEqual("the encoder was given a frame after it finished.", error);
            }
        }

        [Test]
        public void ItIsStoppedAndGivenBackOnce()
        {
            ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec();
            ProtokitePlaytestAndroidVideoEncoder encoder = Over(codec);
            Record(encoder, 1, out string error);
            Assert.IsNull(error);
            encoder.Dispose();
            encoder.Dispose();
            Assert.AreEqual(1, codec.StopCalls);
            Assert.AreEqual(1, codec.DisposeCalls);
        }
    }
}
