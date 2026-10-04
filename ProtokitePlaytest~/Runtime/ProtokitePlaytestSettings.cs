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

        [Tooltip("Off by default. While off, the playtest records nothing and starts no session; recordings an earlier playtest build of the game kept are still uploaded, unless the player asked the playtest to collect nothing.")]
        [SerializeField] private bool playtestingEnabled;

        [Tooltip("The Protokite API this game reports to.")]
        [SerializeField] private string protokiteApiUrl = DefaultProtokiteApiUrl;

        [Header("Player consent")]
        [Tooltip("On by default: the player is asked what this playtest may collect (the screen, play data, both or nothing), and nothing is collected until they answer. The answer is kept on their machine for later launches. Turn it off only where players were asked another way, or for a test run with nobody to answer; everything the playtest turns on is then collected, and each session says nobody was asked.")]
        [SerializeField] private bool askThePlayerForPlaytestConsent = true;

        [Header("Feedback form")]
        [Tooltip("The key that opens the playtest's feedback form, and closes it again, whenever the playtest has a published form; read when the playtest loads. Click Detect Key and press the key, or choose it from the list. None: only ProtokitePlaytest.OpenFeedbackForm opens it.")]
        [SerializeField, ProtokitePlaytestKeyField] private KeyCode feedbackFormKey = KeyCode.F9;

        [Tooltip("Off by default: a playtest is about what the player was doing, and many games cannot be paused. On: the game's time scale is 0 while the form is open, and put back when it closes unless the game set another one meanwhile; a game that pauses itself too (time scale 0) while the form is open is unpaused with it.")]
        [SerializeField] private bool pauseTheGameWhileTheFormIsOpen;

        [Header("Video recording (64-bit Windows)")]
        [Tooltip("The widest the video is, in pixels. The screen's shape is kept, a smaller window is not enlarged, and each side is rounded down to a multiple of 16.")]
        [SerializeField, Range(16, 3840)] private int videoWidth = 1280;

        [Tooltip("The tallest the video is, in pixels. See Video Width.")]
        [SerializeField, Range(16, 2160)] private int videoHeight = 720;

        [Tooltip("Frames recorded each second of play.")]
        [SerializeField, Range(1, 60)] private int videoFramesPerSecond = 15;

        [Tooltip("The video's bitrate, in kilobits a second.")]
        [SerializeField, Range(100, 50000)] private int videoBitrateKbps = 1500;

        [Tooltip("Off by default: video is encoded on the graphics card's own video engine, and a PC whose graphics card has none records no video. On: such a PC records with Windows' own encoder on the processor instead, which costs the game frame rate.")]
        [SerializeField] private bool allowSoftwareEncoder;

        [Header("Video recording (Android)")]
        [Tooltip("On by default: Android players record the screen when the playtest's config turns video on and the player agrees. Off: Android players record no video and never ask the phone for its encoders; everything else in the playtest still runs. Read as the game starts: turned on later in a launch, it takes effect from the next one.")]
        [SerializeField] private bool recordVideoOnAndroid = true;

        [Tooltip("The longer side of the video, in pixels, whichever way the phone is held: on a 20:9 phone, 1280 records a game held sideways at 1280x592 and one held upright at 592x1280. A smaller screen is not enlarged, and each side is rounded down to a multiple of 16. A phone whose encoder does not take the size records at the next size down it does, and says so.")]
        [SerializeField, Range(320, 1920)] private int androidVideoLongSide = 1280;

        [Tooltip("Frames recorded each second of play. 30 doubles the phone's encoding work and the frames the capture copies from the screen.")]
        [SerializeField, Range(1, 30)] private int androidVideoFramesPerSecond = 15;

        [Tooltip("The video's bitrate, in kilobits a second.")]
        [SerializeField, Range(100, 20000)] private int androidVideoBitrateKbps = 1500;

        [Tooltip("Off by default: video is encoded by the phone's hardware encoder, and a phone without one records no video. On: such a phone records with Android's software encoder on the processor instead, which costs the game frame rate and battery.")]
        [SerializeField] private bool androidAllowSoftwareEncoder;

        [Tooltip("On by default: when Android says the phone has started to slow itself down for heat (thermal status moderate), video is recorded at half its frame rate until the phone cools; when Android says it is slowing the phone down enough for the player to notice (severe or above), the recording stops for the launch and what it holds is kept and uploaded as usual. Phones before Android 10 do not report their heat, and record as set.")]
        [SerializeField] private bool slowDownTheRecordingWhenThePhoneIsHot = true;

        [Tooltip("The recording stops for the launch, and what it holds is kept and uploaded as usual, when the phone's battery falls below this percentage while it is not charging. 0: never.")]
        [SerializeField, Range(0, 100)] private int stopTheRecordingBelowBatteryPercent = 15;

        [Tooltip("The most every recording kept on the phone may take together, in megabytes, in place of Recordings Disk Budget Mb. Recordings never take the phone's free space below the line where Android warns that storage is running out (500 MB, or a twentieth of the storage when that is less), so a fuller phone gives them less.")]
        [SerializeField, Min(1)] private int androidRecordingsDiskBudgetMb = 1024;

        [Header("Recordings (every platform)")]
        [Tooltip("A recording stops for good after this many minutes of play.")]
        [SerializeField, Min(0.1f)] private float maxRecordingMinutes = 60f;

        [Tooltip("A recording stops for good before its file passes this many megabytes.")]
        [SerializeField, Min(1)] private int maxRecordingSizeMb = 1536;

        [Tooltip("The most every recording kept on this machine may take together, in megabytes (on Android, Android Recordings Disk Budget Mb instead). To make room for a new recording, those whose game has closed are deleted, the oldest first.")]
        [SerializeField, Min(1)] private int recordingsDiskBudgetMb = 4096;

        [Header("Heavy analytics")]
        [Tooltip("With heavy analytics on, a frame that takes this many milliseconds or longer is counted as a hitch.")]
        [SerializeField, Min(1f)] private float hitchFrameTimeMs = 60f;

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

        /// <summary>Whether the player is asked what this playtest may collect before anything is. An answer already given counts either way.</summary>
        public bool AskThePlayerForPlaytestConsent
        {
            get => askThePlayerForPlaytestConsent;
            set => askThePlayerForPlaytestConsent = value;
        }

        /// <summary>The key that opens and closes the feedback form; <see cref="KeyCode.None"/> leaves opening it to the game.</summary>
        public KeyCode FeedbackFormKey { get => feedbackFormKey; set => feedbackFormKey = value; }

        /// <summary>Whether the game's time scale is 0 while the feedback form is open.</summary>
        public bool PauseTheGameWhileTheFormIsOpen { get => pauseTheGameWhileTheFormIsOpen; set => pauseTheGameWhileTheFormIsOpen = value; }

        /// <summary>The widest the video is, in pixels.</summary>
        public int VideoWidth { get => videoWidth; set => videoWidth = value; }

        /// <summary>The tallest the video is, in pixels.</summary>
        public int VideoHeight { get => videoHeight; set => videoHeight = value; }

        /// <summary>Frames recorded each second of play.</summary>
        public int VideoFramesPerSecond { get => videoFramesPerSecond; set => videoFramesPerSecond = value; }

        /// <summary>The video's bitrate, in kilobits a second.</summary>
        public int VideoBitrateKbps { get => videoBitrateKbps; set => videoBitrateKbps = value; }

        /// <summary>Whether a PC whose graphics card has no video encoder records with Windows' software encoder, at a cost to the game's frame rate.</summary>
        public bool AllowSoftwareEncoder { get => allowSoftwareEncoder; set => allowSoftwareEncoder = value; }

        /// <summary>Whether Android players record video at all; off, the phone is never asked for its encoders.</summary>
        public bool RecordVideoOnAndroid { get => recordVideoOnAndroid; set => recordVideoOnAndroid = value; }

        /// <summary>The longer side of an Android player's video, in pixels, whichever way the phone is held.</summary>
        public int AndroidVideoLongSide { get => androidVideoLongSide; set => androidVideoLongSide = value; }

        /// <summary>Frames an Android player records each second of play.</summary>
        public int AndroidVideoFramesPerSecond { get => androidVideoFramesPerSecond; set => androidVideoFramesPerSecond = value; }

        /// <summary>An Android player's video bitrate, in kilobits a second.</summary>
        public int AndroidVideoBitrateKbps { get => androidVideoBitrateKbps; set => androidVideoBitrateKbps = value; }

        /// <summary>Whether a phone with no hardware video encoder records with Android's software encoder, at a cost to frame rate and battery.</summary>
        public bool AndroidAllowSoftwareEncoder { get => androidAllowSoftwareEncoder; set => androidAllowSoftwareEncoder = value; }

        /// <summary>Whether an Android player's recording takes half its frame rate while the phone is warm, and stops for the launch when it is too hot.</summary>
        public bool SlowDownTheRecordingWhenThePhoneIsHot { get => slowDownTheRecordingWhenThePhoneIsHot; set => slowDownTheRecordingWhenThePhoneIsHot = value; }

        /// <summary>The battery percentage below which an Android player's recording stops for the launch while the phone is not charging; 0 never stops it.</summary>
        public int StopTheRecordingBelowBatteryPercent { get => stopTheRecordingBelowBatteryPercent; set => stopTheRecordingBelowBatteryPercent = value; }

        /// <summary>The most every recording kept on a phone may take together, in megabytes; the phone's free space can make it less.</summary>
        public int AndroidRecordingsDiskBudgetMb { get => androidRecordingsDiskBudgetMb; set => androidRecordingsDiskBudgetMb = value; }

        /// <summary>Minutes of play after which a recording stops for good.</summary>
        public float MaxRecordingMinutes { get => maxRecordingMinutes; set => maxRecordingMinutes = value; }

        /// <summary>The most every recording kept on this machine may take together, in megabytes.</summary>
        public int RecordingsDiskBudgetMb { get => recordingsDiskBudgetMb; set => recordingsDiskBudgetMb = value; }

        /// <summary>Megabytes a recording's file stops before passing.</summary>
        public int MaxRecordingSizeMb { get => maxRecordingSizeMb; set => maxRecordingSizeMb = value; }

        /// <summary>With heavy analytics on, a frame that takes this many milliseconds or longer is counted as a hitch.</summary>
        public float HitchFrameTimeMs { get => hitchFrameTimeMs; set => hitchFrameTimeMs = value; }

        /// <summary>The project's settings, or null when the project has none (which reads as playtesting off).</summary>
        public static ProtokitePlaytestSettings Load() => Resources.Load<ProtokitePlaytestSettings>(ResourceName);
    }

    /// <summary>Draws a key setting with a Detect Key button that sets it from the next key pressed, beside the list of every key.</summary>
    internal sealed class ProtokitePlaytestKeyFieldAttribute : PropertyAttribute
    {
    }
}
