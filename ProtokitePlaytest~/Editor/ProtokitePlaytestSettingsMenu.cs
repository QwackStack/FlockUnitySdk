using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Editor
{
    /// <summary>Protokite > Playtest > Settings: opens the project's playtest settings, creating them the first time.</summary>
    public static class ProtokitePlaytestSettingsMenu
    {
        /// <summary>The menu path, also used by the Flock settings window's Playtesting tab.</summary>
        public const string MenuPath = "Protokite/Playtest/Settings";

        [MenuItem(MenuPath)]
        public static void OpenSettings()
        {
            ProtokitePlaytestSettings settings;
            try
            {
                settings = FindOrCreateSettings();
            }
            catch (InvalidOperationException e)
            {
                EditorUtility.DisplayDialog("Protokite Playtest", e.Message, "OK");
                return;
            }
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        /// <summary>Forgets the answer this machine gave the consent question, so the next Play asks again; the setup window's Forget This Machine's Answer button.</summary>
        public static void ForgetThePlayersConsentAnswer()
        {
            if (ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.NotAnswered))
                Debug.Log("[Protokite Playtest] The answer this machine gave the playtest's consent question is forgotten; the question is put again the next time the playtest loads.");
            else
                Debug.LogWarning("[Protokite Playtest] The answer this machine gave the playtest's consent question could not be removed; see the warning before this one.");
        }

        /// <summary>In Play Mode, opens the feedback form the way its key does; the setup window's Open Feedback Form button.</summary>
        public static void OpenTheFeedbackForm()
        {
            if (!ProtokitePlaytest.OpenFeedbackForm())
                Debug.Log("[Protokite Playtest] The feedback form did not open: it opens in Play Mode, once this build's playtest is loaded and publishes a form, and not while the consent question is on screen. " + ProtokitePlaytest.Status);
        }

        /// <summary>The project's settings asset, created at <see cref="ProtokitePlaytestSettings.AssetPath"/> with playtesting off if there is none.</summary>
        public static ProtokitePlaytestSettings FindOrCreateSettings()
        {
            ProtokitePlaytestSettings existing = AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(ProtokitePlaytestSettings.AssetPath);
            if (existing != null)
                return existing;

            // A project that moved the asset elsewhere under a Resources folder keeps using it.
            string[] found = AssetDatabase.FindAssets("t:" + nameof(ProtokitePlaytestSettings));
            foreach (string guid in found)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == ProtokitePlaytestSettings.ResourceName && path.Contains("/Resources/"))
                    return AssetDatabase.LoadAssetAtPath<ProtokitePlaytestSettings>(path);
            }

            ProtokitePlaytestSettings created = ScriptableObject.CreateInstance<ProtokitePlaytestSettings>();
            // Saved without its script, the asset would load as nothing and playtesting would read as off with no reason given.
            if (MonoScript.FromScriptableObject(created) == null)
            {
                Object.DestroyImmediate(created);
                throw new InvalidOperationException(
                    "Unity cannot find the playtest settings script, so the settings were not created. This happens when the project's " +
                    "folder path contains a '~' (for example a shortened Windows path such as C:\\Users\\ADMINI~1). Open the project " +
                    "from its full path and try again.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ProtokitePlaytestSettings.AssetPath));
            AssetDatabase.CreateAsset(created, ProtokitePlaytestSettings.AssetPath);
            // This asset only: saving every asset would write the developer's other unsaved edits too.
            AssetDatabase.SaveAssetIfDirty(created);
            return created;
        }
    }
}
