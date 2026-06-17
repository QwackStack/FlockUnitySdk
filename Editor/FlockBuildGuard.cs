using System;
using Flock.Config;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Flock.Editor
{
    /// <summary>
    /// Fails the player build when FlockConfig's Game Version ID is unusable — either not resolved
    /// (empty) or drifted from the generated schemas — so a build that would init with no version,
    /// or against the wrong one, can't ship. Opt-out per asset via
    /// <see cref="FlockConfigAsset.failBuildIfVersionUnresolved"/>.
    /// </summary>
    internal sealed class FlockBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            FlockConfigAsset config = Resources.Load<FlockConfigAsset>("FlockConfig");
            if (config == null) return; // no asset → SDK not in use; don't block

            string reason = GetBuildBlockReason(
                config.gameVersionId,
                FlockCodeGenValidator.GetGeneratedGameVersionId(),
                config.failBuildIfVersionUnresolved);

            if (reason != null)
                throw new BuildFailedException(reason);
        }

        // Pure decision: returns the build-blocking message, or null to allow the build.
        // generatedGameVersionId is null when codegen hasn't run (no manifest to compare against).
        internal static string GetBuildBlockReason(string bakedGameVersionId, string generatedGameVersionId, bool guardEnabled)
        {
            if (!guardEnabled) return null;

            if (string.IsNullOrEmpty(bakedGameVersionId))
                return "[Flock] Game Version ID is not resolved on FlockConfig. Open Qwacks > Editor while " +
                       "online to resolve your Game Version before building — or turn off " +
                       "'Fail build if Game Version unresolved' in Advanced Settings > Tools.";

            if (generatedGameVersionId != null &&
                !string.Equals(generatedGameVersionId, bakedGameVersionId, StringComparison.Ordinal))
                return $"[Flock] Game Version ID drift: FlockConfig is baked for '{bakedGameVersionId}' but the " +
                       $"generated schemas were synced for '{generatedGameVersionId}'. Re-run 'Flock > Sync Schemas' " +
                       "(and re-resolve in Qwacks > Editor if the version changed) so they match — or turn off " +
                       "'Fail build if Game Version unresolved' in Advanced Settings > Tools.";

            return null;
        }
    }
}
