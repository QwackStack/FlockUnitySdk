using Flock.Config;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Protokite.Playtest.Editor
{
    /// <summary>Refuses a build with playtesting on whose Playtest ID was not resolved, or was resolved with other Flock settings: its players would join no playtest.</summary>
    internal sealed class ProtokitePlaytestBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            string why = WhyTheBuildIsRefused(ProtokitePlaytestSettings.Load(), ProtokitePlaytestSetupInput.LoadFlockSettings());
            if (why != null)
                throw new BuildFailedException(why);
        }

        /// <summary>Why a build with these settings is refused, or null; playtesting off, or no Playtest ID, is never refused here.</summary>
        internal static string WhyTheBuildIsRefused(ProtokitePlaytestSettings settings, FlockConfigAsset flock)
        {
            if (settings == null || !settings.PlaytestingEnabled || !settings.ChoosesAPlaytest)
                return null;
            const string fix = " Open Protokite > Playtest > Settings while Flock can be reached, so the editor resolves it and says what Flock found; or clear " +
                               "Playtest ID, or turn off Playtesting Enabled.";
            if (settings.PlaytestVersionId == null)
                return $"[Protokite Playtest] Playtest ID '{settings.PlaytestId}' has not been resolved to a playtest, so this build's players would join none." + fix;
            // With no Flock settings there is nothing to compare with, and no Flock SDK to start a playtest.
            if (flock != null && !ProtokitePlaytestIdLookup.IsResolvedWith(settings, flock))
                return $"[Protokite Playtest] Playtest ID '{settings.PlaytestId}' was resolved with other Flock settings (another API URL or key), whose versions " +
                       "this build's Flock does not have, so its players would join none." + fix;
            return null;
        }
    }
}
