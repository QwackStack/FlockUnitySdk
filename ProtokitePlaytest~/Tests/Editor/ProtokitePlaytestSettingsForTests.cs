using System;
using System.IO;
using Protokite.Playtest.Editor;
using UnityEditor;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// Turns the project's playtest settings to a test's values, and puts every value back as it was when disposed. The player's
    /// consent answer is kept in a file of the test's own, never the project's, and asking is off unless the test turns it on.
    /// </summary>
    internal sealed class ProtokitePlaytestSettingsForTests : IDisposable
    {
        public const string ProtokiteApiUrl = "http://protokite.test/";

        private readonly bool _assetExisted;
        private readonly string _asItWas;
        private readonly string _consentFolder;

        public ProtokitePlaytestSettings Settings { get; }

        /// <summary>The file this test's consent answer is kept in.</summary>
        public string ConsentFilePath { get; }

        public ProtokitePlaytestSettingsForTests(bool playtestingEnabled = true, string protokiteApiUrl = ProtokiteApiUrl, bool askThePlayerForPlaytestConsent = false)
        {
            _assetExisted = AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath) != null;
            Settings = ProtokitePlaytestSettingsMenu.FindOrCreateSettings();
            _asItWas = EditorJsonUtility.ToJson(Settings);
            Settings.PlaytestingEnabled = playtestingEnabled;
            Settings.ProtokiteApiUrl = protokiteApiUrl;
            Settings.AskThePlayerForPlaytestConsent = askThePlayerForPlaytestConsent;

            _consentFolder = Path.Combine(Path.GetTempPath(), "protokite_consent_" + Guid.NewGuid().ToString("N"));
            ConsentFilePath = Path.Combine(_consentFolder, "playtest_consent.json");
            ProtokitePlaytest.ConsentFilePathForTesting = ConsentFilePath;
        }

        public void Dispose()
        {
            ProtokitePlaytest.ConsentFilePathForTesting = null;
            try
            {
                if (Directory.Exists(_consentFolder))
                    Directory.Delete(_consentFolder, true);
            }
            catch (IOException)
            {
            }

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
