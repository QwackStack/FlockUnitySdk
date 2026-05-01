using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Http;
using Flock.Models;
using UnityEngine;

namespace Flock.Editor.Codegen
{
    internal static class SchemaFetcher
    {
        public static async Task<FlockSchemaSnapshot> FetchAsync(FlockConfigAsset config)
        {
            string baseUrl = (config.apiUrl ?? "").TrimEnd('/');
            var bootstrapHeaders = new Dictionary<string, string>
            {
                { "X-Flock-API-Key", config.ApiKey }
            };

            string gameVersionId = await ResolveGameVersionIdAsync(baseUrl, config.gameVersion, bootstrapHeaders);
            if (string.IsNullOrEmpty(gameVersionId))
            {
                Debug.LogError($"[Flock Codegen] Could not resolve GameVersionId for game version '{config.gameVersion}'. Aborting sync.");
                return new FlockSchemaSnapshot { GameVersionId = "", FetchedAt = DateTime.UtcNow };
            }

            var headers = new Dictionary<string, string>(bootstrapHeaders)
            {
                ["X-Game-Version-ID"] = gameVersionId
            };

            var snapshot = new FlockSchemaSnapshot
            {
                GameVersionId = gameVersionId,
                FetchedAt = DateTime.UtcNow,
            };

            snapshot.PlayerTemplates = await GetList<PlayerTemplateSchema>($"{baseUrl}/v1/player_template", headers);
            snapshot.GameConfigs     = await GetList<GameConfigSchema>($"{baseUrl}/v1/game_config", headers);

            return snapshot;
        }

        private static async Task<string> ResolveGameVersionIdAsync(string baseUrl, string gameVersion, Dictionary<string, string> headers)
        {
            if (string.IsNullOrEmpty(gameVersion))
            {
                Debug.LogError("[Flock Codegen] Game Version is empty in FlockConfigAsset.");
                return null;
            }

            string url = $"{baseUrl}/v1/game_version/by-name/{Uri.EscapeDataString(gameVersion)}";
            try
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(url, headers);
                if (response?.Error != null && !string.IsNullOrEmpty(response.Error.Code))
                {
                    Debug.LogError($"[Flock Codegen] {url} returned error code '{response.Error.Code}'.");
                    return null;
                }
                return response?.Result?.Id;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock Codegen] GET {url} failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static async Task<List<T>> GetList<T>(string url, Dictionary<string, string> headers)
        {

            try
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<T>>>(url, headers);
                if (response?.Error != null && !string.IsNullOrEmpty(response.Error.Code))
                {
                    Debug.LogError($"[Flock Codegen] {url} returned error code '{response.Error.Code}'. Continuing with 0 items for this endpoint.");
                    return new List<T>();
                }
                return response?.Result ?? new List<T>();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock Codegen] GET {url} failed: {ex.GetType().Name}: {ex.Message}\nContinuing with 0 items for this endpoint.");
                return new List<T>();
            }
        }
    }
}
