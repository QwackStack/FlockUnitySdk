using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Flock.Config;

namespace Flock.Editor.Codegen
{
    internal static class FlockCodegenMenu
    {
        private const string DefaultGeneratedPath = "Assets/Flock/Generated";
        private const string TemplatesSubdir = "Player";
        private const string ConfigsSubdir = "Configs";
        private const string CommandsSubdir = "Commands";
        private const string ShopsSubdir = "Shops";

        internal static async void SyncSchemas()
        {
#if FLOCK_NO_SCHEMA
            // Schema provider is excluded from this SDK build, so codegen has nothing to drive.
            // Defines flow in via Editor/csc.rsp written by the Package Builder when SCHEMA is
            // deselected. Await a completed task to keep the async signature warning-free.
            Debug.Log("[Flock Codegen] Schema provider is excluded from this SDK build — codegen does nothing.");
            await System.Threading.Tasks.Task.CompletedTask;
#else
            if (!TryLoadConfig(out var config, out var error))
            {
                Debug.LogError($"[Flock Codegen] {error}");
                return;
            }

            if (!TryResolveGeneratedPath(config.generatedCodePath, out string generatedRoot, out string pathError))
            {
                Debug.LogError($"[Flock Codegen] {pathError}");
                return;
            }

            Debug.Log(
                "[Flock Codegen] Sync starting\n" +
                $"  ApiUrl:      {config.apiUrl}\n" +
                $"  GameId:      {config.gameId}\n" +
                $"  GameVersion: {config.gameVersion}\n" +
                $"  Output:      {generatedRoot}");

            try
            {
                CodegenResult result = await RunCodegenAsync(config, generatedRoot);
                Debug.Log(
                    "[Flock Codegen] Sync complete\n" +
                    $"  GameVersionId:    {result.Snapshot.GameVersionId}\n" +
                    $"  Templates:        {result.TemplateCount}\n" +
                    $"  Player accessors: {result.PlayerAccessorCount}\n" +
                    $"  Command methods:  {result.CommandMethodCount}\n" +
                    $"  Configs:          {result.ConfigCount}\n" +
                    $"  Config accessors: {result.ConfigAccessorCount}\n" +
                    $"  Output:           {generatedRoot}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock Codegen] Sync failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
#endif
        }

        // Shared fetch + emit core used by the editor button and the CI entry points.
        // SchemaFetcher throws on fetch failure, so a failed sync never reaches the emitters.
        internal static async Task<CodegenResult> RunCodegenAsync(FlockConfigAsset config, string generatedRoot)
        {
            FlockSchemaSnapshot snapshot = await SchemaFetcher.FetchAsync(config);

            Debug.Log(
                "[Flock Codegen] Snapshot fetched\n" +
                $"  PlayerTemplates: {snapshot.PlayerTemplates.Count}\n" +
                $"  GameConfigs:     {snapshot.GameConfigs.Count}\n" +
                $"  Shops:           {snapshot.Shops.Count}");

            if (!Directory.Exists(generatedRoot))
                Directory.CreateDirectory(generatedRoot);

            PlayerTemplateEmitter.EmitResult templateResult = PlayerTemplateEmitter.Emit(
                snapshot.PlayerTemplates, Path.Combine(generatedRoot, TemplatesSubdir));
            // PlayerAccessorEmitter must run after PlayerTemplateEmitter wipes the dir.
            int playerAccessors = PlayerAccessorEmitter.Emit(
                snapshot.PlayerTemplates, templateResult.ClassNamesById, Path.Combine(generatedRoot, TemplatesSubdir));
            int commandAccessors = CommandAccessorEmitter.Emit(
                snapshot.PlayerTemplates, templateResult.ClassNamesById, Path.Combine(generatedRoot, CommandsSubdir));
            GameConfigEmitter.EmitResult configResult = GameConfigEmitter.Emit(
                snapshot.GameConfigs, Path.Combine(generatedRoot, ConfigsSubdir));
            int configAccessors = ConfigAccessorEmitter.Emit(
                snapshot.GameConfigs, configResult.ClassNamesById, Path.Combine(generatedRoot, ConfigsSubdir));
            ShopEmitter.Emit(
                snapshot.Shops, snapshot.PlayerTemplates, Path.Combine(generatedRoot, ShopsSubdir));
            ManifestEmitter.Emit(snapshot, generatedRoot);

            // Skip the import in batch mode: the files are already on disk for CI to commit, and a
            // script-recompile domain reload here could preempt the editor exit and hang the run.
            if (!Application.isBatchMode)
                AssetDatabase.Refresh();
            return new CodegenResult(snapshot, templateResult.Count, playerAccessors, commandAccessors, configResult.Count, configAccessors);
        }

        internal readonly struct CodegenResult
        {
            public readonly FlockSchemaSnapshot Snapshot;
            public readonly int TemplateCount;
            public readonly int PlayerAccessorCount;
            public readonly int CommandMethodCount;
            public readonly int ConfigCount;
            public readonly int ConfigAccessorCount;

            public CodegenResult(FlockSchemaSnapshot snapshot, int templateCount, int playerAccessorCount,
                int commandMethodCount, int configCount, int configAccessorCount)
            {
                Snapshot = snapshot;
                TemplateCount = templateCount;
                PlayerAccessorCount = playerAccessorCount;
                CommandMethodCount = commandMethodCount;
                ConfigCount = configCount;
                ConfigAccessorCount = configAccessorCount;
            }
        }

        internal static void CleanGenerated()
        {
            // Clean falls back to the default path so it still works
            // in fresh projects with no config asset.
            string configuredPath = TryLoadConfig(out var config, out _) ? config.generatedCodePath : null;
            if (!TryResolveGeneratedPath(configuredPath, out string generatedFolder, out string pathError))
            {
                Debug.LogError($"[Flock Codegen] Clean aborted: {pathError}");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Flock — Clean Generated",
                $"This deletes every generated .cs file under:\n\n{generatedFolder}\n\nContinue?",
                "Delete", "Cancel");
            if (!confirmed) return;

            int removed = 0;
            if (AssetDatabase.IsValidFolder(generatedFolder))
            {
                if (AssetDatabase.DeleteAsset(generatedFolder))
                {
                    Debug.Log($"[Flock Codegen] Deleted {generatedFolder}");
                    removed++;
                }
                else
                {
                    Debug.LogWarning($"[Flock Codegen] AssetDatabase.DeleteAsset failed for {generatedFolder}; falling back to filesystem delete.");
                    try
                    {
                        if (Directory.Exists(generatedFolder))
                            Directory.Delete(generatedFolder, recursive: true);
                        string metaPath = generatedFolder + ".meta";
                        if (File.Exists(metaPath))
                            File.Delete(metaPath);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Flock Codegen] Filesystem delete also failed: {ex.Message}");
                    }
                }
            }
            else
            {
                Debug.Log($"[Flock Codegen] No generated folder at {generatedFolder} — nothing to delete.");
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Flock Codegen] Clean complete ({removed} item(s) removed).");
        }

        internal static bool TryResolveGeneratedPath(string configured, out string resolved, out string error)
        {
            resolved = null;
            string raw = string.IsNullOrWhiteSpace(configured) ? DefaultGeneratedPath : configured.Trim();
            string normalized = raw.Replace('\\', '/').TrimEnd('/');

            if (!normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Generated Code Path must start with 'Assets/'. Got: '{raw}'.";
                return false;
            }

            resolved = normalized;
            error = null;
            return true;
        }

        internal static bool TryLoadConfig(out FlockConfigAsset config, out string error)
        {
            config = null;
            string[] guids = AssetDatabase.FindAssets("t:FlockConfigAsset");
            if (guids == null || guids.Length == 0)
            {
                error = "No FlockConfigAsset found. Open Qwacks > Flock and save a configuration first.";
                return false;
            }

            if (guids.Length > 1)
                Debug.LogWarning($"[Flock Codegen] Found {guids.Length} FlockConfigAsset instances; using the first.");

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            config = AssetDatabase.LoadAssetAtPath<FlockConfigAsset>(path);
            if (config == null)
            {
                error = $"Failed to load FlockConfigAsset at {path}.";
                return false;
            }

            return config.IsValid(out error);
        }
    }
}
