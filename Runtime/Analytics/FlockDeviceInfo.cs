using Newtonsoft.Json;
using UnityEngine;

namespace Flock.Analytics
{
    public class FlockDeviceInfo
    {
        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("operating_system")]
        public string OperatingSystem { get; set; }

        [JsonProperty("device_model")]
        public string DeviceModel { get; set; }

        [JsonProperty("device_type")]
        public string DeviceType { get; set; }

        [JsonProperty("app_version")]
        public string AppVersion { get; set; }

        [JsonProperty("screen_width")]
        public int ScreenWidth { get; set; }

        [JsonProperty("screen_height")]
        public int ScreenHeight { get; set; }

        [JsonProperty("screen_dpi")]
        public float ScreenDpi { get; set; }

        [JsonProperty("system_language")]
        public string SystemLanguage { get; set; }

        [JsonProperty("graphics_device_name")]
        public string GraphicsDeviceName { get; set; }

        [JsonProperty("system_memory_mb")]
        public int SystemMemoryMb { get; set; }

        [JsonProperty("sdk_version")]
        public string SdkVersion { get; set; }

        internal static FlockDeviceInfo Capture()
        {
            return new FlockDeviceInfo
            {
                Platform = Application.platform.ToString(),
                OperatingSystem = SystemInfo.operatingSystem,
                DeviceModel = SystemInfo.deviceModel,
                DeviceType = SystemInfo.deviceType.ToString(),
                AppVersion = Application.version,
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height,
                ScreenDpi = Screen.dpi,
                SystemLanguage = Application.systemLanguage.ToString(),
                GraphicsDeviceName = SystemInfo.graphicsDeviceName,
                SystemMemoryMb = SystemInfo.systemMemorySize,
                SdkVersion = FlockSdkVersion.Current
            };
        }
    }
}
