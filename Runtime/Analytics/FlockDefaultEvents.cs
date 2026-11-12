using System.Collections.Generic;

namespace Flock.Analytics
{
    /// <summary>The SDK's own analytics events and the flock_ parameters every analytics event carries.</summary>
    internal static class FlockDefaultEvents
    {
        internal const string AppOpen = "app_open";
        internal const string FirstLaunch = "first_launch";
        internal const string AppUpdate = "app_update";
        internal const string AppCategory = "app";

        internal const string ScreenView = "screen_view";
        internal const string ScreenCategory = "screen";

        internal const string StorePurchase = "store_purchase";
        internal const string StoreCategory = "store";
        internal const string Cancelled = "cancelled";
        internal const string Failed = "failed";
        internal const string Deferred = "deferred";

        internal const string Platform = "flock_platform";
        internal const string OperatingSystem = "flock_os";
        internal const string DeviceModel = "flock_device_model";
        internal const string AppVersion = "flock_app_version";
        internal const string SdkVersion = "flock_sdk_version";
        internal const string SessionNumber = "flock_session_number";
        internal const string Language = "flock_language";

        /// <summary>Set once first_launch is recorded for this game on this device; each game (its backend and key) has its own.</summary>
        internal static string FirstLaunchRecordedPrefFor(string gameKey) => "flock_first_launch_recorded_" + gameKey;

        /// <summary>The app version this game last started with on this device; app_update when it changes.</summary>
        internal static string LastAppVersionPrefFor(string gameKey) => "flock_last_app_version_" + gameKey;

        /// <summary>The SDK's own event names; a game's TrackEvent of one is refused, or each would count twice.</summary>
        internal static readonly string[] Names = { FlockSessionEndEvent.EventName, AppOpen, FirstLaunch, AppUpdate, StorePurchase, ScreenView };

        internal static bool IsTheSdks(string eventName)
        {
            foreach (string name in Names)
            {
                if (string.Equals(name, eventName, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>Adds the flock_ parameters; a property the event already has keeps its value. No session number while none runs.</summary>
        internal static void AddParameters(Dictionary<string, object> properties, FlockDeviceInfo device, int? sessionNumber)
        {
            if (device != null)
            {
                AddUnlessSet(properties, Platform, device.Platform);
                AddUnlessSet(properties, OperatingSystem, device.OperatingSystem);
                AddUnlessSet(properties, DeviceModel, device.DeviceModel);
                AddUnlessSet(properties, AppVersion, device.AppVersion);
                AddUnlessSet(properties, Language, device.SystemLanguage);
            }
            AddUnlessSet(properties, SdkVersion, device?.SdkVersion ?? FlockSdkVersion.Current);
            if (sessionNumber.HasValue)
                AddUnlessSet(properties, SessionNumber, sessionNumber.Value);
        }

        private static void AddUnlessSet(Dictionary<string, object> properties, string key, object value)
        {
            if (value != null && !properties.ContainsKey(key))
                properties[key] = value;
        }
    }
}
