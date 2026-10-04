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

        // Going to the background

        // A codec of its own each time the encoder makes one, as the phone gives a new one after the background; the test sets each up.
        private static ProtokitePlaytestAndroidVideoEncoder OverCodecsInTurn(List<ProtokitePlaytestStandInAndroidCodec> made,
            Action<ProtokitePlaytestStandInAndroidCodec, int> setUp = null)
            => new ProtokitePlaytestAndroidVideoEncoder(PhoneEncoder(), (out string whyNot) =>
            {
                whyNot = null;
                ProtokitePlaytestStandInAndroidCodec codec = new ProtokitePlaytestStandInAndroidCodec();
                setUp?.Invoke(codec, made.Count);
                made.Add(codec);
                return codec;
            });

        // Encodes frames one a frame time apart from the time given; false with why at the first refused.
        private static bool Encode(ProtokitePlaytestAndroidVideoEncoder encoder, long fromMs, int frames, List<ProtokitePlaytestEncodedFrame> all, out string error)
        {
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            byte[] pixels = Frame();
            for (int i = 0; i < frames; i++)
            {
                output.Clear();
                if (!encoder.Encode(pixels, fromMs + i * FrameMs, FrameMs, output, out error))
                    return false;
                all.AddRange(output);
            }
            error = null;
            return true;
        }

        private static bool LetGo(ProtokitePlaytestAndroidVideoEncoder encoder, List<ProtokitePlaytestEncodedFrame> all, out string error)
        {
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            bool letGo = encoder.HandOverEverythingAndLetGo(output, out error);
            all.AddRange(output);
            return letGo;
        }

        [Test]
        public void LettingGoHandsOverEveryFrameTheCodecHeldAndGivesTheCodecBack()
        {
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made, (codec, index) => codec.FramesHeldUntilTheEnd = 2))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(Encode(encoder, 0, 4, frames, out error), error);
                Assert.AreEqual(2, frames.Count, "Precondition: the codec still holds the last two frames");

                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                CollectionAssert.AreEqual(new long[] { 0, 67, 134, 201 }, frames.ConvertAll(frame => frame.TimestampMs), "The frames it held come out as it lets go");
                Assert.IsTrue(made[0].EndOfStreamQueued, "Its stream is ended, which is what makes a codec hand back what it holds");
                Assert.AreEqual(1, made[0].StopCalls, "Stopped");
                Assert.AreEqual(1, made[0].DisposeCalls, "And given back to the phone while the game is away");
            }
            Assert.AreEqual(1, made.Count, "No codec is made until a frame needs one");
            Assert.AreEqual(1, made[0].DisposeCalls, "Disposing afterwards gives nothing back twice");
        }

        [Test]
        public void TheNextFrameStartsANewCodecOnAKeyframeAndTheVideoGoesOn()
        {
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(Encode(encoder, 0, 3, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                Assert.IsTrue(Encode(encoder, 1000, 3, frames, out error), "The first frame after the background starts a new codec: " + error);
                Assert.IsTrue(encoder.Finish(frames, out error), error);

                Assert.AreEqual(2, made.Count, "A codec of its own after the background");
                Assert.AreEqual(64, made[1].Width, "Configured as the first was");
                Assert.AreEqual(1500000, made[1].BitsPerSecond);
                CollectionAssert.AreEqual(new long[] { 0, 67, 134, 1000, 1067, 1134 }, frames.ConvertAll(frame => frame.TimestampMs));
                Assert.IsTrue(frames[3].IsKeyframe, "The new stream starts on a keyframe");
                Assert.IsNull(frames[3].DecoderSettings, "The video's settings were handed over once, with its first frame");
                Assert.IsTrue(made[1].EndOfStreamQueued, "Finishing ends the new stream");
            }
        }

        [Test]
        public void ANewStreamDescribedOtherwiseEndsTheVideoWhereTheGameLeft()
        {
            byte[] otherSettings = { 0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40 };
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made, (codec, index) =>
                   {
                       if (index == 1)
                           codec.SequenceSettingsSent = otherSettings;
                   }))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(Encode(encoder, 0, 3, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                Assert.IsFalse(Encode(encoder, 1000, 3, frames, out error), "A frame the file's header does not describe is never written");
                StringAssert.Contains("came back from the background with stream settings other than the video's, so the video ends where the game left", error);
                Assert.AreEqual(3, frames.Count, "Only the frames before the background");
            }
        }

        [Test]
        public void ANewStreamWithSettingsOnlyOnItsOutputFormatIsCheckedToo()
        {
            byte[] otherSettings = { 0x67, 0x64, 0x00, 0x28, 0xAC, 0xD9, 0x40 };
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made, (codec, index) =>
                   {
                       codec.SettingsInABufferOfTheirOwn = false;
                       codec.SettingsOnlyOnTheOutputFormat = true;
                       if (index == 1)
                           codec.SequenceSettingsSent = otherSettings;
                   }))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(Encode(encoder, 0, 3, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                Assert.IsFalse(Encode(encoder, 1000, 3, frames, out error), "The new stream's own settings are read, not the first stream's kept");
                StringAssert.Contains("stream settings other than the video's", error);
            }
        }

        [Test]
        public void ANewStreamThatDoesNotStartOnAKeyframeEndsTheVideo()
        {
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made, (codec, index) => codec.FirstPictureNeedsAnEarlierOne = index == 1))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(Encode(encoder, 0, 3, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                Assert.IsFalse(Encode(encoder, 1000, 3, frames, out error));
                StringAssert.Contains("came back from the background with no keyframe to start from", error);
            }
        }

        [Test]
        public void LettingGoBeforeAnyFrameOrTwiceInARowHandsOverNothing()
        {
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), "Nothing to hand over before the first frame: " + error);
                Assert.AreEqual(0, made.Count, "No codec is made to be let go");
                Assert.IsTrue(Encode(encoder, 0, 2, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), "A second time away with no frame between: " + error);
                Assert.AreEqual(1, made[0].StopCalls, "Stopped once");
                Assert.AreEqual(2, frames.Count);
                Assert.IsTrue(encoder.Finish(frames, out error), "Finishing with no codec running is nothing to do: " + error);
            }
        }

        [Test]
        public void AFrameOlderThanOneBeforeTheBackgroundIsStillRefused()
        {
            List<ProtokitePlaytestStandInAndroidCodec> made = new List<ProtokitePlaytestStandInAndroidCodec>();
            using (ProtokitePlaytestAndroidVideoEncoder encoder = OverCodecsInTurn(made))
            {
                List<ProtokitePlaytestEncodedFrame> frames = new List<ProtokitePlaytestEncodedFrame>();
                Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                Assert.IsTrue(Encode(encoder, 0, 3, frames, out error), error);
                Assert.IsTrue(LetGo(encoder, frames, out error), error);
                Assert.IsFalse(Encode(encoder, 100, 1, frames, out error), "The file takes each frame later than the one before, across the background too");
                StringAssert.Contains("came after one at 134 ms", error);
            }
        }
    }
}
