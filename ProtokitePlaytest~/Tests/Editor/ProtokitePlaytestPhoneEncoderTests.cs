using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    /// <summary>Which of a phone's encoders records, why none does, and the size it records at, decided as an Android player decides.</summary>
    public class ProtokitePlaytestPhoneEncoderTests
    {
        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = true;
        }

        [TearDown]
        public void TearDown()
        {
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = false;
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = null;
            ProtokitePlaytestVideoEncoders.LongestWaitForAnAnswerForTesting = null;
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
        }

        private static ProtokitePlaytestEncoderFound Hardware(string name, bool takesNv12 = true)
            => new ProtokitePlaytestEncoderFound { Name = name, InHardware = true, OnAPhone = true, TakesNv12 = takesNv12, TakesConstantBitrate = true };

        private static ProtokitePlaytestEncoderFound Software(string name, bool takesNv12 = true)
            => new ProtokitePlaytestEncoderFound { Name = name, InHardware = false, OnAPhone = true, TakesNv12 = takesNv12 };

        // Stands in for the phone's list of H.264 encoders, in the order Android prefers them.
        private static void PhoneOffers(params ProtokitePlaytestEncoderFound[] found)
            => ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () => new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(found.ToList(), null);

        private static ProtokitePlaytestEncoderFound Chosen(IProtokitePlaytestVideoEncoder encoder)
        {
            Assert.IsInstanceOf<ProtokitePlaytestAndroidVideoEncoder>(encoder, "A phone records with the phone's encoder");
            return ((ProtokitePlaytestAndroidVideoEncoder)encoder).Found;
        }

        // Which encoder records

        [Test]
        public void TheFirstHardwareEncoderThatTakesTheCapturesFramesIsChosen()
        {
            ProtokitePlaytestEncoderFound wanted = Hardware("c2.vendor.avc.encoder");
            PhoneOffers(Software("c2.android.avc.encoder"), Hardware("c2.other.avc.encoder", takesNv12: false), wanted, Hardware("c2.later.avc.encoder"));
            using (IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(false, out string whyNot))
                Assert.AreSame(wanted, Chosen(encoder), whyNot);
            using (IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(true, out string whyNot))
                Assert.AreSame(wanted, Chosen(encoder), "Hardware first even where software is allowed");
        }

        [Test]
        public void APhoneWithOnlyASoftwareEncoderRecordsOnlyWhereTheStudioAllowsIt()
        {
            ProtokitePlaytestEncoderFound software = Software("c2.android.avc.encoder");
            PhoneOffers(software);
            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape("This launch records no playtest video: " + ProtokitePlaytestVideoEncoders.NoHardwareEncoderOnThisPhone)));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(false, out string whyNot));
            Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoHardwareEncoderOnThisPhone, whyNot);
            StringAssert.Contains("Android Allow Software Encoder", whyNot, "Naming the setting that changes it");
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(false, out _), "Still none, said once");
            LogAssert.NoUnexpectedReceived();

            using (IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestVideoEncoders.Create(true, out whyNot))
                Assert.AreSame(software, Chosen(encoder), whyNot);
        }

        [Test]
        public void APhoneWithNoEncoderSaysSo()
        {
            PhoneOffers();
            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape(ProtokitePlaytestVideoEncoders.NoEncoderOnThisPhone)));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(true, out string whyNot));
            Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoEncoderOnThisPhone, whyNot);
        }

        [Test]
        public void EncodersThatTakeNoneOfTheCapturesFramesAreNotOfferedAsASoftwareFix()
        {
            PhoneOffers(Hardware("c2.vendor.avc.encoder", takesNv12: false), Software("c2.android.avc.encoder", takesNv12: false));
            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape(ProtokitePlaytestVideoEncoders.NoEncoderTakesTheFrames)));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(true, out string whyNot));
            Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoEncoderTakesTheFrames, whyNot);
        }

        [Test]
        public void AMediaLibraryThatCannotBeReachedIsSaidInThePhonesWords()
        {
            const string why = "Android's media library, which encodes the video, could not be reached on this phone: mediandk";
            ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () => new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(null, why);
            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape(why)));
            Assert.IsNull(ProtokitePlaytestVideoEncoders.Create(false, out string whyNot));
            Assert.AreEqual(why, whyNot);
        }

        [Test]
        public void APhoneThatDoesNotAnswerInTimeIsSaidToBeAPhone()
        {
            using (ManualResetEventSlim answer = new ManualResetEventSlim())
            {
                ProtokitePlaytestVideoEncoders.LongestWaitForAnAnswerForTesting = TimeSpan.FromMilliseconds(100);
                ProtokitePlaytestVideoEncoders.LookForEncodersForTesting = () =>
                {
                    answer.Wait(TimeSpan.FromSeconds(10));
                    return new KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>(new List<ProtokitePlaytestEncoderFound>(), null);
                };
                try
                {
                    Assert.AreEqual(ProtokitePlaytestVideoEncoders.NoAnswerInTimeOnThisPhone, ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(false));
                }
                finally
                {
                    answer.Set();
                }
            }
        }

        [Test]
        public void EveryAndroidProcessorShouldHaveVideo()
        {
            ProtokitePlaytestVideoEncoders.ReadProcessArchitectureForTesting = () => System.Runtime.InteropServices.Architecture.Arm;
            try
            {
                Assert.IsTrue(ProtokitePlaytestVideoEncoders.ShouldHaveVideo, "A 32-bit ARM phone records: the encoder is the system's");
            }
            finally
            {
                ProtokitePlaytestVideoEncoders.ReadProcessArchitectureForTesting = null;
            }
        }

        // What the phone lent is given back

        private sealed class LentObject : IDisposable
        {
            public int TimesGivenBack;
            public void Dispose() => TimesGivenBack++;
        }

        [Test]
        public void ANewLaunchGivesBackWhatThePhoneLent()
        {
            LentObject lent = new LentObject();
            ProtokitePlaytestEncoderFound encoder = Hardware("c2.vendor.avc.encoder");
            encoder.HeldFromThePlatform = lent;
            encoder.TakesSizeAndRate = (width, height, rate) => true;
            PhoneOffers(encoder);
            Assert.IsNotNull(ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone), whyNone);
            Assert.AreEqual(0, lent.TimesGivenBack, "Precondition: kept while the launch may ask it");

            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            Assert.AreEqual(1, lent.TimesGivenBack);
            Assert.IsNull(encoder.TakesSizeAndRate, "Nothing can ask it once given back");
            ProtokitePlaytestVideoEncoders.ResetForNewLaunch();
            Assert.AreEqual(1, lent.TimesGivenBack, "Given back once");
        }

        // The size a recording takes

        private static ProtokitePlaytestAndroidVideoEncoder EncoderTaking(Func<int, int, int, bool> takes)
        {
            ProtokitePlaytestEncoderFound found = Hardware("c2.vendor.avc.encoder");
            found.TakesSizeAndRate = takes;
            return new ProtokitePlaytestAndroidVideoEncoder(found, (out string whyNot) =>
            {
                whyNot = "never made in these tests";
                return null;
            });
        }

        private static ProtokitePlaytestVideoSettings PhoneSettings(int framesPerSecond = 15)
        {
            ProtokitePlaytestVideoSettings settings = ProtokitePlaytestVideoSettings.From(null, true);
            settings.FramesPerSecond = framesPerSecond;
            return settings;
        }

        [Test]
        public void ASizeTheEncoderTakesIsLeftAsItIs()
        {
            List<string> asked = new List<string>();
            ProtokitePlaytestVideoSettings settings = PhoneSettings();
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking((w, h, r) => { asked.Add($"{w}x{h}@{r}"); return true; }), settings, 3088, 1440,
                out string note, out string whyNot), whyNot);
            Assert.IsNull(note);
            CollectionAssert.AreEqual(new[] { "1280x592@15" }, asked, "The size this screen fits to is the one asked");
            Assert.AreEqual(1280, settings.MaxVideoWidth);
            Assert.AreEqual(1280, settings.MaxVideoHeight);
            Assert.AreEqual(15, settings.FramesPerSecond);
        }

        [Test]
        public void AnUprightPhoneIsAskedAboutAnUprightVideo()
        {
            List<string> asked = new List<string>();
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking((w, h, r) => { asked.Add($"{w}x{h}"); return true; }), PhoneSettings(), 1440, 3088,
                out _, out string whyNot), whyNot);
            CollectionAssert.AreEqual(new[] { "592x1280" }, asked);
        }

        [Test]
        public void ASizeTheEncoderRefusesStepsDownToTheNextItTakesAndSaysWhich()
        {
            ProtokitePlaytestVideoSettings settings = PhoneSettings();
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking((w, h, r) => w <= 960), settings, 3088, 1440, out string note, out string whyNot), whyNot);
            Assert.AreEqual("c2.vendor.avc.encoder (the phone's hardware encoder) does not take 1280x592 at 15 frames a second, so this recording is 960x448 at 15.", note);
            Assert.AreEqual(960, settings.MaxVideoWidth);
            Assert.AreEqual(960, settings.MaxVideoHeight);
            Assert.IsTrue(ProtokitePlaytestVideoSettings.FitVideoSize(3088, 1440, settings.MaxVideoWidth, settings.MaxVideoHeight, out int width, out int height));
            Assert.AreEqual("960x448", $"{width}x{height}", "The capture made from the settings is the size the encoder took");
        }

        [Test]
        public void ARateTheEncoderRefusesAtEverySizeStepsDownToFifteenAtTheFullSize()
        {
            ProtokitePlaytestVideoSettings settings = PhoneSettings(30);
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking((w, h, r) => r <= 15), settings, 3088, 1440, out string note, out string whyNot), whyNot);
            StringAssert.EndsWith("does not take 1280x592 at 30 frames a second, so this recording is 1280x592 at 15.", note, "Every size at the asked rate first, then the rate");
            Assert.AreEqual(15, settings.FramesPerSecond);
            Assert.AreEqual(1280, settings.MaxVideoWidth);
        }

        [Test]
        public void AtTheAskedRateASmallerSizeIsTriedBeforeALowerRate()
        {
            ProtokitePlaytestVideoSettings settings = PhoneSettings(30);
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking((w, h, r) => r <= 15 || w <= 960), settings, 3088, 1440, out string note, out string whyNot), whyNot);
            StringAssert.EndsWith("so this recording is 960x448 at 30.", note, "Smoothness kept where a smaller picture does");
            Assert.AreEqual(30, settings.FramesPerSecond);
            Assert.AreEqual(960, settings.MaxVideoWidth);
        }

        [Test]
        public void AnEncoderThatTakesNoSizeRecordsNoVideoAndLeavesTheSettings()
        {
            ProtokitePlaytestVideoSettings settings = PhoneSettings();
            Assert.IsFalse(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking((w, h, r) => false), settings, 3088, 1440, out string note, out string whyNot));
            Assert.IsNull(note);
            Assert.AreEqual("c2.vendor.avc.encoder (the phone's hardware encoder) takes none of the sizes from 1280x592 down to 480x224, at 15 frames a second or fewer.", whyNot);
            Assert.AreEqual(1280, settings.MaxVideoWidth, "Left as they were");
            Assert.AreEqual(15, settings.FramesPerSecond);
        }

        [Test]
        public void AnEncoderThatDoesNotSayWhatItTakesIsLeftToTry()
        {
            ProtokitePlaytestVideoSettings settings = PhoneSettings();
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(EncoderTaking(null), settings, 3088, 1440, out string note, out string whyNot), whyNot);
            Assert.IsNull(note);
            Assert.IsTrue(ProtokitePlaytestVideoEncoders.FitTheEncoder(new FakeH264Encoder(), settings, 3088, 1440, out note, out whyNot),
                "Nor is any other platform's encoder held to a phone's sizes");
            Assert.AreEqual(1280, settings.MaxVideoWidth);
        }
    }
}
