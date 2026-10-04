using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Flock;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// A phone's recording and the phone: its heat and battery asked every few seconds of play, its free space when a recording starts,
    /// and what each recording holds written out when the game goes to the background. Tests stand in for the phone.
    /// </summary>
    public class ProtokitePlaytestPhoneLimitsTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const double SixtyFps = 1.0 / 60.0;
        private const long Megabyte = 1024L * 1024;
        private const long PhoneStorage = 256L * 1024 * Megabyte;
        private const long AndroidsLowStorageLine = 500 * Megabyte;

        private ProtokitePlaytestSettingsForTests _settings;
        private string _folder;
        private FakeFrameSource _source;
        private FakeH264Encoder _encoder;
        private readonly List<string> _logged = new List<string>();
        private int _thermalQuestions;
        private int _batteryQuestions;
        private int _diskQuestions;

        private string Recordings => Path.Combine(_folder, "Recordings");

        [SetUp]
        public void SetUp()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsFalse(FlockClient.IsInitialized, "Precondition: no Flock client left running by another test");
            _settings = new ProtokitePlaytestSettingsForTests(playtestingEnabled: false);
            _folder = Path.Combine(Path.GetTempPath(), "protokite_phone_limits_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
            ProtokitePlaytest.RecordingsFolderForTesting = Recordings;
            ProtokitePlaytest.VideoEncoderForTesting = () => _encoder = new FakeH264Encoder();
            ProtokitePlaytest.VideoFrameSourceForTesting = (settings, format) => _source = new FakeFrameSource();
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = true;
            _settings.Settings.AndroidVideoFramesPerSecond = 15;
            _settings.Settings.AndroidVideoBitrateKbps = 1500;
            _settings.Settings.MaxRecordingSizeMb = 1536;
            _settings.Settings.MaxRecordingMinutes = 60f;
            _settings.Settings.RecordingsDiskBudgetMb = 4096;
            _settings.Settings.AndroidRecordingsDiskBudgetMb = 1024;
            _settings.Settings.SlowDownTheRecordingWhenThePhoneIsHot = true;
            _settings.Settings.StopTheRecordingBelowBatteryPercent = 15;
            // A phone at rest, half charged and not charging, with plenty of room, until a test says otherwise.
            _thermalQuestions = 0;
            _batteryQuestions = 0;
            _diskQuestions = 0;
            SetThermalStatus(0);
            SetBattery(0.5f, BatteryStatus.Discharging);
            SetFreeSpace(100L * 1024 * Megabyte);
            _logged.Clear();
            Application.logMessageReceived += Heard;
        }

        [TearDown]
        public void TearDown()
        {
            Application.logMessageReceived -= Heard;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ProtokitePlaytest.ResetForNewLaunch();
            Assert.IsTrue(ProtokitePlaytest.WaitForEarlierRecordingsForTesting(TimeSpan.FromSeconds(10)), "The finishing pass ended before its folder is deleted");
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = false;
            ProtokitePlaytestPhoneConditions.ThermalStatusForTesting = null;
            ProtokitePlaytestPhoneConditions.BatteryForTesting = null;
            ProtokitePlaytestPhoneConditions.DiskSpaceForTesting = null;
            ProtokitePlaytest.VideoEncoderForTesting = null;
            ProtokitePlaytest.VideoFrameSourceForTesting = null;
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            _settings.Dispose();
            try
            {
                if (Directory.Exists(_folder))
                    Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private void Heard(string message, string stack, LogType type)
        {
            lock (_logged)
                _logged.Add(message);
        }

        private int LinesSaying(string words)
        {
            lock (_logged)
                return _logged.FindAll(line => line.Contains(words)).Count;
        }

        private void SetThermalStatus(int? status) => ProtokitePlaytestPhoneConditions.ThermalStatusForTesting = () =>
        {
            _thermalQuestions++;
            return status;
        };

        private void SetBattery(float level, BatteryStatus status) => ProtokitePlaytestPhoneConditions.BatteryForTesting = () =>
        {
            _batteryQuestions++;
            return (level, status);
        };

        private void SetFreeSpace(long freeBytes) => ProtokitePlaytestPhoneConditions.DiskSpaceForTesting = folder =>
        {
            _diskQuestions++;
            return (freeBytes, PhoneStorage);
        };

        // Frames at 60 fps, each encoded before the next, as in a game.
        private void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                ProtokitePlaytest.UpdateVideo(SixtyFps);
                _source?.WaitForTheEncoderToCatchUp();
            }
        }

        // Frames for as long as it takes the phone to be asked again.
        private void FramesUntilThePhoneIsAskedAgain() => Frames((int)(ProtokitePlaytestPhoneConditions.TimeBetweenQuestions.TotalSeconds * 60) + 1);

        // How many frames the test video captures in two seconds of play.
        private int FramesCapturedInTwoSeconds()
        {
            int before = _source.Asked.Count;
            Frames(120);
            return _source.Asked.Count - before;
        }

        private static void RecordTestVideo(double seconds)
        {
            Assert.IsTrue(ProtokitePlaytest.RecordTestVideo(seconds, out string whyNot), "Precondition: the test video is asked for: " + whyNot);
        }

        private void FramesUntilTheTestVideoIsFinished()
        {
            DateTime until = DateTime.UtcNow.AddSeconds(20);
            while (ProtokitePlaytest.TestVideoState != ProtokitePlaytestTestVideoState.Finished && DateTime.UtcNow < until)
            {
                ProtokitePlaytest.UpdateVideo(SixtyFps);
                Thread.Sleep(1);
            }
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Finished, ProtokitePlaytest.TestVideoState, "Precondition: the test video's file was written");
        }

        private FlockTestClient PlaytestRecording()
        {
            _settings.Settings.PlaytestingEnabled = true;
            FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(
                "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"t\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":true},\"form\":null}}")));
            ProtokitePlaytest.Refresh();
            Frames(1);
            Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Precondition: the playtest records the screen");
            return flock;
        }

        // What the phone says, and what a recording does about it

        [TestCase(null, 0, TestName = "A phone that does not say records as set")]
        [TestCase(0, 0, TestName = "No heat")]
        [TestCase(1, 0, TestName = "Light heat, which players do not notice")]
        [TestCase(2, 1, TestName = "Moderate heat halves the frame rate")]
        [TestCase(3, 2, TestName = "Severe heat stops the recording")]
        [TestCase(6, 2, TestName = "Shutdown heat stops the recording")]
        [TestCase(9, 2, TestName = "A status Android adds later above shutdown stops it too")]
        // The step is its number (0 full rate, 1 half, 2 stop): a public test cannot take an internal enum.
        public void HeatStepsFollowAndroidsThermalStatus(int? status, int step)
        {
            Assert.AreEqual((ProtokitePlaytestHeatStep)step, ProtokitePlaytestPhoneConditions.HeatStep(status, true));
            Assert.AreEqual(ProtokitePlaytestHeatStep.FullFrameRate, ProtokitePlaytestPhoneConditions.HeatStep(status, false), "Switched off, heat changes nothing");
        }

        [TestCase(0.14f, BatteryStatus.Discharging, 15, true, TestName = "Below the level and not charging stops")]
        [TestCase(0.15f, BatteryStatus.Discharging, 15, false, TestName = "At the level goes on")]
        [TestCase(0.1499f, BatteryStatus.Discharging, 15, false, TestName = "A level the phone shows as the line goes on")]
        [TestCase(0.10f, BatteryStatus.NotCharging, 15, true, TestName = "Plugged in but not charging stops")]
        [TestCase(0.10f, BatteryStatus.Charging, 15, false, TestName = "Charging goes on")]
        [TestCase(0.10f, BatteryStatus.Full, 15, false, TestName = "Full on its charger goes on")]
        [TestCase(-1f, BatteryStatus.Unknown, 15, false, TestName = "A level not known goes on")]
        [TestCase(0.01f, BatteryStatus.Discharging, 0, false, TestName = "Zero never stops")]
        [TestCase(0.59f, BatteryStatus.Discharging, 60, true, TestName = "The studio's own level")]
        public void TheBatteryStopsARecordingOnlyBelowItsLevelWhileNotCharging(float level, BatteryStatus status, int stopBelowPercent, bool stops)
        {
            Assert.AreEqual(stops, ProtokitePlaytestPhoneConditions.BatteryTooLow(level, status, stopBelowPercent));
        }

        [Test]
        public void RecordingsLeaveThePhoneAtAndroidsOwnLowStorageLine()
        {
            Assert.AreEqual(500 * Megabyte, ProtokitePlaytestPhoneConditions.LowStorageLine(PhoneStorage), "500 MB on a large phone");
            Assert.AreEqual(1024 * Megabyte / 20, ProtokitePlaytestPhoneConditions.LowStorageLine(1024 * Megabyte), "5% of a small one");
            Assert.AreEqual(500 * Megabyte, ProtokitePlaytestPhoneConditions.LowStorageLine(10240 * Megabyte + 20 * Megabyte), "500 MB just past where 5% passes it");
            Assert.AreEqual(9216 * Megabyte / 20, ProtokitePlaytestPhoneConditions.LowStorageLine(9216 * Megabyte), "5% just before");
            Assert.AreEqual(0, ProtokitePlaytestPhoneConditions.LowStorageLine(0));
        }

        // Heat

        [Test]
        public void AWarmPhoneRecordsAtHalfTheFrameRateUntilItCools()
        {
            RecordTestVideo(600);
            Frames(1);
            Assert.AreEqual(30, FramesCapturedInTwoSeconds(), 1, "Precondition: 15 frames a second on a phone at rest");

            SetThermalStatus(2);
            FramesUntilThePhoneIsAskedAgain();
            Assert.AreEqual(15, FramesCapturedInTwoSeconds(), 1, "Half the frame rate while the phone is warm");
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "Still recording");
            Assert.AreEqual(1, LinesSaying("so the test video takes 7.5 frames a second, half its rate, until the phone cools"), "Said once, not at every question");

            SetThermalStatus(1);
            FramesUntilThePhoneIsAskedAgain();
            Assert.AreEqual(30, FramesCapturedInTwoSeconds(), 1, "Back to its rate once the phone cools");
            Assert.AreEqual(1, LinesSaying("The phone has cooled (thermal status 1), so the test video takes 15 frames a second again"));
        }

        [Test]
        public void AHeatReadingThatStopsComingIsNotCalledCooling()
        {
            RecordTestVideo(600);
            Frames(1);
            SetThermalStatus(2);
            FramesUntilThePhoneIsAskedAgain();
            Assert.AreEqual(1, LinesSaying("half its rate"), "Precondition: slowed for a warm phone");
            SetThermalStatus(null);
            FramesUntilThePhoneIsAskedAgain();
            Assert.AreEqual(30, FramesCapturedInTwoSeconds(), 1, "A phone that stops saying records as set");
            Assert.AreEqual(1, LinesSaying("The phone no longer reports its heat, so the test video takes 15 frames a second again"));
            Assert.AreEqual(0, LinesSaying("The phone has cooled"), "It never said it cooled");
        }

        [Test]
        public void ASeverePhoneStopsTheRecordingForTheLaunchAndKeepsWhatItHolds()
        {
            RecordTestVideo(600);
            Frames(120);
            SetThermalStatus(3);
            FramesUntilThePhoneIsAskedAgain();
            FramesUntilTheTestVideoIsFinished();
            ProtokitePlaytestVideoRecordingSummary summary = ProtokitePlaytest.FinishedTestVideo;
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.PhoneTooHot, summary.StopReason);
            Assert.IsNotNull(summary.FilePath, "What it recorded is kept");
            Assert.Greater(summary.FramesWritten, 30);
            Assert.AreEqual(1, LinesSaying("enough for the player to notice (thermal status 3), so the test video stops for this launch; what it holds is kept."));
        }

        [Test]
        public void APhoneAtRestLeavesTheRecordingAlone()
        {
            RecordTestVideo(600);
            Frames(1);
            for (int i = 0; i < 4; i++)
                FramesUntilThePhoneIsAskedAgain();
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
            Assert.AreEqual(30, FramesCapturedInTwoSeconds(), 1, "At its own rate");
            Assert.GreaterOrEqual(_thermalQuestions, 4, "Precondition: the phone was asked");
        }

        [Test]
        public void ThePhoneIsAskedEveryFewSecondsOfPlayNotEveryFrame()
        {
            RecordTestVideo(600);
            Frames(2);
            Assert.AreEqual(1, _thermalQuestions, "Asked at the recording's first frame, the one after it starts");
            Assert.AreEqual(1, _batteryQuestions);
            Frames(60 * 12);
            Assert.AreEqual(3, _thermalQuestions, "Then once every five seconds: twice in twelve");
            Assert.AreEqual(3, _batteryQuestions);
        }

        [Test]
        public void ARecordingThatStartsOnAWarmPhoneIsSlowedAtItsFirstFrame()
        {
            RecordTestVideo(1);
            FramesUntilTheTestVideoIsFinished();
            Assert.AreEqual(1, _thermalQuestions, "Precondition: the phone was asked a second ago, when it was at rest");

            SetThermalStatus(2);
            RecordTestVideo(600);
            Frames(1);
            Assert.AreEqual(15, FramesCapturedInTwoSeconds(), 1, "Held to how the phone is now, not how it was when last asked");
        }

        [Test]
        public void WithTheHeatSwitchOffThePhoneIsNotAskedAboutHeat()
        {
            _settings.Settings.SlowDownTheRecordingWhenThePhoneIsHot = false;
            SetThermalStatus(3);
            RecordTestVideo(600);
            Frames(60 * 6);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "A hot phone records as set");
            Assert.AreEqual(30, FramesCapturedInTwoSeconds(), 1);
            Assert.AreEqual(0, _thermalQuestions, "Never asked");
            Assert.Greater(_batteryQuestions, 0, "Control: the battery still is");
        }

        [Test]
        public void APhoneThatDoesNotReportItsHeatIsSaidOnceALaunch()
        {
            SetThermalStatus(null);
            RecordTestVideo(600);
            Frames(60 * 11);
            Assert.AreEqual(1, LinesSaying("Video is not slowed down for heat on this phone: the phone does not report its heat."));
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);

            ProtokitePlaytest.ResetForNewLaunch();
            RecordTestVideo(600);
            Frames(2);
            Assert.AreEqual(2, LinesSaying("Video is not slowed down for heat on this phone"), "A new launch says it again");
        }

        [Test]
        public void AWindowsRecordingNeverAsksAboutAPhone()
        {
            ProtokitePlaytestVideoEncoders.ActAsAndroidForTesting = false;
            SetThermalStatus(3);
            SetBattery(0.01f, BatteryStatus.Discharging);
            RecordTestVideo(600);
            Frames(60 * 6);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
            Assert.AreEqual(0, _thermalQuestions);
            Assert.AreEqual(0, _batteryQuestions);
            Assert.AreEqual(0, _diskQuestions, "Nor about its free space");
        }

        // The battery

        [Test]
        public void ALowBatteryStopsTheRecordingUnlessThePhoneIsCharging()
        {
            RecordTestVideo(600);
            Frames(120);
            SetBattery(0.10f, BatteryStatus.Charging);
            FramesUntilThePhoneIsAskedAgain();
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "On its charger, a low battery records on");

            SetBattery(0.14f, BatteryStatus.Discharging);
            FramesUntilThePhoneIsAskedAgain();
            FramesUntilTheTestVideoIsFinished();
            Assert.AreEqual(ProtokitePlaytestVideoStopReason.BatteryLow, ProtokitePlaytest.FinishedTestVideo.StopReason);
            Assert.IsNotNull(ProtokitePlaytest.FinishedTestVideo.FilePath, "What it recorded is kept");
            Assert.AreEqual(1, LinesSaying("The phone's battery is at 14% and not charging, below Stop The Recording Below Battery Percent (15), so the test video stops for this launch"));
        }

        [Test]
        public void WithTheBatteryLevelAtZeroThePhoneIsNotAskedAboutItsBattery()
        {
            _settings.Settings.StopTheRecordingBelowBatteryPercent = 0;
            SetBattery(0.01f, BatteryStatus.Discharging);
            RecordTestVideo(600);
            Frames(60 * 6);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState);
            Assert.AreEqual(0, _batteryQuestions);
        }

        // The playtest's own recording

        [Test]
        public void ThePlaytestsRecordingIsHeldToThePhoneToo()
        {
            SetThermalStatus(2);
            using (PlaytestRecording())
            {
                Frames(1);
                Assert.IsTrue(ProtokitePlaytest.VideoRecordingForTesting.RecordsAtHalfTheFrameRate, "Slowed for a warm phone from its first frame");
                SetThermalStatus(3);
                FramesUntilThePhoneIsAskedAgain();
                Assert.IsFalse(ProtokitePlaytest.IsRecordingVideo, "Stopped for a hot one");
                Assert.AreEqual(1, LinesSaying("so the playtest's recording stops for this launch; what it holds is kept and uploaded as usual."));
                DateTime until = DateTime.UtcNow.AddSeconds(20);
                while (ProtokitePlaytest.FinishedVideo == null && DateTime.UtcNow < until)
                {
                    ProtokitePlaytest.UpdateVideo(SixtyFps);
                    Thread.Sleep(1);
                }
                Assert.IsNotNull(ProtokitePlaytest.FinishedVideo, "Precondition: its file was written");
                Assert.AreEqual(ProtokitePlaytestVideoStopReason.PhoneTooHot, ProtokitePlaytest.FinishedVideo.StopReason);
                Assert.IsNotNull(ProtokitePlaytest.FinishedVideo.FilePath, "What it recorded is kept");
            }
        }

        // Going to the background

        [Test]
        public void GoingToTheBackgroundWritesOutTheTestVideo()
        {
            RecordTestVideo(600);
            Frames(60);
            ProtokitePlaytest.HandleGameWentToTheBackground();
            Assert.IsTrue(SpinWait.SpinUntil(() => _encoder.TimesLetGo == 1, TimeSpan.FromSeconds(10)), "The test video's encoder let go");
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, "And it records on when the game comes back");
        }

        [Test]
        public void AVideoEndedByTheBackgroundSaysTheGameWentThere()
        {
            RecordTestVideo(600);
            Frames(60);
            _encoder.RefuseToLetGo = "the phone's encoder would not end its stream";
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                @"The test video could not be written to the end: .*would not end its stream.*The game went to the background 1 time\(s\)"));
            ProtokitePlaytest.HandleGameWentToTheBackground();
            FramesUntilTheTestVideoIsFinished();
        }

        [Test]
        public void APlaytestRecordingEndedByTheBackgroundSaysTheGameWentThere()
        {
            using (PlaytestRecording())
            {
                Frames(60);
                _encoder.RefuseToLetGo = "the phone's encoder would not end its stream";
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    @"The playtest video could not be written to the end: .*would not end its stream.*The game went to the background 1 time\(s\)"));
                ProtokitePlaytest.HandleGameWentToTheBackground();
                DateTime until = DateTime.UtcNow.AddSeconds(20);
                while (ProtokitePlaytest.FinishedVideo == null && DateTime.UtcNow < until)
                {
                    ProtokitePlaytest.UpdateVideo(SixtyFps);
                    Thread.Sleep(1);
                }
                Assert.IsNotNull(ProtokitePlaytest.FinishedVideo, "Precondition: the recording ended and was reported");
            }
        }

        [Test]
        public void GoingToTheBackgroundWritesOutThePlaytestsRecording()
        {
            using (PlaytestRecording())
            {
                Frames(60);
                ProtokitePlaytest.HandleGameWentToTheBackground();
                Assert.IsTrue(SpinWait.SpinUntil(() => _encoder.TimesLetGo == 1, TimeSpan.FromSeconds(10)), "The playtest's encoder let go");
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo);
            }
        }

        [Test]
        public void TheDriverHandsTheBackgroundToThePlaytest()
        {
            RecordTestVideo(600);
            Frames(60);
            GameObject driverObject = new GameObject("Driver under test");
            try
            {
                ProtokitePlaytestDriver driver = driverObject.AddComponent<ProtokitePlaytestDriver>();
                MethodInfo pause = typeof(ProtokitePlaytestDriver).GetMethod("OnApplicationPause", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(pause, "Unity calls the driver's OnApplicationPause");
                pause.Invoke(driver, new object[] { true });
                Assert.IsTrue(SpinWait.SpinUntil(() => _encoder.TimesLetGo == 1, TimeSpan.FromSeconds(10)), "Leaving for the background writes out what the recording holds");
                pause.Invoke(driver, new object[] { false });
                Assert.AreEqual(1, _encoder.TimesLetGo, "Coming back lets nothing go");
            }
            finally
            {
                Object.DestroyImmediate(driverObject);
            }
        }

        // Free space

        [Test]
        public void ThePhonesFreeSpaceCutsTheRecordingsRoom()
        {
            SetFreeSpace(AndroidsLowStorageLine + 100 * Megabyte);
            using (PlaytestRecording())
            {
                Assert.AreEqual(100 * Megabyte, ProtokitePlaytest.VideoRecordingForTesting.MaxBytes, "What is free above Android's own low-storage line, and no more");
                Assert.AreEqual(1, LinesSaying("(Max Recording Size Mb is 1536 MB, but the phone's free space leaves only this much)"));
            }
        }

        [Test]
        public void APhoneWithPlentyFreeIsHeldToTheAndroidBudget()
        {
            _settings.Settings.AndroidRecordingsDiskBudgetMb = 300;
            using (PlaytestRecording())
            {
                Assert.AreEqual(300 * Megabyte, ProtokitePlaytest.VideoRecordingForTesting.MaxBytes, "Android Recordings Disk Budget Mb, not the other platforms' 4096");
                Assert.AreEqual(1, LinesSaying("(Max Recording Size Mb is 1536 MB, but Android Recordings Disk Budget Mb leaves only this much)"));
            }
        }

        [Test]
        public void APhoneWithTooLittleFreeSpaceRecordsNoTestVideoAndSaysWhy()
        {
            SetFreeSpace(50 * Megabyte);
            Assert.IsTrue(ProtokitePlaytest.RecordTestVideo(5, out string whyNot), whyNot);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "No test video is recorded: the phone has 50 MB free, and recordings leave 500 MB of it, where Android warns that storage is running out, which leaves 0 MB for a test video"));
            Frames(1);
            StringAssert.Contains("Free some of the phone's storage.", ProtokitePlaytest.TestVideoProblem);
        }

        [Test]
        public void DeletingAnEndedTestVideoGivesBackTheSpaceItTook()
        {
            string planted = ProtokitePlaytestPlantedRuns.Plant(Recordings, ProtokitePlaytestRecordingKind.TestVideo, "20260101-000000-00000001", finishedVideo: new byte[3 * Megabyte]);
            // Two megabytes free above the line, and three held by a test video that may be deleted: five to record into.
            SetFreeSpace(AndroidsLowStorageLine + 2 * Megabyte);
            RecordTestVideo(20);
            Frames(1);
            Assert.AreEqual(ProtokitePlaytestTestVideoState.Recording, ProtokitePlaytest.TestVideoState, ProtokitePlaytest.TestVideoProblem);
            Assert.IsFalse(Directory.Exists(planted), "The older test video was deleted to make room");
            Assert.Greater(ProtokitePlaytest.TestVideoForTesting.MaxBytes, 4 * Megabyte, "The space it took is counted as free once it is deleted");
            Assert.AreEqual(1, LinesSaying("to make room in the room the phone's free space leaves (the phone has 502 MB free"));
        }

        [Test]
        public void FreeSpaceThatCannotBeReadLeavesTheBudgetAlone()
        {
            ProtokitePlaytestPhoneConditions.DiskSpaceForTesting = folder => null;
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                "The phone's free space could not be read, so this launch's recording is kept to Android Recordings Disk Budget Mb alone: the storage does not say how much is free"));
            using (PlaytestRecording())
            {
                Assert.AreEqual(1024 * Megabyte, ProtokitePlaytest.VideoRecordingForTesting.MaxBytes, "Held to the budget");
                Assert.AreEqual(0, LinesSaying("leaves only this much"), "The approved budget holds more than an hour records, so no cut is named");
            }
        }
    }
}
