using System;
using Protokite.Playtest.Editor;
using UnityEditor;

namespace Protokite.Playtest.Tests
{
    /// <summary>Turns the project's playtest settings to a test's values, and puts every value back as it was when disposed.</summary>
    internal sealed class ProtokitePlaytestSettingsForTests : IDisposable
    {
        public const string ProtokiteApiUrl = "http://protokite.test/";

        private readonly bool _assetExisted;
        private readonly string _asItWas;

        public ProtokitePlaytestSettings Settings { get; }

        public ProtokitePlaytestSettingsForTests(bool playtestingEnabled = true, string protokiteApiUrl = ProtokiteApiUrl)
        {
            _assetExisted = AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath) != null;
            Settings = ProtokitePlaytestSettingsMenu.FindOrCreateSettings();
            _asItWas = EditorJsonUtility.ToJson(Settings);
            Settings.PlaytestingEnabled = playtestingEnabled;
            Settings.ProtokiteApiUrl = protokiteApiUrl;
        }

        public void Dispose()
        {
            if (_assetExisted)
            {
                EditorJsonUtility.FromJsonOverwrite(_asItWas, Settings);
                EditorUtility.SetDirty(Settings);
                AssetDatabase.SaveAssets();
            }
            else
            {
                AssetDatabase.DeleteAsset(ProtokitePlaytestSettings.AssetPath);
            }
        }
    }
}
