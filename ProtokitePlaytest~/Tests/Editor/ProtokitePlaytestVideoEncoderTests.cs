using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestVideoEncoderTests
    {
        private const int FramesPerSecond = 15;
        private const long FrameMs = 67;

        [SetUp]
        public void SetUp() => ProtokitePlaytestVideoEncoders.ResetForNewLaunch();

        [TearDown]
        public void TearDown()
        {
            ProtokitePlaytestVideoEncoders.ReadProcessArchitectureForTesting = null;
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = null;
            ProtokitePlaytestVideoEncoders.LongestWaitForAnAnswerForTesting = null;
            ProtokitePlaytestVideoEncoders.WaitForTheAnswerForTesting = null;
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
        }

        private static ProtokitePlaytestEncoderFound OnTheGraphicsCard(string name, int vendorId = 0x10DE) =>
            new ProtokitePlaytestEncoderFound { Name = name, InHardware = true, VendorId = vendorId };

        private static readonly ProtokitePlaytestEncoderFound Software = new ProtokitePlaytestEncoderFound { Name = "H264 Encoder MFT" };

        // Stands in for what Windows offers on this PC, counting the times it was asked.
        private static Func<int> PcOffers(params ProtokitePlaytestEncoderFound[] found)
        {
            int asked = 0;
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
            {
                asked++;
                return new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(found.ToList(), null);
            };
            return () => asked;
        }

        // Whether this PC records, and why not

        [Test]
        public void APcWithAGraphicsCardEncoderRecords()
        {
            PcOffers(OnTheGraphicsCard("NVIDIA H.264 Encoder MFT"), Software);
            using (IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(false, out string whyNot))
            {
                Assert.IsNotNull(encoder, whyNot);
                Assert.AreEqual(ProtokitePlaytestPixelFormat.Nv12, encoder.InputPixelFormat);
            }
        }

        [Test]
        public void NoGraphicsCardEncoderMeansNoVideoReportedOnceNamingTheSettingThatAllowsTheSoftwareOne()
        {
            PcOffers(Software);
            LogAssert.Expect(LogType.Warning, new Regex("records no playtest video: this PC's graphics card has no H.264 encoder.*Allow Software Encoder.*Everything else"));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(false, out string whyNot));
            StringAssert.Contains("frame rate", whyNot, "The setting's cost is named with it");
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(false, out _), "Still no video, said once");
            LogAssert.NoUnexpectedReceived();

            using (IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(true, out whyNot))
                Assert.IsNotNull(encoder, "Allowed, Windows' software encoder records: " + whyNot);
        }

        [Test]
        public void APcWithNoEncoderAtAllRecordsNothingEvenWithTheSoftwareOneAllowed()
        {
            PcOffers();
            LogAssert.Expect(LogType.Warning, new Regex("records no playtest video: Windows offers no H.264 encoder on this PC"));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(true, out string whyNot));
            Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoEncoderAtAll, whyNot);
        }

        [Test]
        public void WindowsWithoutMediaFoundationIsNamedForWhatItIs()
        {
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(null, ProtokitePlaytestVideoEncoders.MediaFoundationMissing);
            LogAssert.Expect(LogType.Warning, new Regex("records no playtest video: Windows' Media Foundation.*Windows N edition.*Media Feature Pack"));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(true, out string whyNot));
            StringAssert.Contains("Media Feature Pack", whyNot);
        }

        [TestCase(Architecture.X86)]
        [TestCase(Architecture.Arm64)]
        public void A32BitOrArmWindowsBuildIsToldWhereVideoRecordsWithoutAWarning(Architecture architecture)
        {
            Func<int> asked = PcOffers(OnTheGraphicsCard("NVIDIA H.264 Encoder MFT"));
            ProtokitePlaytestVideoEncoders.ReadProcessArchitectureForTesting = () => architecture;
            LogAssert.Expect(LogType.Log, new Regex("records no playtest video: video is recorded on 64-bit Windows and Android only"));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(false, out string whyNot));
            StringAssert.Contains("64-bit Windows and Android only", whyNot);
            Assert.AreEqual(0, asked(), "Windows is not asked about encoders a build of this kind does not use");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnX64ProcessAndOneThatCannotNameItsArchitectureBothAskWindows()
        {
            PcOffers(OnTheGraphicsCard("NVIDIA H.264 Encoder MFT"));
            ProtokitePlaytestVideoEncoders.ReadProcessArchitectureForTesting = () => Architecture.X64;
            Assert.IsNull(ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false));
            ProtokitePlaytestVideoEncoders.ReadProcessArchitectureForTesting = () => throw new PlatformNotSupportedException();
            Assert.IsNull(ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false), "An answer it cannot give still tries");
        }

        [Test]
        public void WindowsIsAskedOnceALaunchAndAskedAgainByTheNext()
        {
            Func<int> asked = PcOffers(OnTheGraphicsCard("NVIDIA H.264 Encoder MFT"));
            Func<KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>> answers = ProtokitePlaytestVideoEncoders.LookForEncodersForTesting;
            Thread looking;
            // The answer is held until the second start, so that start meets the asking still under way; never disposed while waited on.
            ManualResetEventSlim answer = new ManualResetEventSlim();
            try
            {
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                {
                    answer.Wait(TimeSpan.FromSeconds(10));
                    return answers();
                };
                looking = ProtokitePlaytestVideoEncoders.StartLookingForEncoders();
                Assert.IsNotNull(looking, "Asked on a thread of its own, as Windows is");
                Assert.AreSame(looking, ProtokitePlaytestVideoEncoders.StartLookingForEncoders(), "Asked once, however often it is started");
            }
            finally
            {
                answer.Set();
            }
            Assert.IsTrue(looking.Join(TimeSpan.FromSeconds(10)));
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = answers;
            ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false);
            ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(true);
            ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out _);
            Assert.IsNull(ProtokitePlaytestVideoEncoders.StartLookingForEncoders(), "Answered: nothing more to ask");
            Assert.AreEqual(1, asked());
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out _);
            Assert.AreEqual(2, asked(), "A new launch asks again");
        }

        [Test]
        public void ACheckAnEarlierLaunchStartedNeverAnswersThisOne()
        {
            ManualResetEventSlim answer = new ManualResetEventSlim();
            try
            {
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                {
                    answer.Wait(TimeSpan.FromSeconds(10));
                    return new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(new List<ProtokitePlaytestEncoderFound> { OnTheGraphicsCard("Last Launch's Encoder") }, null);
                };
                Thread lastLaunchs = ProtokitePlaytestVideoEncoders.StartLookingForEncoders();
                Assert.IsNotNull(lastLaunchs, "Precondition: the last launch's check is under way");

                ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
                PcOffers(Software);
                Assert.IsNull(ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(true), "This launch's own check answers it");
                answer.Set();
                Assert.IsTrue(lastLaunchs.Join(TimeSpan.FromSeconds(10)), "Precondition: the last launch's check has answered too");

                List<ProtokitePlaytestEncoderFound> found = ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone);
                CollectionAssert.AreEqual(new[] { Software.Name }, found?.Select(encoder => encoder.Name).ToArray(), "A late answer from the last launch changes nothing: " + whyNone);
                Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoGraphicsCardEncoder, ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false));
            }
            finally
            {
                // Released whatever happened, and never disposed while a check still waits on it.
                answer.Set();
            }
        }

        [Test]
        public void AnAnswerThatTakesTooLongIsWaitedForNoLongerThanItsTimeAndSaysSo()
        {
            ManualResetEventSlim answer = new ManualResetEventSlim();
            try
            {
                ProtokitePlaytestVideoEncoders.LongestWaitForAnAnswerForTesting = TimeSpan.FromMilliseconds(300);
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                {
                    answer.Wait(TimeSpan.FromSeconds(10));
                    return new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(new List<ProtokitePlaytestEncoderFound> { OnTheGraphicsCard("A Slow Encoder") }, null);
                };
                Assert.IsFalse(ProtokitePlaytestVideoEncoders.FinishedLookingForEncoders(), "Asked and not answered: a recording waits, without the game waiting");
                Thread looking = ProtokitePlaytestVideoEncoders.StartLookingForEncoders();

                System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsNull(ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone));
                Assert.Less(waited.ElapsedMilliseconds, 3000, "Waited only what was left of the time to answer");
                Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoAnswerInTime, whyNone);
                Assert.IsTrue(ProtokitePlaytestVideoEncoders.FinishedLookingForEncoders(), "Once the time is up a recording goes on, told why there is no video");
                waited.Restart();
                ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false);
                Assert.Less(waited.ElapsedMilliseconds, 200, "The time is not waited for a second time");

                answer.Set();
                Assert.IsTrue(looking.Join(TimeSpan.FromSeconds(10)));
                Assert.IsNull(ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false), "An answer that comes late still counts for the launch");
            }
            finally
            {
                answer.Set();
            }
        }

        [Test]
        public void AWaitThatWakesEarly_IsWaitedAgain_SoTheCheckAgreesItsTimeIsUp()
        {
            ManualResetEventSlim answer = new ManualResetEventSlim();
            int waits = 0;
            try
            {
                ProtokitePlaytestVideoEncoders.LongestWaitForAnAnswerForTesting = TimeSpan.FromMilliseconds(300);
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                {
                    answer.Wait(TimeSpan.FromSeconds(10));
                    return new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(new List<ProtokitePlaytestEncoderFound> { OnTheGraphicsCard("A Slow Encoder") }, null);
                };
                // Every wait wakes after a quarter of its time, as a coarse timer can wake before the time it was given.
                ProtokitePlaytestVideoEncoders.WaitForTheAnswerForTesting = (looking, left) =>
                {
                    waits++;
                    looking.Join(TimeSpan.FromTicks(left.Ticks / 4));
                };

                Assert.IsNull(ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone));
                Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoAnswerInTime, whyNone);
                Assert.IsTrue(ProtokitePlaytestVideoEncoders.FinishedLookingForEncoders(), "Told there was no answer in time, the check agrees its time is up");
                Assert.Greater(waits, 1, "Control: the wait woke early and was waited again");
            }
            finally
            {
                answer.Set();
            }
        }

