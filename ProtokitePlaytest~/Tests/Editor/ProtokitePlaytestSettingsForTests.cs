using System;
using Protokite.Playtest.Editor;
using UnityEditor;

namespace Protokite.Playtest.Tests
{
    /// <summary>Turns the project's playtest settings to a test's values, and puts them back as they were when disposed.</summary>
    internal sealed class ProtokitePlaytestSettingsForTests : IDisposable
    {
        public const string ProtokiteApiUrl = "http://protokite.test/";

        private readonly bool _assetExisted;
        private readonly bool _wasEnabled;
        private readonly string _oldUrl;

        public ProtokitePlaytestSettings Settings { get; }

        public ProtokitePlaytestSettingsForTests(bool playtestingEnabled = true, string protokiteApiUrl = ProtokiteApiUrl)
        {
            _assetExisted = AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath) != null;
            Settings = ProtokitePlaytestSettingsMenu.FindOrCreateSettings();
            _wasEnabled = Settings.PlaytestingEnabled;
            _oldUrl = Settings.ProtokiteApiUrl;
            Settings.PlaytestingEnabled = playtestingEnabled;
            Settings.ProtokiteApiUrl = protokiteApiUrl;
        }

        public void Dispose()
        {
            if (_assetExisted)
            {
                Settings.PlaytestingEnabled = _wasEnabled;
                Settings.ProtokiteApiUrl = _oldUrl;
                AssetDatabase.SaveAssets();
            }
            else
            {
                AssetDatabase.DeleteAsset(ProtokitePlaytestSettings.AssetPath);
            }
        }
    }
}
