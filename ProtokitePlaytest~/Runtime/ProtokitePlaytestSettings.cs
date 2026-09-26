using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>The project's playtest settings, kept at Assets/Resources/ProtokitePlaytestSettings.asset so every build carries them.</summary>
    public sealed class ProtokitePlaytestSettings : ScriptableObject
    {
        /// <summary>The name <see cref="Resources.Load(string)"/> finds the settings under.</summary>
        public const string ResourceName = "ProtokitePlaytestSettings";

        /// <summary>Where the settings asset is created.</summary>
        public const string AssetPath = "Assets/Resources/" + ResourceName + ".asset";

        /// <summary>The Protokite API a new project talks to: production. Set http://localhost:8020 for a local stack.</summary>
        public const string DefaultProtokiteApiUrl = "https://api-protokite.qwacks.com";

        [Tooltip("Off by default. While off, the playtest records nothing and sends nothing.")]
        [SerializeField] private bool playtestingEnabled;

        [Tooltip("The Protokite API this game reports to.")]
        [SerializeField] private string protokiteApiUrl = DefaultProtokiteApiUrl;

        [Header("Video recording (64-bit Windows)")]
        [Tooltip("VP8 costs a slow PC least. VP9 makes smaller files for more processor time.")]
        [SerializeField] private ProtokitePlaytestVideoCodec videoCodec = ProtokitePlaytestVideoCodec.Vp8;

        [Tooltip("The widest the video is, in pixels. The screen's shape is kept, a smaller window is not enlarged, and each side is rounded down to a multiple of 16.")]
        [SerializeField, Range(16, 3840)] private int videoWidth = 1280;

        [Tooltip("The tallest the video is, in pixels. See Video Width.")]
        [SerializeField, Range(16, 2160)] private int videoHeight = 720;

        [Tooltip("Frames recorded each second of play.")]
        [SerializeField, Range(1, 60)] private int videoFramesPerSecond = 15;

        [Tooltip("The video's bitrate, in kilobits a second.")]
        [SerializeField, Range(100, 50000)] private int videoBitrateKbps = 1500;

        [Tooltip("Threads the encoder uses. One costs the game least.")]
        [SerializeField, Range(1, 16)] private int encoderThreads = 1;

        [Tooltip("On: the codec's own speed (12 for VP8, 8 for VP9). Off: Encoder Speed.")]
        [SerializeField] private bool useCodecDefaultSpeed = true;

        [Tooltip("Higher is faster and looks worse: VP8 takes -16 to 16, VP9 -9 to 9. Used when Use Codec Default Speed is off.")]
        [SerializeField, Range(-16, 16)] private int encoderSpeed = 12;

        [Tooltip("On: the encoder gives way to the game when the processor is busy.")]
        [SerializeField] private bool encoderBelowGamePriority = true;

        [Tooltip("A recording stops for good after this many minutes of play.")]
        [SerializeField, Min(0.1f)] private float maxRecordingMinutes = 60f;

        [Tooltip("A recording stops for good before its file passes this many megabytes.")]
        [SerializeField, Min(1)] private int maxRecordingSizeMb = 1536;

        /// <summary>Whether playtesting is switched on for this project.</summary>
        public bool PlaytestingEnabled
        {
            get => playtestingEnabled;
            set => playtestingEnabled = value;
        }

        /// <summary>The Protokite API this game reports to.</summary>
        public string ProtokiteApiUrl
        {
            get => protokiteApiUrl;
            set => protokiteApiUrl = value;
        }

        /// <summary>The codec recordings use.</summary>
        public ProtokitePlaytestVideoCodec VideoCodec { get => videoCodec; set => videoCodec = value; }

        /// <summary>The widest the video is, in pixels.</summary>
        public int VideoWidth { get => videoWidth; set => videoWidth = value; }

        /// <summary>The tallest the video is, in pixels.</summary>
        public int VideoHeight { get => videoHeight; set => videoHeight = value; }

        /// <summary>Frames recorded each second of play.</summary>
        public int VideoFramesPerSecond { get => videoFramesPerSecond; set => videoFramesPerSecond = value; }

        /// <summary>The video's bitrate, in kilobits a second.</summary>
        public int VideoBitrateKbps { get => videoBitrateKbps; set => videoBitrateKbps = value; }

        /// <summary>Threads the encoder uses.</summary>
        public int EncoderThreads { get => encoderThreads; set => encoderThreads = value; }

        /// <summary>Whether the encoder runs at its codec's own speed rather than <see cref="EncoderSpeed"/>.</summary>
        public bool UseCodecDefaultSpeed { get => useCodecDefaultSpeed; set => useCodecDefaultSpeed = value; }

        /// <summary>The encoder's speed when <see cref="UseCodecDefaultSpeed"/> is off.</summary>
        public int EncoderSpeed { get => encoderSpeed; set => encoderSpeed = value; }

        /// <summary>Whether the encoder gives way to the game when the processor is busy.</summary>
        public bool EncoderBelowGamePriority { get => encoderBelowGamePriority; set => encoderBelowGamePriority = value; }

        /// <summary>Minutes of play after which a recording stops for good.</summary>
        public float MaxRecordingMinutes { get => maxRecordingMinutes; set => maxRecordingMinutes = value; }

        /// <summary>Megabytes a recording's file stops before passing.</summary>
        public int MaxRecordingSizeMb { get => maxRecordingSizeMb; set => maxRecordingSizeMb = value; }

        /// <summary>The project's settings, or null when the project has none (which reads as playtesting off).</summary>
        public static ProtokitePlaytestSettings Load() => Resources.Load<ProtokitePlaytestSettings>(ResourceName);
    }
}