#if UNITY_EDITOR_WIN
        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void ThisPcIsAskedOnAThreadOfItsOwn()
        {
            int apartmentBefore = CoGetApartmentType(out int typeBefore, out _);
            Thread looking = ProtokitePlaytestVideoEncoders.StartLookingForEncoders();
            Assert.IsNotNull(looking, "Asked on a thread, so the game's main thread does not wait for Windows");
            Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, looking.ManagedThreadId);
            List<ProtokitePlaytestEncoderFound> found = ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone);
            Assert.IsNotNull(found, whyNone);
            Assert.IsTrue(found.Any(encoder => !encoder.InHardware), "Windows' own software encoder is offered on every PC with Media Foundation");
            Assert.AreEqual((apartmentBefore, typeBefore), (CoGetApartmentType(out int typeAfter, out _), typeAfter), "The main thread's COM is left as it was");
        }

        [DllImport("ole32.dll")] private static extern int CoGetApartmentType(out int type, out int qualifier);

        // The real encoder, on a thread of its own as a recording's

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void EveryFrameComesOutInOrderOnTheGraphicsCardAndWindowsDecodesThePictureSent()
        {
            RealEncoding.AssumeAGraphicsCardEncoder();
            const int frames = 30;
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            string description = null;
            RealEncoding.OnItsOwnThread(() =>
            {
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder())
                {
                    Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                    Assert.IsNull(encoder.Description, "Nothing has started before the first frame");
                    for (int i = 0; i < frames; i++)
                        Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(i), i * FrameMs, FrameMs, output, out error), error);
                    Assert.IsTrue(encoder.Finish(output, out error), error);
                    description = encoder.Description;
                }
            });

            StringAssert.Contains("(on the graphics card)", description, "The graphics card's encoder is the one used");
            Assert.AreEqual(frames, output.Count, "One compressed frame per frame sent: none held back, none dropped");
            CollectionAssert.AreEqual(Enumerable.Range(0, frames).Select(i => i * FrameMs).ToArray(), output.Select(frame => frame.TimestampMs).ToArray(),
                "In the order sent, at the times sent: no frame is held back to be shown out of order");
            Assert.IsTrue(output[0].IsKeyframe, "A video starts on a keyframe");
            Assert.IsNotNull(output[0].DecoderSettings, "And the first frame carries the stream's settings");
            Assert.IsTrue(output.Skip(1).All(frame => frame.DecoderSettings == null), "Only the first");
            Assert.IsTrue(output.All(frame => ProtokitePlaytestH264.LooksLikeAStoredFrame(frame.Data, 0, frame.Data.Length, frame.Data.Length)), "Every frame is stored the way an MP4 file keeps it");

            string path = Path.Combine(Path.GetTempPath(), "protokite-encoder-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
                Assert.IsTrue(file.Open(path, RealEncoding.Width, RealEncoding.Height, FrameMs, out string error), error);
                output.ForEach(frame => Assert.IsTrue(file.WriteFrame(frame, out _)));
                Assert.IsTrue(file.Close(out error), error);
                ProtokitePlaytestWindowsMp4Reader.Result read = ProtokitePlaytestWindowsMp4Reader.Read(path, index => true);
                Assert.IsNull(read.Error);
                Assert.AreEqual(frames, read.Pictures.Count);
                for (int i = 0; i < frames; i++)
                    Assert.That(RealEncoding.LumaPsnr(RealEncoding.MovingPicture(i), read.Pictures[i]), Is.GreaterThan(28), $"Frame {i} decodes to the picture that was sent");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void KeyframesComeAtTheIntervalAskedFor()
        {
            RealEncoding.AssumeAGraphicsCardEncoder();
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            RealEncoding.OnItsOwnThread(() =>
            {
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder())
                {
                    ProtokitePlaytestVideoEncoderSettings settings = Settings();
                    settings.KeyframeIntervalSeconds = 2;
                    Assert.IsTrue(encoder.Configure(settings, out string error), error);
                    for (int i = 0; i < 70; i++)
                        Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(i), i * FrameMs, FrameMs, output, out error), error);
                    Assert.IsTrue(encoder.Finish(output, out error), error);
                }
            });
            int[] keyframes = output.Select((frame, index) => frame.IsKeyframe ? index : -1).Where(index => index >= 0).ToArray();
            CollectionAssert.AreEqual(new[] { 0, 30, 60 }, keyframes, "Every two seconds at 15 frames a second, so a cut-off file plays from a recent point");
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AFrameOfTheWrongLengthIsRefusedBeforeTheEncoderReadsIt()
        {
            RealEncoding.AssumeAGraphicsCardEncoder();
            RealEncoding.OnItsOwnThread(() =>
            {
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder())
                {
                    Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                    List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
                    byte[] tooShort = new byte[ProtokitePlaytestNv12.FrameLength(RealEncoding.Width, RealEncoding.Height) - 1];
                    Assert.IsFalse(encoder.Encode(tooShort, 0, FrameMs, output, out error));
                    StringAssert.Contains($"not {tooShort.Length}", error);
                    Assert.IsFalse(encoder.Encode(null, 0, FrameMs, output, out error), "No frame at all");
                    Assert.IsNull(encoder.Description, "Nothing was started for a frame that was refused");
                    Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(0), 0, FrameMs, output, out error), "The encoder still works afterwards: " + error);
                }
            });
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void WindowsSoftwareEncoderIsUsedOnlyWhenAllowed()
        {
            string refused = null;
            string description = null;
            int frames = 0;
            RealEncoding.OnItsOwnThread(() =>
            {
                List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder { EncoderAllowedForTesting = found => !found.InHardware })
                {
                    Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                    Assert.IsFalse(encoder.Encode(RealEncoding.MovingPicture(0), 0, FrameMs, output, out refused), "A PC with no graphics card encoder, the software one not allowed");
                }
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder { EncoderAllowedForTesting = found => !found.InHardware })
                {
                    ProtokitePlaytestVideoEncoderSettings settings = Settings();
                    settings.AllowSoftwareEncoder = true;
                    Assert.IsTrue(encoder.Configure(settings, out string error), error);
                    for (int i = 0; i < 5; i++)
                        Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(i), i * FrameMs, FrameMs, output, out error), error);
                    Assert.IsTrue(encoder.Finish(output, out error), error);
                    description = encoder.Description;
                    frames = output.Count;
                }
            });
            StringAssert.Contains("Allow Software Encoder", refused);
            StringAssert.Contains("software encoder", description);
            Assert.AreEqual(5, frames);
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AnEncoderThatRefusesTheVideoIsNamedWithWhy()
        {
            string error = null;
            int tried = 0;
            RealEncoding.OnItsOwnThread(() =>
            {
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder { EncoderAllowedForTesting = found => ++tried > 0 })
                {
                    // Larger than any H.264 level allows, so every encoder refuses it, software included, whatever this PC has.
                    ProtokitePlaytestVideoEncoderSettings settings = Settings();
                    settings.Width = 16384;
                    settings.Height = 16384;
                    settings.AllowSoftwareEncoder = true;
                    Assert.IsTrue(encoder.Configure(settings, out error), error);
                    Assert.IsFalse(encoder.Start(out error));
                    Assert.IsFalse(encoder.Start(out string again));
                    StringAssert.Contains("failed earlier", again, "An encoder that failed takes no more frames");
                }
            });
            Assert.Greater(tried, 0, "Precondition: Windows offers at least its software encoder");
            const string refusedBy = "no H.264 encoder on this PC would take the video: ";
            StringAssert.StartsWith(refusedBy, error);
            Assert.AreEqual(tried, error.Substring(refusedBy.Length).Split(new[] { "; " }, StringSplitOptions.None).Length, "Each encoder tried is named with why: " + error);
            StringAssert.Contains("16384x16384", error, "The size refused is named");
        }

        // An encoder that hands frames back through events, acted out by a stand-in keeping Windows' rule for them

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void TheStandInRefusesAnOutputCallNoEventAllowedAsWindowsDoes()
        {
            StandInEventEncoder standIn = new StandInEventEncoder();
            ProtokitePlaytestMediaFoundation.OutputDataBuffer buffer = new ProtokitePlaytestMediaFoundation.OutputDataBuffer();
            Assert.AreEqual(StandInEventEncoder.Unexpected, standIn.ProcessOutput(0, 1, ref buffer, out _), "E_UNEXPECTED, 0x8000FFFF, what Intel's encoder answered on a laptop");
            Assert.AreEqual(1, standIn.CallsRefused);
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AnEncoderThatHandsFramesBackThroughEventsRecordsEveryFrameEvenWhenItsFormatChangesFirst(bool formatChangesFirst, bool settingsOnlyOnItsOutputType,
            bool needsABiggerBufferAfterTheChange)
        {
            StandInEventEncoder standIn = new StandInEventEncoder
            {
                ChangesFormatBeforeTheFirstFrame = formatChangesFirst,
                GivesSettingsOnlyOnItsOutputType = settingsOnlyOnItsOutputType,
                // Larger than a 256x144 frame, the buffer the encoder offers until the new type asks for more.
                OutputBufferBytesAfterTheChange = needsABiggerBufferAfterTheChange ? 200000u : 0u
            };
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            string description = null;
            RealEncoding.OnItsOwnThread(() =>
            {
                using (ProtokitePlaytestWindowsVideoEncoder encoder = new ProtokitePlaytestWindowsVideoEncoder { TransformForTesting = () => standIn })
                {
                    Assert.IsTrue(encoder.Configure(Settings(), out string error), error);
                    Assert.IsTrue(encoder.Start(out error), error);
                    Assert.IsTrue(encoder.IsAsynchronous, "Precondition: the stand-in hands frames back through events");
                    for (int i = 0; i < 5; i++)
                        Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(i), i * FrameMs, FrameMs, output, out error), error);
                    Assert.IsTrue(encoder.Finish(output, out error), error);
                    description = encoder.Description;
                }
            });
            Assert.AreEqual(formatChangesFirst ? 1 : 0, standIn.FormatChanges, "Precondition: the stand-in changed its output format only when told to");
            Assert.AreEqual(0, standIn.CallsRefused, "No call its events did not allow");
            CollectionAssert.AreEqual(Enumerable.Range(0, 5).Select(i => i * FrameMs).ToArray(), output.Select(frame => frame.TimestampMs).ToArray(),
                "Every frame comes out, in order, at its time");
            Assert.IsTrue(output[0].IsKeyframe);
            Assert.IsNotNull(output[0].DecoderSettings, "The first frame carries the stream's settings");
            Assert.AreEqual("a stand-in encoder", description);
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void TheGameGraphicsCardMakersEncoderIsTriedFirst()
        {
            List<ProtokitePlaytestEncoderFound> onThisPc = ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone);
            Assert.IsNotNull(onThisPc, whyNone);
            foreach (int vendorId in onThisPc.Where(encoder => encoder.InHardware && encoder.VendorId != 0).Select(encoder => encoder.VendorId).Distinct())
            {
                List<string> offered = null;
                RealEncoding.OnItsOwnThread(() =>
                {
                    bool comStarted = ProtokitePlaytestWindowsVideoEncoder.StartCom();
                    ProtokitePlaytestMediaFoundation.MFStartup(ProtokitePlaytestMediaFoundation.MediaFoundationVersion, ProtokitePlaytestMediaFoundation.StartupFull);
                    List<KeyValuePair<ProtokitePlaytestMediaFoundation.IMFActivate, ProtokitePlaytestEncoderFound>> found = ProtokitePlaytestWindowsVideoEncoder.Offered(false, vendorId);
                    offered = found.Select(encoder => $"{encoder.Value.Name} {encoder.Value.VendorId:X4}").ToList();
                    found.ForEach(encoder => ProtokitePlaytestMediaFoundation.Release(encoder.Key));
                    ProtokitePlaytestMediaFoundation.MFShutdown();
                    if (comStarted)
                        ProtokitePlaytestMediaFoundation.CoUninitialize();
                });
                StringAssert.EndsWith($"{vendorId:X4}", offered[0], $"With the game on vendor {vendorId:X4}'s card, its encoder comes first: {string.Join(", ", offered)}");
                Assert.IsFalse(offered.Any(name => name.StartsWith("H264 Encoder MFT ")), "The software encoder is not offered unless allowed");
            }
        }
