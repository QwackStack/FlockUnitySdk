using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Tests
{
    /// <summary>A test run in an open editor leaves the project's playtest settings as the developer had them: nothing saved, nothing lost.</summary>
    public class ProtokitePlaytestProjectSettingsKeptTests
    {
        private ProtokitePlaytestSettings _project;
        private byte[] _fileBefore;
        private float _hitchBefore;
        private bool _unsavedBefore;

        [SetUp]
        public void SetUp()
        {
            _project = AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath);
            if (_project == null)
                Assert.Ignore("This project has no playtest settings of its own to keep.");
            _fileBefore = File.ReadAllBytes(ProtokitePlaytestSettings.AssetPath);
            _hitchBefore = _project.HitchFrameTimeMs;
            _unsavedBefore = EditorUtility.IsDirty(_project);
        }

        [TearDown]
        public void TearDown()
        {
            if (_project == null)
                return;
            _project.HitchFrameTimeMs = _hitchBefore;
            if (!_unsavedBefore)
                EditorUtility.ClearDirty(_project);
        }

        [Test]
        public void AnUnsavedEditIsNeitherSavedNorLost()
        {
            // A developer's edit they have not saved, as they would make it in the inspector.
            _project.HitchFrameTimeMs = _hitchBefore + 17f;
            EditorUtility.SetDirty(_project);
            bool enabledBefore = _project.PlaytestingEnabled;

            using (new ProtokitePlaytestSettingsForTests(playtestingEnabled: !enabledBefore))
                Assert.AreEqual(!enabledBefore, _project.PlaytestingEnabled, "Precondition: the test's value is in force");

            CollectionAssert.AreEqual(_fileBefore, File.ReadAllBytes(ProtokitePlaytestSettings.AssetPath), "The file is as it was: neither the test's values nor the unsaved edit are saved");
            Assert.AreEqual(_hitchBefore + 17f, _project.HitchFrameTimeMs, "The unsaved edit is still there");
            Assert.AreEqual(enabledBefore, _project.PlaytestingEnabled, "The test's value is gone");
            Assert.IsTrue(EditorUtility.IsDirty(_project), "The edit is still unsaved, for the developer to save or not");
        }

        [Test]
        public void AFileSavedDuringATestIsPutBack()
        {
            KeyCode keyBefore = _project.FeedbackFormKey;
            using (ProtokitePlaytestSettingsForTests test = new ProtokitePlaytestSettingsForTests())
            {
                test.Settings.FeedbackFormKey = test.Settings.FeedbackFormKey == KeyCode.Q ? KeyCode.W : KeyCode.Q;
                // What code under test that saves does, to this asset alone.
                EditorUtility.SetDirty(test.Settings);
                AssetDatabase.SaveAssetIfDirty(test.Settings);
                CollectionAssert.AreNotEqual(_fileBefore, File.ReadAllBytes(ProtokitePlaytestSettings.AssetPath), "Precondition: the test's values reached the file");
            }

            CollectionAssert.AreEqual(_fileBefore, File.ReadAllBytes(ProtokitePlaytestSettings.AssetPath), "The file is put back as it was");
            Assert.AreEqual(keyBefore, _project.FeedbackFormKey, "And so is what the editor holds");
        }
    }
}
