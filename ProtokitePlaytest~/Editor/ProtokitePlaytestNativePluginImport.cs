using System;
using UnityEditor;

namespace Protokite.Playtest.Editor
{
    /// <summary>Keeps protokite_vpx.dll in the 64-bit Windows editor and players only, set in code and never by hand in the .meta.</summary>
    internal static class ProtokitePlaytestNativePluginImport
    {
        internal const string PluginFileName = "protokite_vpx.dll";

        private static readonly BuildTarget[] OtherPlayers =
        {
            BuildTarget.StandaloneWindows, BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64, BuildTarget.Android,
            BuildTarget.iOS, BuildTarget.WebGL
        };

        // Settings changed from an import rule do not stick to a native plugin (measured, 6000.3), so they are saved through
        // the importer once the editor has loaded, which also writes them into the .meta.
        [InitializeOnLoadMethod]
        private static void PutRightAfterLoading() => EditorApplication.delayCall += () => PutRightWhereWrong();

        /// <summary>Saves the right platforms on every copy of the DLL that lacks them; answers how many it changed.</summary>
        internal static int PutRightWhereWrong()
        {
            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets(System.IO.Path.GetFileNameWithoutExtension(PluginFileName)))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/" + PluginFileName, StringComparison.OrdinalIgnoreCase) || !(AssetImporter.GetAtPath(path) is PluginImporter plugin)
                    || HasTheRightPlatforms(plugin))
                    continue;
                SetTheRightPlatforms(plugin);
                plugin.SaveAndReimport();
                changed++;
            }
            return changed;
        }

        /// <summary>True when the plugin loads in the 64-bit Windows editor and 64-bit Windows players, and nowhere else.</summary>
        internal static bool HasTheRightPlatforms(PluginImporter plugin)
        {
            if (plugin.GetCompatibleWithAnyPlatform() || !plugin.GetCompatibleWithEditor()
                || plugin.GetEditorData("OS") != "Windows" || plugin.GetEditorData("CPU") != "x86_64"
                || !plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64)
                || plugin.GetPlatformData(BuildTarget.StandaloneWindows64, "CPU") != "x86_64")
                return false;
            foreach (BuildTarget target in OtherPlayers)
            {
                if (plugin.GetCompatibleWithPlatform(target))
                    return false;
            }
            return true;
        }

        private static void SetTheRightPlatforms(PluginImporter plugin)
        {
            plugin.SetCompatibleWithAnyPlatform(false);
            plugin.SetCompatibleWithEditor(true);
            plugin.SetEditorData("OS", "Windows");
            plugin.SetEditorData("CPU", "x86_64");
            plugin.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
            plugin.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            foreach (BuildTarget target in OtherPlayers)
                plugin.SetCompatibleWithPlatform(target, false);
        }
    }
}
