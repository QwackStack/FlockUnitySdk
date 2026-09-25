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

        /// <summary>The project's settings, or null when the project has none (which reads as playtesting off).</summary>
        public static ProtokitePlaytestSettings Load() => Resources.Load<ProtokitePlaytestSettings>(ResourceName);
    }
}
