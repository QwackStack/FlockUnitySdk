using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Http;
using Flock.Models;
using UnityEditor;

namespace Flock.Editor
{
    /// <summary>Resolves the Game Version name to its ID at edit time and bakes it onto the config, so runtime init needs no network. Separate from "Sync Schemas".</summary>
    internal static class FlockVersionResolver
    {
        // Maintainer wire detail (kept out of README / in-editor guide by convention).
        private const string ApiKeyHeader = "X-Flock-API-Key";

        internal readonly struct ResolveResult
        {
            public readonly bool Success;
            public readonly string GameVersionId;
            public readonly string Error;

            private ResolveResult(bool success, string id, string error)
            {
                Success = success;
                GameVersionId = id;
                Error = error;
            }

            public static ResolveResult Ok(string id) => new ResolveResult(true, id, null);
            public static ResolveResult Fail(string error) => new ResolveResult(false, null, error);
        }

        /// <summary>Backend by-name version lookup. Same request shape as the removed runtime resolve.</summary>
        internal static async Task<ResolveResult> ResolveAsync(
            string apiUrl, string apiKey, string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiUrl)) return ResolveResult.Fail("API URL is required.");
            if (string.IsNullOrWhiteSpace(apiKey)) return ResolveResult.Fail("API Key is required.");
            if (string.IsNullOrWhiteSpace(gameVersion)) return ResolveResult.Fail("Game Version is required.");

            string url = $"{apiUrl}/{FlockClient.ApiVersion}/game_version/by-name/{Uri.EscapeDataString(gameVersion)}";
            Dictionary<string, string> headers = new Dictionary<string, string> { { ApiKeyHeader, apiKey } };

            try
            {
                GenericResponse<GameVersionSchema> response =
                    await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(url, headers, ct);

                if (response?.Result == null || string.IsNullOrEmpty(response.Result.Id))
                    return ResolveResult.Fail(
                        $"Server returned no ID for Game Version '{gameVersion}'. Check that the name exists on the dashboard.");

                return ResolveResult.Ok(response.Result.Id);
            }
            catch (Exception ex)
            {
                return ResolveResult.Fail($"Could not resolve Game Version '{gameVersion}': {ex.Message}");
            }
        }

        /// <summary>Bakes a successful result onto the asset (leaves the prior ID on failure) and returns whether it changed. Pure — caller persists.</summary>
        internal static bool ApplyResult(FlockConfigAsset asset, ResolveResult result)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (!result.Success || string.IsNullOrEmpty(result.GameVersionId)) return false;
            if (string.Equals(asset.gameVersionId, result.GameVersionId, StringComparison.Ordinal)) return false;

            asset.gameVersionId = result.GameVersionId;
            return true;
        }

        /// <summary>Editor convenience: resolve → apply → persist → drift-check. Returns the result for UI.</summary>
        internal static async Task<ResolveResult> ResolveAndBakeAsync(FlockConfigAsset asset, CancellationToken ct = default)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            ResolveResult result = await ResolveAsync(asset.apiUrl, asset.apiKey, asset.gameVersion, ct);
            if (ApplyResult(asset, result))
            {
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }
            if (result.Success)
                FlockCodeGenValidator.WarnIfDrifted(result.GameVersionId);
            return result;
        }
    }
}
