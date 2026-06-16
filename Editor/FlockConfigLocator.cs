using UnityEditor;
using Flock.Config;

namespace Flock.Editor
{
    /// Single source of truth for "which FlockConfig asset" — used by the editor
    /// window and the play-mode guard so they can never disagree.
    internal static class FlockConfigLocator
    {
        private const string DefaultConfigPath = "Assets/Resources/FlockConfig.asset";

        /// The asset at the default path, else the first one anywhere in the project, else null.
        public static FlockConfigAsset FindConfigAsset()
        {
            FlockConfigAsset config = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(DefaultConfigPath);
            if (config != null)
                return config;

            string[] guids = AssetDatabase.FindAssets("t:FlockConfigAsset");
            if (guids != null && guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));

            return null;
        }
    }
}
