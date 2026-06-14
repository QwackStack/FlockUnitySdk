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
            GUILayout.Label("Flock SDK — Getting Started", _title);
            GUILayout.Label("What Flock is, how to set it up, and where to find the full docs.", _subtitle);
            EditorGUILayout.Space(8);

            DrawSection("What is Flock?", FlockSdkGuide.Overview);
            DrawSection("Configuration", FlockSdkGuide.Configuration);
            DrawSection("Setup steps", FlockSdkGuide.Setup);
            DrawSection("Quick Start", FlockSdkGuide.Quickstart);
            DrawLinks();
        }

        private void DrawSection(string title, string body)
        {
            EditorGUILayout.BeginVertical(_card);
            GUILayout.Label(title, _section);
            GUILayout.Label(body, _body);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void DrawLinks()
        {
            EditorGUILayout.BeginVertical(_card);
            GUILayout.Label("Documentation & links", _section);
            DrawLinkButton("Open Full Documentation", FlockSdkGuide.DocsUrl);
            DrawLinkButton("Open Flock Dashboard", FlockSdkGuide.DashboardUrl);
            DrawLinkButton("Contact Support", FlockSdkGuide.SupportUrl);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        // Renders a link as a button. If the URL is still a placeholder (not a
        // real http link), the button is disabled and labelled so it is obvious
        // in the editor that it still needs filling in.
        private static void DrawLinkButton(string label, string url)
        {
            bool isSet = !string.IsNullOrEmpty(url) && url.StartsWith("http");
            using (new EditorGUI.DisabledScope(!isSet))
            {
                if (GUILayout.Button(isSet ? label : label + "  (link not set)"))
                    Application.OpenURL(url);
            }
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
