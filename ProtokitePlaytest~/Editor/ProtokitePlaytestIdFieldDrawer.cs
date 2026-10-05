using Flock.Config;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Editor
{
    /// <summary>The Playtest ID, the version it resolved to, and what Flock said about it; resolved through Flock whenever it or the Flock settings change.</summary>
    [CustomPropertyDrawer(typeof(ProtokitePlaytestIdFieldAttribute))]
    internal sealed class ProtokitePlaytestIdFieldDrawer : PropertyDrawer
    {
        internal const string FieldLabel = "Playtest ID";
        internal const string ResolvedLabel = "Resolved Version ID";
        private const float Gap = 2f;
        private const float ButtonWidth = 110f;
        private static readonly GUIContent ResolveAgainButton = new GUIContent("Resolve Again", "Ask Flock about the Playtest ID again.");

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            string said = WhatToSay(property).Item1;
            float width = Mathf.Max(100f, EditorGUIUtility.currentViewWidth - 40f - ButtonWidth - Gap);
            float message = Mathf.Max(line, EditorStyles.helpBox.CalcHeight(new GUIContent(said), width));
            return line + Gap + line + Gap + message;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            ProtokitePlaytestSettings settings = property.serializedObject.targetObject as ProtokitePlaytestSettings;
            FlockConfigAsset flock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            // At Layout only, before what an answer changes is drawn; the field commits on Enter or on leaving it, not for every letter.
            if (Event.current.type == EventType.Layout)
                ProtokitePlaytestIdResolver.ResolveForAView(settings, flock);

            float line = EditorGUIUtility.singleLineHeight;
            Rect field = new Rect(position.x, position.y, position.width, line);
            GUIContent fieldLabel = new GUIContent(FieldLabel, label.tooltip);
            EditorGUI.BeginProperty(field, fieldLabel, property);
            property.stringValue = EditorGUI.DelayedTextField(field, fieldLabel, property.stringValue);
            EditorGUI.EndProperty();

            Rect resolved = new Rect(position.x, field.yMax + Gap, position.width, line);
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.TextField(resolved, new GUIContent(ResolvedLabel, "The playtest's own Flock version, found from the Playtest ID. Only the playtest's requests to Protokite carry it."),
                    settings != null ? settings.PlaytestVersionId ?? "" : "");

            (string said, MessageType kind) = WhatToSay(property);
            Rect message = new Rect(position.x, resolved.yMax + Gap, position.width - ButtonWidth - Gap, position.yMax - resolved.yMax - Gap);
            EditorGUI.HelpBox(message, said, kind);
            Rect button = new Rect(message.xMax + Gap, message.y, ButtonWidth, line);
            using (new EditorGUI.DisabledScope(ProtokitePlaytestIdResolver.IsAsking || !ProtokitePlaytestIdLookup.CanAsk(settings, flock)
                                               || !ProtokitePlaytestIdResolver.IsTheGamesSettings(settings)))
            {
                if (GUI.Button(button, ResolveAgainButton))
                    ProtokitePlaytestIdResolver.ResolveForAView(settings, flock, askAgain: true);
            }
        }

        private static (string, MessageType) WhatToSay(SerializedProperty property)
        {
            ProtokitePlaytestSettings settings = property.serializedObject.targetObject as ProtokitePlaytestSettings;
            FlockConfigAsset flock = ProtokitePlaytestSetupInput.LoadFlockSettings();
            ProtokitePlaytestIdAnswer answer = ProtokitePlaytestIdResolver.Answer;
            bool answerIsForThese = answer != null && answer.AskedFor == ProtokitePlaytestIdLookup.KeyFor(settings, flock);
            return WhatToSay(settings, flock, ProtokitePlaytestIdResolver.IsAsking, answerIsForThese ? answer : null,
                ProtokitePlaytestIdResolver.IsTheGamesSettings(settings));
        }

        /// <summary>The line under the Playtest ID, and how loudly to say it, from the settings and Flock's answer for them (null when none yet).</summary>
        internal static (string, MessageType) WhatToSay(ProtokitePlaytestSettings settings, FlockConfigAsset flock, bool asking, ProtokitePlaytestIdAnswer answer,
            bool theGamesSettings)
        {
            if (settings == null || !settings.ChoosesAPlaytest)
                return ("Empty: a build joins the game's newest playtest in Protokite, whichever is newest when it starts (or, when the Flock SDK's Game Version is " +
                        "a playtest's own name, pt-<test id>, that one). Paste the ID from a playtest's page in Protokite to choose it.", MessageType.Info);
            if (!theGamesSettings)
                return ($"These are not the playtest settings a build loads ({ProtokitePlaytestSettings.AssetPath}, or another asset of that name in a Resources " +
                        "folder), so this Playtest ID is not resolved here.", MessageType.Warning);

            string versionId = settings.PlaytestVersionId;
            bool resolvedWithThese = ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock);
            string before = versionId == null ? " Until it is resolved, playtesting stays off."
                : resolvedWithThese ? $" A build asks for the version it resolved to before, {versionId}."
                : $" It was resolved to {versionId} with other Flock settings (another API URL or key), so a build with playtesting on is refused until it is resolved with these.";
            if (!ProtokitePlaytestIdLookup.CanAsk(settings, flock))
                return ("Set the API URL and API key in Flock > Settings: the Playtest ID is resolved through Flock." + before, MessageType.Warning);
            if (asking)
                return ("Checking with Flock..." + before, MessageType.Info);
            if (answer == null)
                return (versionId != null && resolvedWithThese ? $"Resolved to {versionId}; not checked with Flock again yet." : "Not resolved yet." + before,
                    MessageType.Info);
            if (answer.Problem != null)
                return ("Could not check with Flock: " + answer.Problem + before, MessageType.Warning);
            if (answer.VersionId == null)
                return (answer.WhyNoPlaytest + " Until it is resolved, playtesting stays off.", MessageType.Error);
            string gameVersion = flock != null && !string.IsNullOrWhiteSpace(flock.gameVersion) ? $"Game Version '{flock.gameVersion}'" : "its own Game Version";
            return ($"This build joins the playtest whose version is {answer.VersionName} ({answer.VersionId}). Only the playtest's requests to Protokite carry it; " +
                    $"the Flock SDK keeps {gameVersion} and everything set up under it.", MessageType.Info);
        }
    }
}
