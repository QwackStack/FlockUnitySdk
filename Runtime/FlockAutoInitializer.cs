using System;
using System.Threading.Tasks;
using Flock.Config;
using UnityEngine;

namespace Flock
{
    /// <summary>
    /// Opt-in zero-touch startup: when <see cref="FlockConfigAsset.autoInitializeOnLoad"/> is ON,
    /// initializes the SDK before the first scene loads from <c>Assets/Resources/FlockConfig.asset</c>
    /// — no <see cref="FlockBootstrap"/> component or manual <see cref="FlockClient.Create"/> needed —
    /// then restores a persisted session in the background. Mirrors the auto-init model of
    /// Sentry / GameAnalytics; feasible now that init is synchronous.
    /// </summary>
    internal static class FlockAutoInitializer
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize()
        {
            if (FlockClient.IsInitialized) return; // already initialized (e.g. a manual startup hook)

            FlockConfigAsset config = Resources.Load<FlockConfigAsset>("FlockConfig");
            if (config == null || !config.autoInitializeOnLoad) return;

            if (!config.IsValid(out string validationError))
            {
                Debug.LogError(
                    $"[Flock] Auto-Initialize On Load is enabled but FlockConfig is incomplete: {validationError}. " +
                    "Open Qwacks > Editor to fix it, or turn off Auto-Initialize On Load.");
                return;
            }

            try
            {
                FlockClient.Create(config.ToInitConfig());
            }
            catch (Exception ex)
            {
                // Don't crash startup — surface clearly and leave the SDK uninitialized.
                Debug.LogError($"[Flock] Auto-initialize failed: {ex.Message}");
                return;
            }

            _ = RestoreSessionAsync();
        }

        // Resumes a persisted session in the background; result + in-flight state surface via
        // FlockEvents.OnSessionRestored and FlockClient.IsRestoringSession.
        private static async Task RestoreSessionAsync()
        {
            try
            {
                await FlockClient.Instance.Authentication.TryRestoreSessionAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock] Auto-initialize session restore failed: {ex.Message}");
            }
        }
    }
}
