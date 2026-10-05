using System;

namespace Protokite.Playtest
{
    /// <summary>The video settings one recording uses: the settings asset's values, each moved into the range it can take.</summary>
    internal sealed class ProtokitePlaytestVideoSettings
    {
        /// <summary>The conversion to the encoder's pixel layout packs pixels in fours, and H.264 codes 16-pixel blocks, so every side is a multiple of this.</summary>
        internal const int SideMultiple = 16;

        /// <summary>The longer side an Android player's video takes when the settings asset says nothing.</summary>
        internal const int DefaultAndroidLongSide = 1280;

        public int MaxVideoWidth = 1280;
        public int MaxVideoHeight = 720;
        public int FramesPerSecond = 15;
        public int BitrateKbps = 1500;

        /// <summary>Whether the platform's software encoder may be used on a machine whose hardware has none.</summary>
        public bool AllowSoftwareEncoder;

        /// <summary>Whether this platform records video at all: off only where a studio turned Android video off.</summary>
        public bool RecordVideo = true;

        /// <summary>Whether these are an Android player's settings, read from the Android section.</summary>
        public bool ForAndroid;

        /// <summary>Whether an Android recording takes half its frame rate while the phone is warm and stops when it is too hot.</summary>
        public bool SlowDownWhenHot = true;

        /// <summary>The battery percentage below which an Android recording stops while the phone is not charging; 0 never.</summary>
        public int StopBelowBatteryPercent = DefaultStopBelowBatteryPercent;

        internal const int DefaultStopBelowBatteryPercent = 15;
        internal const long DefaultAndroidDiskBudgetBytes = 1024L * 1024 * 1024;

        /// <summary>The game's graphics card maker, whose encoder is tried first; 0 when unknown.</summary>
        public int GraphicsCardVendorId;

        public double MaxSeconds = 3600.0;
        public long MaxBytes = 1536L * 1024 * 1024;
        public long DiskBudgetBytes = 4096L * 1024 * 1024;

        /// <summary>Room a file's header takes, with plenty to spare.</summary>
        private const long FileHeaderAllowance = 4096;

        /// <summary>How long each captured frame is shown, in milliseconds.</summary>
        public long FrameDurationMs => Math.Max(1L, (long)Math.Round(1000.0 / FramesPerSecond));

        /// <summary>The settings asset's values for the platform this runs on, in range: an Android player reads the Android section, everything else (an editor included, whatever it builds for) the other.</summary>
        public static ProtokitePlaytestVideoSettings From(ProtokitePlaytestSettings settings)
            => From(settings, ProtokitePlaytestVideoEncoders.OnAndroid);

        /// <summary>The settings asset's values in range, read as an Android player's or not. The asset's inspector keeps them in range, but a file edited by hand can hold anything.</summary>
        internal static ProtokitePlaytestVideoSettings From(ProtokitePlaytestSettings settings, bool forAndroid)
        {
            ProtokitePlaytestVideoSettings video = new ProtokitePlaytestVideoSettings { ForAndroid = forAndroid };
            if (forAndroid)
            {
                // The longer side is the limit whichever way the phone is held, so the video fits a square of that side.
                int longSide = settings == null ? DefaultAndroidLongSide : Clamp(settings.AndroidVideoLongSide, 320, 1920);
                video.MaxVideoWidth = longSide;
                video.MaxVideoHeight = longSide;
                video.DiskBudgetBytes = DefaultAndroidDiskBudgetBytes;
            }
            if (settings == null)
                return video;
            if (forAndroid)
            {
                video.FramesPerSecond = Clamp(settings.AndroidVideoFramesPerSecond, 1, 30);
                video.BitrateKbps = Clamp(settings.AndroidVideoBitrateKbps, 100, 20000);
                video.AllowSoftwareEncoder = settings.AndroidAllowSoftwareEncoder;
                video.RecordVideo = settings.RecordVideoOnAndroid;
                video.SlowDownWhenHot = settings.SlowDownTheRecordingWhenThePhoneIsHot;
                video.StopBelowBatteryPercent = Clamp(settings.StopTheRecordingBelowBatteryPercent, 0, 100);
            }
            else
            {
                video.MaxVideoWidth = Clamp(settings.VideoWidth, SideMultiple, 3840);
                video.MaxVideoHeight = Clamp(settings.VideoHeight, SideMultiple, 2160);
                video.FramesPerSecond = Clamp(settings.VideoFramesPerSecond, 1, 60);
                video.BitrateKbps = Clamp(settings.VideoBitrateKbps, 100, 50000);
                video.AllowSoftwareEncoder = settings.AllowSoftwareEncoder;
            }
            float minutes = settings.MaxRecordingMinutes;
            video.MaxSeconds = float.IsNaN(minutes) || minutes < 0.1f ? 6.0 : Math.Min(minutes, 1e6) * 60.0;
            video.MaxBytes = Math.Max(1L, settings.MaxRecordingSizeMb) * 1024 * 1024;
            video.DiskBudgetBytes = Math.Max(1L, forAndroid ? settings.AndroidRecordingsDiskBudgetMb : settings.RecordingsDiskBudgetMb) * 1024 * 1024;
            return video;
        }

        /// <summary>The room a recording is expected to need: its length limit at its bitrate plus a quarter, never more than its size limit.</summary>
        // Reserving the size limit instead would have the shortest recording delete recordings waiting to upload.
        public long BytesToMakeRoomFor(int bytesAddedToEachFrame)
        {
            // The encoder holds the bitrate as a ceiling (a still picture measured far under it); a quarter more covers what it overshoots.
            double seconds = Math.Max(0.0, MaxSeconds);
            double videoBytes = BitrateKbps * 1000.0 / 8.0 * seconds * 1.25;
            double frameBytes = Math.Ceiling(seconds * FramesPerSecond) * bytesAddedToEachFrame;
            double wanted = Math.Ceiling(videoBytes + frameBytes + FileHeaderAllowance);
            return wanted >= MaxBytes ? MaxBytes : (long)wanted;
        }

        /// <summary>What the encoder is configured with for a video of this size.</summary>
        public ProtokitePlaytestVideoEncoderSettings EncoderSettings(int width, int height) => new ProtokitePlaytestVideoEncoderSettings
        {
            Width = width,
            Height = height,
            FramesPerSecond = FramesPerSecond,
            BitrateKbps = BitrateKbps,
            AllowSoftwareEncoder = AllowSoftwareEncoder,
            GraphicsCardVendorId = GraphicsCardVendorId
        };

        /// <summary>
        /// The size a screen of this size is recorded at: its shape kept, fitted inside the maximum, never enlarged, and each side
        /// rounded down to a multiple of 16. False for a screen with no size.
        /// </summary>
        public static bool FitVideoSize(int screenWidth, int screenHeight, int maxWidth, int maxHeight, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (screenWidth <= 0 || screenHeight <= 0)
                return false;
            double scale = Math.Min(1.0, Math.Min((double)maxWidth / screenWidth, (double)maxHeight / screenHeight));
            width = RoundDownToSide(Math.Min((int)Math.Round(screenWidth * scale), maxWidth));
            height = RoundDownToSide(Math.Min((int)Math.Round(screenHeight * scale), maxHeight));
            return true;
        }

        private static int RoundDownToSide(int pixels) => Math.Max(SideMultiple, pixels / SideMultiple * SideMultiple);

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
