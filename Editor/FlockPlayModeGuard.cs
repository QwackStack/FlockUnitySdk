using UnityEditor;
using UnityEngine;
using Flock.Config;

namespace Flock.Editor
{
    /// Intercepts Play-enter when the Flock SDK isn't set up and offers a one-click fix.
    /// Editor-only. See Documentation~/specs/2026-06-16-play-mode-setup-guard-design.md.
    [InitializeOnLoad]
    internal static class FlockPlayModeGuard
    {
        private const string SessionSuppressKey = "Flock.PlayModeGuard.SuppressThisSession";

        static FlockPlayModeGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Only act on the transition into play mode, and never in headless/CI (no dialogs).
            if (change != PlayModeStateChange.ExitingEditMode || Application.isBatchMode)
                return;

            FlockSetupVerdict verdict = FlockSetupClassifier.Classify(GatherState());

            if (verdict == FlockSetupVerdict.Ok)
                return;

            if (verdict == FlockSetupVerdict.Warn)
            {
                Debug.LogWarning(
                    "[Flock] Auto-Initialize On Load is off and no FlockBootstrap is in the open scene(s), so the SDK " +
                    "won't initialize. If you call FlockClient.Create() from your own startup code that's fine — otherwise " +
                    "open Flock > Settings and click 'Add to Scene', or turn Auto-Initialize On Load back on.");
                return;
            }

            // Block.
            if (SessionState.GetBool(SessionSuppressKey, false))
                return;

            ShowBlockDialog();
        }

        private static FlockSetupState GatherState()
        {
            FlockConfigAsset config = FlockConfigLocator.FindConfigAsset();
            bool configExists = config != null;
            bool configValid = configExists && config.IsValid(out string _);
            // No asset → guard stays active by default (there's no asset to hold the toggle).
            bool guardEnabled = !configExists || config.playModeGuardEnabled;
            bool autoInitializeEnabled = configExists && config.autoInitializeOnLoad;
            bool bootstrapPresent = UnityEngine.Object.FindAnyObjectByType<FlockBootstrap>() != null;

            return new FlockSetupState(configExists, configValid, guardEnabled, autoInitializeEnabled, bootstrapPresent);
        }

        private static void ShowBlockDialog()
        {
            // Three buttons so Escape / window-close lands on the SAFE option (cancel play),
            // never silently entering a broken play session.
            int choice = EditorUtility.DisplayDialogComplex(
                "Flock isn't set up",
                "Flock can't initialize with the current setup, so your game will hit a Flock error at runtime.\n\n" +
                "Open the setup window to fix it, cancel, or play anyway for this session.",
                "Open Setup",    // 0
                "Cancel",        // 1  (also Escape / window close)
                "Play Anyway");  // 2

            switch (choice)
            {
                case 0:
                    EditorApplication.isPlaying = false;
                    QwacksEditorWindow.ShowWindow();
                    break;
                case 2:
                    // Don't nag again until the editor restarts.
                    SessionState.SetBool(SessionSuppressKey, true);
                    break;
                default:
                    EditorApplication.isPlaying = false;
                    break;
            }
        }
    }
}
