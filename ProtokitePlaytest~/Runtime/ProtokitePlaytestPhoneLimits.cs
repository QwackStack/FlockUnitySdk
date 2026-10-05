using UnityEngine;

namespace Protokite.Playtest
{
    public static partial class ProtokitePlaytest
    {
        // Real time since the phone was last asked about its heat and battery; endless as each recording starts, so it asks at once.
        private static double _secondsSinceThePhoneWasAsked = double.PositiveInfinity;
        private static int? _thermalStatus;
        private static float _batteryLevel = -1f;
        private static BatteryStatus _batteryStatus = BatteryStatus.Unknown;
        private static bool _saidThePhoneDoesNotReportItsHeat;

        /// <summary>Has the phone asked again at the next frame, so a recording that has just started is held to how the phone is now.</summary>
        private static void AskThePhoneAtTheNextFrame() => _secondsSinceThePhoneWasAsked = double.PositiveInfinity;

        /// <summary>Once a frame: while a phone's screen records, asks the phone every few seconds and slows, restores or stops each recording to match.</summary>
        private static void KeepRecordingsWithinThePhonesLimits(double frameSeconds)
        {
            ProtokitePlaytestVideoRecording playtest = RecordingAPhone(_videoRecording);
            ProtokitePlaytestVideoRecording testVideo = RecordingAPhone(_testVideo);
            if (playtest == null && testVideo == null)
                return;

            _secondsSinceThePhoneWasAsked += frameSeconds;
            if (_secondsSinceThePhoneWasAsked >= ProtokitePlaytestPhoneConditions.TimeBetweenQuestions.TotalSeconds)
            {
                _secondsSinceThePhoneWasAsked = 0.0;
                AskThePhone(WantsTheHeat(playtest) || WantsTheHeat(testVideo), WantsTheBattery(playtest) || WantsTheBattery(testVideo));
            }
            KeepWithinThePhonesLimits(playtest, "the playtest's recording", "kept and uploaded as usual");
            KeepWithinThePhonesLimits(testVideo, "the test video", "kept");
        }

        private static ProtokitePlaytestVideoRecording RecordingAPhone(ProtokitePlaytestVideoRecording recording)
            => recording != null && recording.IsCapturing && recording.RecordsAPhone ? recording : null;

        private static bool WantsTheHeat(ProtokitePlaytestVideoRecording recording) => recording != null && recording.SlowsDownWhenHot;

        private static bool WantsTheBattery(ProtokitePlaytestVideoRecording recording) => recording != null && recording.StopsBelowBatteryPercent > 0;

        // Only what a recording's settings use is asked, so a studio that turned both off costs the phone nothing.
        private static void AskThePhone(bool heat, bool battery)
        {
            if (heat)
            {
                _thermalStatus = ProtokitePlaytestPhoneConditions.ReadThermalStatus(out string whyNot);
                if (!_thermalStatus.HasValue && !_saidThePhoneDoesNotReportItsHeat)
                {
                    _saidThePhoneDoesNotReportItsHeat = true;
                    Debug.Log(LogPrefix + "Video is not slowed down for heat on this phone: " + whyNot);
                }
            }
            if (battery)
                (_batteryLevel, _batteryStatus) = ProtokitePlaytestPhoneConditions.ReadBattery();
        }

        private static void KeepWithinThePhonesLimits(ProtokitePlaytestVideoRecording recording, string what, string kept)
        {
            if (recording == null)
                return;
            if (ProtokitePlaytestPhoneConditions.BatteryTooLow(_batteryLevel, _batteryStatus, recording.StopsBelowBatteryPercent))
            {
                Debug.Log(LogPrefix + $"The phone's battery is at {Mathf.RoundToInt(_batteryLevel * 100f)}% and not charging, below Stop The Recording Below Battery Percent " +
                    $"({recording.StopsBelowBatteryPercent}), so {what} stops for this launch; what it holds is {kept}.");
                recording.StopCapturing(ProtokitePlaytestVideoStopReason.BatteryLow);
                return;
            }
            switch (ProtokitePlaytestPhoneConditions.HeatStep(_thermalStatus, recording.SlowsDownWhenHot))
            {
                case ProtokitePlaytestHeatStep.Stop:
                    Debug.Log(LogPrefix + $"Android says it is slowing the phone down for heat enough for the player to notice (thermal status {_thermalStatus}), " +
                        $"so {what} stops for this launch; what it holds is {kept}.");
                    recording.StopCapturing(ProtokitePlaytestVideoStopReason.PhoneTooHot);
                    break;
                case ProtokitePlaytestHeatStep.HalfFrameRate:
                    if (!recording.RecordsAtHalfTheFrameRate)
                    {
                        recording.RecordsAtHalfTheFrameRate = true;
                        Debug.Log(LogPrefix + $"Android says it has started to slow the phone down for heat (thermal status {_thermalStatus}), so {what} takes " +
                            $"{recording.FramesPerSecond / 2.0:0.#} frames a second, half its rate, until the phone cools.");
                    }
                    break;
                default:
                    if (recording.RecordsAtHalfTheFrameRate)
                    {
                        recording.RecordsAtHalfTheFrameRate = false;
                        Debug.Log(LogPrefix + (_thermalStatus.HasValue ? $"The phone has cooled (thermal status {_thermalStatus}), " : "The phone no longer reports its heat, ") +
                            $"so {what} takes {recording.FramesPerSecond} frames a second again.");
                    }
                    break;
            }
        }

        // Each recording's start asks the phone afresh, so a new launch only says again what the phone does not report.
        private static void ResetPhoneLimitsForNewLaunch()
        {
            _saidThePhoneDoesNotReportItsHeat = false;
            ProtokitePlaytestPhoneConditions.ForgetForNewLaunch();
        }
    }
}
