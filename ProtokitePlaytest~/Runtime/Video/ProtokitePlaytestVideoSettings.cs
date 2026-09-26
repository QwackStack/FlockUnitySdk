using System;

namespace Protokite.Playtest
{
    /// <summary>The video settings one recording uses: the settings asset's values, each moved into the range it can take.</summary>
    internal sealed class ProtokitePlaytestVideoSettings
    {
        /// <summary>The conversion to the encoder's pixel layout packs pixels in fours, so every side is a multiple of this.</summary>
        internal const int SideMultiple = 16;

        public ProtokitePlaytestVideoCodec Codec = ProtokitePlaytestVideoCodec.Vp8;
        public int MaxVideoWidth = 1280;
        public int MaxVideoHeight = 720;
        public int FramesPerSecond = 15;
        public int BitrateKbps = 1500;
        public int Threads = 1;

        /// <summary>Null means the codec's own speed.</summary>
        public int? Speed;

        public bool EncoderBelowGamePriority = true;
        public double MaxSeconds = 3600.0;
        public long MaxBytes = 1536L * 1024 * 1024;

        /// <summary>How long each captured frame is shown, in milliseconds.</summary>
        public long FrameDurationMs => Math.Max(1L, (long)Math.Round(1000.0 / FramesPerSecond));

        /// <summary>The settings asset's values, in range. The asset's inspector keeps them there, but a file edited by hand can hold anything.</summary>
        public static ProtokitePlaytestVideoSettings From(ProtokitePlaytestSettings settings)
        {
            ProtokitePlaytestVideoSettings video = new ProtokitePlaytestVideoSettings();
            if (settings == null)
                return video;
            video.Codec = settings.VideoCodec == ProtokitePlaytestVideoCodec.Vp9 ? ProtokitePlaytestVideoCodec.Vp9 : ProtokitePlaytestVideoCodec.Vp8;
            video.MaxVideoWidth = Clamp(settings.VideoWidth, SideMultiple, 3840);
            video.MaxVideoHeight = Clamp(settings.VideoHeight, SideMultiple, 2160);
            video.FramesPerSecond = Clamp(settings.VideoFramesPerSecond, 1, 60);
            video.BitrateKbps = Clamp(settings.VideoBitrateKbps, 100, 50000);
            video.Threads = Clamp(settings.EncoderThreads, 1, 16);
            video.Speed = settings.UseCodecDefaultSpeed ? (int?)null : Clamp(settings.EncoderSpeed, -16, 16);
            video.EncoderBelowGamePriority = settings.EncoderBelowGamePriority;
            float minutes = settings.MaxRecordingMinutes;
            video.MaxSeconds = float.IsNaN(minutes) || minutes < 0.1f ? 6.0 : Math.Min(minutes, 1e6) * 60.0;
            video.MaxBytes = Math.Max(1L, settings.MaxRecordingSizeMb) * 1024 * 1024;
            return video;
        }

        /// <summary>What the encoder is configured with for a video of this size.</summary>
        public ProtokitePlaytestVideoEncoderSettings EncoderSettings(int width, int height) => new ProtokitePlaytestVideoEncoderSettings
        {
            Codec = Codec,
            Width = width,
            Height = height,
            FramesPerSecond = FramesPerSecond,
            BitrateKbps = BitrateKbps,
            Threads = Threads,
            Speed = Speed
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
