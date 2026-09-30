using System;
using System.IO;
using System.Linq;
using Protokite.Playtest.Editor;
using UnityEditor;

namespace Protokite.Playtest.Tests
{
    /// <summary>Sets the project's playtest settings to a test's values, with a consent file of the test's own, and puts them back when disposed, saving nothing.</summary>
    internal sealed class ProtokitePlaytestSettingsForTests : IDisposable
    {
        public const string ProtokiteApiUrl = "http://protokite.test/";

        private readonly bool _assetExisted;
        private readonly string _asItWas;
        private readonly string _assetPath;
        private readonly byte[] _fileAsItWas;
        private readonly bool _hadUnsavedEdits;
        private readonly string _consentFolder;

        public ProtokitePlaytestSettings Settings { get; }

        /// <summary>The file this test's consent answer is kept in.</summary>
        public string ConsentFilePath { get; }

        public ProtokitePlaytestSettingsForTests(bool playtestingEnabled = true, string protokiteApiUrl = ProtokiteApiUrl, bool askThePlayerForPlaytestConsent = false)
        {
            // Settings a project moved elsewhere under Resources are its own too, so existence is read wherever they are.
            string[] settingsBefore = AssetDatabase.FindAssets("t:" + nameof(ProtokitePlaytestSettings));
            Settings = ProtokitePlaytestSettingsMenu.FindOrCreateSettings();
            _assetPath = AssetDatabase.GetAssetPath(Settings);
            _assetExisted = settingsBefore.Contains(AssetDatabase.AssetPathToGUID(_assetPath));
            _fileAsItWas = _assetExisted ? File.ReadAllBytes(_assetPath) : null;
            // A developer's unsaved edit is theirs to save or not, so it is put back unsaved.
            _hadUnsavedEdits = EditorUtility.IsDirty(Settings);
            _asItWas = EditorJsonUtility.ToJson(Settings);
            Settings.PlaytestingEnabled = playtestingEnabled;
            Settings.ProtokiteApiUrl = protokiteApiUrl;
            Settings.AskThePlayerForPlaytestConsent = askThePlayerForPlaytestConsent;

            _consentFolder = Path.Combine(Path.GetTempPath(), "protokite_consent_" + Guid.NewGuid().ToString("N"));
            ConsentFilePath = Path.Combine(_consentFolder, "playtest_consent.json");
            ProtokitePlaytest.ConsentFilePathForTesting = ConsentFilePath;
        }

        // Nothing is saved: a save writes every unsaved asset in the project, a developer's edits included.
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

            if (!_assetExisted)
            {
                AssetDatabase.DeleteAsset(_assetPath);
                return;
            }
            // Code under test that saved wrote the test's values to the file, so the file is put back as it was.
            if (!File.ReadAllBytes(_assetPath).SequenceEqual(_fileAsItWas))
            {
                File.WriteAllBytes(_assetPath, _fileAsItWas);
                AssetDatabase.ImportAsset(_assetPath, ImportAssetOptions.ForceUpdate);
            }
            EditorJsonUtility.FromJsonOverwrite(_asItWas, Settings);
            if (_hadUnsavedEdits)
                EditorUtility.SetDirty(Settings);
            else
                EditorUtility.ClearDirty(Settings);
        }
    }
}
