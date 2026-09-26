using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestVideoSettingsTests
    {
        private static ProtokitePlaytestSettings NewSettings() => ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();

        [Test]
        public void ANewProjectRecordsWhatASlowPcCanAfford()
        {
            ProtokitePlaytestSettings asset = NewSettings();
            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(asset);
            Assert.AreEqual(ProtokitePlaytestVideoCodec.Vp8, video.Codec);
            Assert.AreEqual(1280, video.MaxVideoWidth);
            Assert.AreEqual(720, video.MaxVideoHeight);
            Assert.AreEqual(15, video.FramesPerSecond);
            Assert.AreEqual(1500, video.BitrateKbps);
            Assert.AreEqual(1, video.Threads);
            Assert.IsNull(video.Speed, "The codec's own speed");
            Assert.IsTrue(video.EncoderBelowGamePriority);
            Assert.AreEqual(3600.0, video.MaxSeconds, 1e-9, "An hour");
            Assert.AreEqual(1536L * 1024 * 1024, video.MaxBytes, "1.5 GB");
            Assert.AreEqual(67, video.FrameDurationMs);
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void EverySettingIsReadFromItsOwnField()
        {
            // Every value differs from every other, so a setting read from its neighbour shows.
            ProtokitePlaytestSettings asset = NewSettings();
            asset.VideoCodec = ProtokitePlaytestVideoCodec.Vp9;
            asset.VideoWidth = 1024;
            asset.VideoHeight = 576;
            asset.VideoFramesPerSecond = 24;
            asset.VideoBitrateKbps = 900;
            asset.EncoderThreads = 3;
            asset.UseCodecDefaultSpeed = false;
            asset.EncoderSpeed = 5;
            asset.EncoderBelowGamePriority = false;
            asset.MaxRecordingMinutes = 7f;
            asset.MaxRecordingSizeMb = 11;

            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(asset);
            Assert.AreEqual(ProtokitePlaytestVideoCodec.Vp9, video.Codec);
            Assert.AreEqual(1024, video.MaxVideoWidth);
            Assert.AreEqual(576, video.MaxVideoHeight);
            Assert.AreEqual(24, video.FramesPerSecond);
            Assert.AreEqual(900, video.BitrateKbps);
            Assert.AreEqual(3, video.Threads);
            Assert.AreEqual(5, video.Speed);
            Assert.IsFalse(video.EncoderBelowGamePriority);
            Assert.AreEqual(420.0, video.MaxSeconds, 1e-9);
            Assert.AreEqual(11L * 1024 * 1024, video.MaxBytes);

            ProtokitePlaytestVideoEncoderSettings encoder = video.EncoderSettings(640, 352);
            Assert.AreEqual(ProtokitePlaytestVideoCodec.Vp9, encoder.Codec);
            Assert.AreEqual(640, encoder.Width, "The fitted size, not the maximum");
            Assert.AreEqual(352, encoder.Height);
            Assert.AreEqual(24, encoder.FramesPerSecond);
            Assert.AreEqual(900, encoder.BitrateKbps);
            Assert.AreEqual(3, encoder.Threads);
            Assert.AreEqual(5, encoder.SpeedToUse);
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void ValuesOutOfRangeAreMovedIntoIt()
        {
            ProtokitePlaytestSettings asset = NewSettings();
            asset.VideoCodec = (ProtokitePlaytestVideoCodec)3;
            asset.VideoWidth = 99999;
            asset.VideoHeight = 0;
            asset.VideoFramesPerSecond = 0;
            asset.VideoBitrateKbps = 5;
            asset.EncoderThreads = 99;
            asset.UseCodecDefaultSpeed = false;
            asset.EncoderSpeed = -99;
            asset.MaxRecordingMinutes = float.NaN;
            asset.MaxRecordingSizeMb = -4;

            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(asset);
            Assert.AreEqual(ProtokitePlaytestVideoCodec.Vp8, video.Codec, "A codec this build does not know is VP8");
            Assert.AreEqual(3840, video.MaxVideoWidth);
            Assert.AreEqual(16, video.MaxVideoHeight);
            Assert.AreEqual(1, video.FramesPerSecond);
            Assert.AreEqual(100, video.BitrateKbps);
            Assert.AreEqual(16, video.Threads);
            Assert.AreEqual(-16, video.Speed);
            Assert.AreEqual(6.0, video.MaxSeconds, 1e-9, "The shortest a recording may be");
            Assert.AreEqual(1024L * 1024, video.MaxBytes, "At least a megabyte");
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void NoSettingsAssetReadsAsTheDefaults()
        {
            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(null);
            Assert.AreEqual(1280, video.MaxVideoWidth);
            Assert.AreEqual(15, video.FramesPerSecond);
        }

        [Test]
        public void TheSpeedSwitchIsSavedWithTheAsset()
        {
            // A nullable field would never be saved; the switch and the number are two fields Unity keeps.
            ProtokitePlaytestSettings asset = NewSettings();
            asset.UseCodecDefaultSpeed = false;
            asset.EncoderSpeed = -7;
            asset.MaxRecordingMinutes = 12.5f;
            string saved = EditorJsonUtility.ToJson(asset);
            ProtokitePlaytestSettings loaded = NewSettings();
            EditorJsonUtility.FromJsonOverwrite(saved, loaded);
            Assert.IsFalse(loaded.UseCodecDefaultSpeed);
            Assert.AreEqual(-7, loaded.EncoderSpeed);
            Assert.AreEqual(12.5f, loaded.MaxRecordingMinutes);
            Assert.AreEqual(-7, ProtokitePlaytestVideoSettings.From(loaded).Speed);
            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(loaded);
        }

        [TestCase(2560, 1440, 1280, 720, TestName = "A 1440p screen halves")]
        [TestCase(1920, 1080, 1280, 720, TestName = "A 1080p screen")]
        [TestCase(3440, 1440, 1280, 528, TestName = "An ultrawide screen keeps its shape, each side a multiple of 16")]
        [TestCase(1024, 768, 960, 720, TestName = "A 4 by 3 screen is held by its height")]
        [TestCase(640, 360, 640, 352, TestName = "A smaller window is not enlarged")]
        [TestCase(854, 480, 848, 480, TestName = "854 wide, which sheared every frame, is recorded 848 wide")]
        [TestCase(10, 10, 16, 16, TestName = "A tiny window is recorded at the smallest size")]
        public void AScreenIsFittedInsideTheMaximum(int screenWidth, int screenHeight, int width, int height)
        {
            Assert.IsTrue(ProtokitePlaytestVideoSettings.FitVideoSize(screenWidth, screenHeight, 1280, 720, out int fittedWidth, out int fittedHeight));
            Assert.AreEqual(width, fittedWidth, "Width");
            Assert.AreEqual(height, fittedHeight, "Height");
            Assert.AreEqual(0, fittedWidth % 16);
            Assert.AreEqual(0, fittedHeight % 16);
        }

        [Test]
        public void AMaximumThatIsNotAMultipleOf16IsRoundedDown()
        {
            Assert.IsTrue(ProtokitePlaytestVideoSettings.FitVideoSize(1920, 1080, 854, 480, out int width, out int height));
            Assert.AreEqual(848, width, "A 1080p screen fits 854 by 480 at 853 by 480, and 853 is rounded down");
            Assert.AreEqual(480, height);
        }

        [Test]
        public void AScreenWithNoSizeHasNoVideoSize()
        {
            Assert.IsFalse(ProtokitePlaytestVideoSettings.FitVideoSize(0, 1080, 1280, 720, out _, out _));
            Assert.IsFalse(ProtokitePlaytestVideoSettings.FitVideoSize(1920, -1, 1280, 720, out _, out _));
        }
    }
}
