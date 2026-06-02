using System;
using System.Reflection;
using Flock.Logging;

namespace Flock.Config
{
    internal static class CodeGenValidator
    {
        private const string ManifestTypeName = "Flock.Generated.SchemasManifest";
        private const string GameVersionIdField = "GameVersionId";

        //version changed
        public static void WarnIfDrifted(string configuredGameVersionId, IFlockLogger logger)
        {
            if (logger == null || string.IsNullOrEmpty(configuredGameVersionId)) return;

            string generatedGameVersionId = TryReadGeneratedGameVersionId();
            if (generatedGameVersionId == null)
            {
                logger.LogDebug("No generated SchemasManifest found — run 'Flock > Sync Schemas' to generate typed accessors.");
                return;
            }

            if (!string.Equals(generatedGameVersionId, configuredGameVersionId))
            {
                logger.LogWarning(
                    $"Generated schemas were synced for game_version_id='{generatedGameVersionId}' " +
                    $"but the SDK is configured for '{configuredGameVersionId}'. " +
                    "Re-run 'Flock > Sync Schemas' to regenerate against the current version.");
            }
        }

        private static string TryReadGeneratedGameVersionId()
        {
            // Generated code lives wherever the user pointed FlockConfigAsset.generatedCodePath
            // (default Assets/Flock/Generated → Assembly-CSharp). We don't know the assembly
            // name up front, so scan loaded assemblies for the manifest type.
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = asm.GetType(ManifestTypeName, throwOnError: false);
                }
                catch
                {
                    continue;
                }
                if (type == null) continue;

                FieldInfo field = type.GetField(GameVersionIdField, BindingFlags.Public | BindingFlags.Static);
                return field?.GetRawConstantValue() as string;
            }
            return null;
        }
    }
}
