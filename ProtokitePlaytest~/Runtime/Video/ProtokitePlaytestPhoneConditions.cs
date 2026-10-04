using System;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>What a recording does about the phone's heat.</summary>
    internal enum ProtokitePlaytestHeatStep
    {
        /// <summary>Records at its frame rate.</summary>
        FullFrameRate,

        /// <summary>Android says the phone has started to slow itself down: half the frame rate until it cools.</summary>
        HalfFrameRate,

        /// <summary>Android says the phone is slowing itself down enough for the player to notice: the recording stops for the launch.</summary>
        Stop
    }

    /// <summary>The phone's heat, battery and free space as Android reports them ("not known" outside an Android player), and what a recording does about each.</summary>
    internal static class ProtokitePlaytestPhoneConditions
    {
        /// <summary>PowerManager.THERMAL_STATUS_MODERATE: Android has started to slow the phone down.</summary>
        internal const int ModerateThermalStatus = 2;

        /// <summary>PowerManager.THERMAL_STATUS_SEVERE: Android slows the phone down enough for the player to notice.</summary>
        internal const int SevereThermalStatus = 3;

        /// <summary>The most free space Android's own low-storage line asks for (StorageManager: 500 MB, or 5% of the storage when that is less).</summary>
        internal const long MostAndroidKeepsFree = 500L * 1024 * 1024;

        /// <summary>How often the phone is asked about its heat and battery while a recording runs (0.14 ms a question on a phone, measured).</summary>
        internal static readonly TimeSpan TimeBetweenQuestions = TimeSpan.FromSeconds(5);

        /// <summary>Stands in for Android's thermal status (0 none to 6 shutdown); null stands for a phone that does not say.</summary>
        internal static Func<int?> ThermalStatusForTesting;

        /// <summary>Stands in for the battery: its level from 0 to 1 (-1 not known), and whether it is charging.</summary>
        internal static Func<(float Level, BatteryStatus Status)> BatteryForTesting;

        /// <summary>Stands in for the free space and size of the storage holding a folder; null stands for storage that does not say.</summary>
        internal static Func<string, (long FreeBytes, long TotalBytes)?> DiskSpaceForTesting;

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject _powerManager;
        // Why the phone could not say, once it could not: a phone before Android 10 is not asked again every few seconds.
        private static string _heatNotReportedBecause;
#endif

        /// <summary>What a recording does at this thermal status; a status not known records as set.</summary>
        public static ProtokitePlaytestHeatStep HeatStep(int? thermalStatus, bool slowDownWhenHot)
        {
            if (!slowDownWhenHot || !thermalStatus.HasValue)
                return ProtokitePlaytestHeatStep.FullFrameRate;
            if (thermalStatus.Value >= SevereThermalStatus)
                return ProtokitePlaytestHeatStep.Stop;
            return thermalStatus.Value >= ModerateThermalStatus ? ProtokitePlaytestHeatStep.HalfFrameRate : ProtokitePlaytestHeatStep.FullFrameRate;
        }

        /// <summary>Whether the battery is below the percentage while not charging; a level not known, or a percentage of 0, never is.</summary>
        public static bool BatteryTooLow(float level, BatteryStatus status, int stopBelowPercent)
        {
            if (stopBelowPercent <= 0 || level < 0f || status == BatteryStatus.Charging || status == BatteryStatus.Full)
                return false;
            // Rounded as the phone shows it, so a level read as 0.15 is 15% and not 14.99%.
            return Mathf.RoundToInt(level * 100f) < stopBelowPercent;
        }

        /// <summary>The free space recordings leave on storage of this size: the line below which Android warns that storage is running out.</summary>
        public static long LowStorageLine(long totalBytes) => Math.Min(MostAndroidKeepsFree, Math.Max(0L, totalBytes / 20));

        /// <summary>Android's thermal status (Android 10 and later), or null with why the phone does not say.</summary>
        public static int? ReadThermalStatus(out string whyNot)
        {
            whyNot = null;
            if (ThermalStatusForTesting != null)
            {
                int? status = ThermalStatusForTesting();
                if (!status.HasValue)
                    whyNot = "the phone does not report its heat.";
                return status;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_heatNotReportedBecause != null)
            {
                whyNot = _heatNotReportedBecause;
                return null;
            }
            try
            {
                if (_powerManager == null)
                {
                    using (AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                        _powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                }
                return _powerManager.Call<int>("getCurrentThermalStatus");
            }
            catch (Exception ex)
            {
                whyNot = _heatNotReportedBecause = "the phone does not report its heat (Android 10 and later do): " + ex.Message;
                return null;
            }
#else
            whyNot = "only an Android player reports the phone's heat.";
            return null;
#endif
        }

        /// <summary>The battery's level from 0 to 1 (-1 when not known) and status; known only in an Android player.</summary>
        public static (float Level, BatteryStatus Status) ReadBattery()
        {
            if (BatteryForTesting != null)
                return BatteryForTesting();
#if UNITY_ANDROID && !UNITY_EDITOR
            return (SystemInfo.batteryLevel, SystemInfo.batteryStatus);
#else
            return (-1f, BatteryStatus.Unknown);
#endif
        }

        /// <summary>The free space and size of the storage holding the folder (Android's StatFs: .NET's DriveInfo throws in an IL2CPP player), or null with why.</summary>
        public static (long FreeBytes, long TotalBytes)? ReadDiskSpace(string folder, out string whyNot)
        {
            whyNot = null;
            if (DiskSpaceForTesting != null)
            {
                (long FreeBytes, long TotalBytes)? space = DiskSpaceForTesting(folder);
                if (!space.HasValue)
                    whyNot = "the storage does not say how much is free.";
                return space;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaObject statFs = new AndroidJavaObject("android.os.StatFs", folder))
                    return (statFs.Call<long>("getAvailableBytes"), statFs.Call<long>("getTotalBytes"));
            }
            catch (Exception ex)
            {
                whyNot = "Android would not say how much of the phone's storage is free: " + ex.Message;
                return null;
            }
#else
            whyNot = "only an Android player reads the phone's free space.";
            return null;
#endif
        }

        /// <summary>Lets go of what was kept from Java, for a new launch.</summary>
        internal static void ForgetForNewLaunch()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _powerManager?.Dispose();
            _powerManager = null;
            _heatNotReportedBecause = null;
#endif
        }
    }
}
