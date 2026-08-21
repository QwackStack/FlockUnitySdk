using UnityEditor;
using UnityEngine;

namespace Flock.Editor
{
    // Editor API shims so the package compiles across the whole supported Unity range.
    internal static class FlockEditorCompat
    {
        private static GUIStyle _linkLabel;

        /// <summary>Finds the first active object of type T, using whichever API the running editor has.</summary>
        internal static T FindFirstInScene<T>() where T : Object
        {
            // 2022.2, not 2021.3: FindAnyObjectByType landed in 2021.3.18f1 and there is no define for a patch.
#if UNITY_2022_2_OR_NEWER
            return Object.FindAnyObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        /// <summary>Link-styled label. Own style rather than EditorStyles.linkLabel, which is not public on older editors.</summary>
        internal static GUIStyle LinkLabel
        {
            get
            {
                if (_linkLabel == null)
                {
                    _linkLabel = new GUIStyle(EditorStyles.label);
                    _linkLabel.normal.textColor = new Color(0.30f, 0.56f, 0.93f);
                    _linkLabel.hover.textColor = new Color(0.45f, 0.68f, 1.00f);
                }
                return _linkLabel;
            }
        }
    }
}
