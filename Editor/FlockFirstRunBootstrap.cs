using UnityEditor;
using UnityEngine;

namespace Flock.Editor
{
    /// <summary>
    /// Pops the Qwacks/Flock editor window the first time the SDK is imported
    /// into a project. The "shown" flag is keyed by project path in EditorPrefs,
    /// so each project gets exactly one welcome regardless of how many times the
    /// SDK is reimported. Users who close the window won't see it auto-reopen.
    /// </summary>
    [InitializeOnLoad]
    internal static class FlockFirstRunBootstrap
    {
        private const string ShownKeyPrefix = "Flock.QwacksWindow.WelcomeShown:";

        static FlockFirstRunBootstrap()
        {
            string key = ShownKeyPrefix + Application.dataPath;
            if (EditorPrefs.GetBool(key, false))
                return;

            // Defer until after the import / domain reload has settled. Trying
            // to open a window inline from a static ctor races with Unity's
            // own startup and sometimes silently no-ops.
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool(key, false))
                    return;

                EditorPrefs.SetBool(key, true);
                QwacksEditorWindow.ShowWindow();
            };
        }
    }
}