#endif

        [Test]
        public void SettingsTheEncoderCannotTakeAreRefusedWithWhy()
        {
            (Action<ProtokitePlaytestVideoEncoderSettings> change, string why)[] cases =
            {
                (s => s.Width = 255, "even"),
                (s => s.Height = 8, "at least 16"),
                (s => s.FramesPerSecond = 0, "one frame a second"),
                (s => s.BitrateKbps = 0, "bitrate")
            };
            foreach ((Action<ProtokitePlaytestVideoEncoderSettings> change, string why) in cases)
            {
                ProtokitePlaytestVideoEncoderSettings settings = Settings();
                change(settings);
                IProtokitePlaytestVideoEncoder encoder = NewEncoderOrIgnore();
                Assert.IsFalse(encoder.Configure(settings, out string error), $"Expected a refusal about '{why}'");
                StringAssert.Contains(why, error);
            }
        }

        [Test]
        public void NothingIsEncodedBeforeConfiguringAndFinishingWithNoFrameHoldsNothing()
        {
            IProtokitePlaytestVideoEncoder encoder = NewEncoderOrIgnore();
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            Assert.IsFalse(encoder.Encode(RealEncoding.MovingPicture(0), 0, FrameMs, output, out string error));
            StringAssert.Contains("not configured", error);
            Assert.IsTrue(encoder.Configure(Settings(), out error), error);
            Assert.IsFalse(encoder.Configure(Settings(), out error), "Configured once");
            Assert.IsTrue(encoder.Finish(output, out error), "A recording that never had a frame finishes with nothing to hand over: " + error);
            CollectionAssert.IsEmpty(output);
            Assert.IsFalse(encoder.Encode(RealEncoding.MovingPicture(0), 0, FrameMs, output, out error));
            StringAssert.Contains("finished", error);
            encoder.Dispose();
            Assert.IsFalse(encoder.Finish(output, out error));
            StringAssert.Contains("closed", error);
        }

        [Test]
        public void TheDefaultsAreTheOnesTheSpikeMeasured()
        {
            ProtokitePlaytestVideoEncoderSettings defaults = ProtokitePlaytestVideoEncoderSettings.Defaults;
            Assert.AreEqual((1280, 720, 15, 1500, 10), (defaults.Width, defaults.Height, defaults.FramesPerSecond, defaults.BitrateKbps, defaults.KeyframeIntervalSeconds));
            Assert.IsFalse(defaults.AllowSoftwareEncoder, "Windows' software encoder costs the game frame rate, so a studio turns it on");
        }

        private static ProtokitePlaytestVideoEncoderSettings Settings() => new ProtokitePlaytestVideoEncoderSettings
        {
            Width = RealEncoding.Width,
            Height = RealEncoding.Height,
            FramesPerSecond = FramesPerSecond,
            BitrateKbps = 1500
        };

        private static IProtokitePlaytestVideoEncoder NewEncoderOrIgnore()
        {
#if UNITY_EDITOR_WIN
            return new ProtokitePlaytestWindowsVideoEncoder();
#else
            Assert.Ignore("Video is recorded on Windows only.");
            return null;
#endif
        }

        // What keeps the encoder's platform out of everything else, and the package free of native files

        [Test]
        public void NothingButTheWindowsEncoderFilesNameMediaFoundation()
        {
            string runtime = Path.Combine(PackageFolder().resolvedPath, "Runtime");
            string[] allowed = { "ProtokitePlaytestMediaFoundation.cs", "ProtokitePlaytestWindowsVideoEncoder.cs", "ProtokitePlaytestVideoEncoders.cs" };
            string[] naming = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
                .Where(file => !allowed.Contains(Path.GetFileName(file)))
                .Where(file => Regex.IsMatch(File.ReadAllText(file), "Media Foundation|mfplat|MFStartup|IMFTransform|ole32|CoInitialize"))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.IsTrue(allowed.All(name => File.Exists(Path.Combine(runtime, "Video", name))), "Positive control: the Windows encoder's files are where this scan expects");
            CollectionAssert.IsEmpty(naming, "Everything else goes through IProtokitePlaytestVideoEncoder");
        }

        [Test]
        public void ThePackageShipsNoNativeBinary()
        {
            // An unsigned DLL of the package's own was turned away by Windows 11's Smart App Control on players' PCs.
            string[] native = Directory.GetFiles(PackageFolder().resolvedPath, "*", SearchOption.AllDirectories)
                .Where(file => Regex.IsMatch(Path.GetExtension(file), @"^\.(dll|so|dylib|a|lib|bundle|exe)$", RegexOptions.IgnoreCase))
                .ToArray();
            CollectionAssert.IsEmpty(native, "Video is encoded by the platform's own encoders");
        }

        // The seam a second encoder plugs into

        [Test]
        public void TheFakeAndTheRealEncoderKeepTheSameContract()
        {
            List<IProtokitePlaytestVideoEncoder> encoders = new List<IProtokitePlaytestVideoEncoder> { new FakeH264Encoder() };
#if UNITY_EDITOR_WIN
            encoders.Add(new ProtokitePlaytestWindowsVideoEncoder());
#endif
            foreach (IProtokitePlaytestVideoEncoder encoder in encoders)
            {
                string name = encoder.GetType().Name;
                List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
                RealEncoding.OnItsOwnThread(() =>
                {
                    using (encoder)
                    {
                        Assert.AreEqual(ProtokitePlaytestPixelFormat.Nv12, encoder.InputPixelFormat, name);
                        // The software encoder allowed, so the contract is checked on a PC whose graphics card has no encoder too.
                        ProtokitePlaytestVideoEncoderSettings settings = Settings();
                        settings.AllowSoftwareEncoder = true;
                        Assert.IsTrue(encoder.Configure(settings, out string error), name + ": " + error);
                        for (int i = 0; i < 3; i++)
                            Assert.IsTrue(encoder.Encode(RealEncoding.MovingPicture(i), i * FrameMs, FrameMs, output, out error), name + ": " + error);
                        Assert.IsTrue(encoder.Finish(output, out error), name + ": " + error);
                        Assert.IsFalse(encoder.Encode(RealEncoding.MovingPicture(3), 3 * FrameMs, FrameMs, output, out _), name + " refuses frames after finishing");
                        Assert.IsNotNull(encoder.Description, name + " says what encoded the video");
                    }
                });
                Assert.AreEqual(3, output.Count, name);
                Assert.IsTrue(output[0].IsKeyframe, name);
                Assert.IsNotNull(output[0].DecoderSettings, name + "'s first frame carries the stream's settings");
            }
        }

        private static UnityEditor.PackageManager.PackageInfo PackageFolder()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly);
            Assert.IsNotNull(package, "The playtest is installed as a package in this project");
            return package;
        }
    }
}
