using System;
using System.IO;
using System.Linq;
using Flock.Config;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Tests
{
    /// <summary>Sets the project's playtest settings to a test's values, with answer files of the test's own, and puts them back when disposed, saving nothing.</summary>
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

        /// <summary>The file this test's answer about upload networks is kept in, beside the consent answer's.</summary>
        public string UploadNetworkFilePath { get; }

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
            UploadNetworkFilePath = Path.Combine(_consentFolder, "playtest_upload_network.json");
            ProtokitePlaytest.UploadNetworkFilePathForTesting = UploadNetworkFilePath;
        }

        // Nothing is saved: a save writes every unsaved asset in the project, a developer's edits included.
        public void Dispose()
        {
            ProtokitePlaytest.ConsentFilePathForTesting = null;
            ProtokitePlaytest.UploadNetworkFilePathForTesting = null;
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

    /// <summary>The project's own Flock settings, as a build and the setup window read them; made for the test, and deleted after, when the project has none.</summary>
    // A project made only to run the tests, as CI's is, has no Flock settings of its own.
    internal sealed class ProjectFlockSettingsForTests : IDisposable
    {
        private const string MadeFolder = "Assets/ProtokitePlaytestFlockSettingsForTests";
        private readonly bool _made;

        public FlockConfigAsset Settings { get; }

        public ProjectFlockSettingsForTests()
        {
            Settings = ProtokitePlaytestSetupInput.LoadFlockSettings();
            if (Settings != null)
            {
                // Settings a run that was stopped mid-test made are still this helper's to delete.
                _made = AssetDatabase.GetAssetPath(Settings).StartsWith(MadeFolder + "/", StringComparison.Ordinal);
                return;
            }
            string path = MadeFolder + "/Resources/" + ProtokitePlaytestSetupInput.FlockSettingsResourceName + ".asset";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // An API URL and key, as a Playtest ID is resolved only with both.
            FlockConfigAsset made = ScriptableObject.CreateInstance<FlockConfigAsset>();
            made.apiUrl = "https://flock.example.com";
            made.apiKey = "test-key";
            AssetDatabase.CreateAsset(made, path);
            _made = true;
            Settings = ProtokitePlaytestSetupInput.LoadFlockSettings();
            if (Settings != null)
                return;
            AssetDatabase.DeleteAsset(MadeFolder);
            throw new InvalidOperationException("The Flock settings made for the test do not load from Resources.");
        }

        public void Dispose()
        {
            if (_made)
                AssetDatabase.DeleteAsset(MadeFolder);
        }
    }
}
