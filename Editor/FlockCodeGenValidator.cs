using System;
using System.Reflection;
using UnityEngine;

namespace Flock.Editor
{
    /// <summary>Reads the generated <c>SchemasManifest</c> Game Version ID and warns (editor-only) when the baked ID has drifted from it.</summary>
    internal static class FlockCodeGenValidator
    {
        private const string ManifestTypeName = "Flock.Generated.SchemasManifest";
        private const string GameVersionIdField = "GameVersionId";

        public static void WarnIfDrifted(string bakedGameVersionId)
        {
            if (string.IsNullOrEmpty(bakedGameVersionId)) return;

            string generated = GetGeneratedGameVersionId();
            if (generated == null) return; // no generated manifest yet — nothing to compare

            if (!string.Equals(generated, bakedGameVersionId))
                Debug.LogWarning(
                    $"[Flock] Generated schemas were synced for game_version_id='{generated}' but the " +
                    $"baked Game Version ID is '{bakedGameVersionId}'. Run 'Flock > Sync Schemas' to regenerate.");
        }

        /// <summary>The Game Version ID the generated <c>SchemasManifest</c> was synced for, or null if codegen hasn't run.</summary>
        internal static string GetGeneratedGameVersionId()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = asm.GetType(ManifestTypeName, throwOnError: false); }
                catch { continue; }
                if (type == null) continue;

                FieldInfo field = type.GetField(GameVersionIdField, BindingFlags.Public | BindingFlags.Static);
                return field?.GetRawConstantValue() as string;
            }
            return null;
        }
    }
}
