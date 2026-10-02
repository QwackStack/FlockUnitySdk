using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestVideoSettingsTests
    {
        private static ProtokitePlaytestSettings NewSettings() => ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();

        [Test]
        public void ANewProjectRecordsAtTheDefaultsOnTheGraphicsCardOnly()
        {
            ProtokitePlaytestSettings asset = NewSettings();
            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(asset);
            Assert.AreEqual(1280, video.MaxVideoWidth);
            Assert.AreEqual(720, video.MaxVideoHeight);
            Assert.AreEqual(15, video.FramesPerSecond);
            Assert.AreEqual(1500, video.BitrateKbps);
            Assert.IsFalse(video.AllowSoftwareEncoder, "Windows' software encoder costs the game frame rate, so a studio turns it on");
            Assert.AreEqual(3600.0, video.MaxSeconds, 1e-9, "An hour");
            Assert.AreEqual(1536L * 1024 * 1024, video.MaxBytes, "1.5 GB");
            Assert.AreEqual(4096L * 1024 * 1024, video.DiskBudgetBytes, "4 GB for every recording kept on the machine");
            Assert.AreEqual(67, video.FrameDurationMs);
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void ARecordingMakesRoomForItsLengthLimitAtItsBitrateNotItsSizeLimit()
        {
            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(NewSettings());
            int fragment = ProtokitePlaytestMp4File.FrameHeaderBytes;
            // An hour at 1500 kbps with a quarter to spare, a fragment's header around each of 54,000 frames, and 4 KB for the headers.
            long expected = 843750000L + 54000L * fragment + 4096;
            Assert.AreEqual(expected, video.BytesToMakeRoomFor(fragment));
            Assert.Less(expected, video.MaxBytes, "Well under the 1.5 GB size limit");

            video.MaxSeconds = 5.0;
            Assert.AreEqual(1171875L + 75L * fragment + 4096, video.BytesToMakeRoomFor(fragment), "Five seconds wants about a megabyte");

            video.MaxSeconds = 3600.0;
            video.MaxBytes = 1024L * 1024;
            Assert.AreEqual(1024L * 1024, video.BytesToMakeRoomFor(fragment), "Never more than the size limit");
        }

        [Test]
        public void EverySettingIsReadFromItsOwnField()
        {
            // Every value differs from every other, so a setting read from its neighbour shows.
            ProtokitePlaytestSettings asset = NewSettings();
            asset.VideoWidth = 1024;
            asset.VideoHeight = 576;
            asset.VideoFramesPerSecond = 24;
            asset.VideoBitrateKbps = 900;
            asset.AllowSoftwareEncoder = true;
            asset.MaxRecordingMinutes = 7f;
            asset.MaxRecordingSizeMb = 11;
            asset.RecordingsDiskBudgetMb = 13;

            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(asset);
            Assert.AreEqual(1024, video.MaxVideoWidth);
            Assert.AreEqual(576, video.MaxVideoHeight);
            Assert.AreEqual(24, video.FramesPerSecond);
            Assert.AreEqual(900, video.BitrateKbps);
            Assert.IsTrue(video.AllowSoftwareEncoder);
            Assert.AreEqual(420.0, video.MaxSeconds, 1e-9);
            Assert.AreEqual(11L * 1024 * 1024, video.MaxBytes);
            Assert.AreEqual(13L * 1024 * 1024, video.DiskBudgetBytes);

            video.GraphicsCardVendorId = 0x10DE;
            ProtokitePlaytestVideoEncoderSettings encoder = video.EncoderSettings(640, 352);
            Assert.AreEqual(640, encoder.Width, "The fitted size, not the maximum");
            Assert.AreEqual(352, encoder.Height);
            Assert.AreEqual(24, encoder.FramesPerSecond);
            Assert.AreEqual(900, encoder.BitrateKbps);
            Assert.IsTrue(encoder.AllowSoftwareEncoder);
            Assert.AreEqual(0x10DE, encoder.GraphicsCardVendorId, "The game's graphics card maker, whose encoder is tried first");
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void ValuesOutOfRangeAreMovedIntoIt()
        {
            ProtokitePlaytestSettings asset = NewSettings();
            asset.VideoWidth = 99999;
            asset.VideoHeight = 0;
            asset.VideoFramesPerSecond = 0;
            asset.VideoBitrateKbps = 5;
            asset.MaxRecordingMinutes = float.NaN;
            asset.MaxRecordingSizeMb = -4;
            asset.RecordingsDiskBudgetMb = 0;

            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(asset);
            Assert.AreEqual(3840, video.MaxVideoWidth);
            Assert.AreEqual(16, video.MaxVideoHeight);
            Assert.AreEqual(1, video.FramesPerSecond);
            Assert.AreEqual(100, video.BitrateKbps);
            Assert.AreEqual(6.0, video.MaxSeconds, 1e-9, "The shortest a recording may be");
            Assert.AreEqual(1024L * 1024, video.MaxBytes, "At least a megabyte");
            Assert.AreEqual(1024L * 1024, video.DiskBudgetBytes, "At least a megabyte");
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void NoSettingsAssetReadsAsTheDefaults()
        {
            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(null);
            Assert.AreEqual(1280, video.MaxVideoWidth);
            Assert.AreEqual(15, video.FramesPerSecond);
            Assert.IsFalse(video.AllowSoftwareEncoder);
        }

        [Test]
        public void ASettingsAssetSavedWithTheRemovedEncoderSettingsStillLoads()
        {
            // An asset an earlier version saved, with the settings of the encoder this version no longer ships.
            string earlier = "{\"MonoBehaviour\":{\"playtestingEnabled\":true,\"videoCodec\":9,\"videoWidth\":1024,\"encoderThreads\":4,\"useCodecDefaultSpeed\":false," +
                             "\"encoderSpeed\":5,\"encoderBelowGamePriority\":false,\"videoBitrateKbps\":900}}";
            ProtokitePlaytestSettings loaded = NewSettings();
            EditorJsonUtility.FromJsonOverwrite(earlier, loaded);
            Assert.IsTrue(loaded.PlaytestingEnabled, "The settings that remain are read");
            Assert.AreEqual(1024, loaded.VideoWidth);
            Assert.AreEqual(900, loaded.VideoBitrateKbps);
            Assert.IsFalse(loaded.AllowSoftwareEncoder, "The new setting starts off");
            Object.DestroyImmediate(loaded);
        }

        [Test]
        public void TheSoftwareEncoderSwitchIsSavedWithTheAsset()
        {
            ProtokitePlaytestSettings asset = NewSettings();
            asset.AllowSoftwareEncoder = true;
            asset.MaxRecordingMinutes = 12.5f;
            string saved = EditorJsonUtility.ToJson(asset);
            ProtokitePlaytestSettings loaded = NewSettings();
            EditorJsonUtility.FromJsonOverwrite(saved, loaded);
            Assert.IsTrue(loaded.AllowSoftwareEncoder);
            Assert.AreEqual(12.5f, loaded.MaxRecordingMinutes);
            Assert.IsTrue(ProtokitePlaytestVideoSettings.From(loaded).AllowSoftwareEncoder);
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
