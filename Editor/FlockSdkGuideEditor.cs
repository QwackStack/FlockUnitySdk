using Flock.Docs;
using UnityEditor;
using UnityEngine;

namespace Flock.Editor
{
    /// <summary>
    /// Read-only inspector for <see cref="FlockSdkGuide"/>. Hides the default
    /// script field and field labels, and renders each section from the
    /// guide's constants as a styled card. Edits are intentionally not
    /// supported — the source of truth is FlockSdkGuide.cs.
    /// </summary>
    [CustomEditor(typeof(FlockSdkGuide))]
    public class FlockSdkGuideEditor : UnityEditor.Editor
    {
        private GUIStyle _title;
        private GUIStyle _subtitle;
        private GUIStyle _section;
        private GUIStyle _body;
        private GUIStyle _card;

        public override void OnInspectorGUI()
        {
            EnsureStyles();

            EditorGUILayout.Space(6);
            GUILayout.Label("Flock SDK Guide", _title);
            GUILayout.Label("Quick reference for initializing the SDK and using analytics.", _subtitle);
            EditorGUILayout.Space(8);

            DrawSection("SDK Initialization", FlockSdkGuide.Initialization);
            DrawSection("Init Parameters", FlockSdkGuide.InitParameters);
            DrawSection("Analytics", FlockSdkGuide.AnalyticsOverview);
            DrawSection("Analytics Parameters", FlockSdkGuide.AnalyticsParameters);
            DrawSection("Code Usage", FlockSdkGuide.CodeUsage);
            DrawSection("Exception Capturing", FlockSdkGuide.ExceptionCapturing);
        }

        private void DrawSection(string title, string body)
        {
            EditorGUILayout.BeginVertical(_card);
            GUILayout.Label(title, _section);
            GUILayout.Label(body, _body);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void EnsureStyles()
        {
            if (_section != null) return;

            _title = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 4, 0)
            };
            _subtitle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            _section = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin = new RectOffset(0, 0, 0, 6)
            };
            // Mono-ish font keeps the indented code samples readable. Falling
            // back to the default editor font if the built-in mono asset isn't
            // available on this Unity version.
            Font mono = EditorStyles.textField.font;
            _body = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = false,
                fontSize = 11,
                font = mono != null ? mono : EditorStyles.label.font
            };
            _card = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(14, 14, 12, 14),
                margin = new RectOffset(0, 0, 4, 4)
            };
        }
    }
}
