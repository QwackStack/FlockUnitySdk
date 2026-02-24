using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Flock.Models;
using Flock.Http;
using Flock.Interfaces;

namespace Flock.Config
{
    public class FlockConfigProvider : FlockProviderBase, IConfigProvider
    {
        public FlockConfigProvider(FlockClient client) : base(client) { }

        public async Task<List<GameConfigSchema>> GetAllConfigsAsync(string tag = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl()).Append("/v1/game_config");

                if (!string.IsNullOrEmpty(tag))
                    url.Append("?tag=").Append(tag);

                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(url.ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);

                var configs = response.Result;
                await ApplyPatchesToConfigsAsync(configs, cancellationToken);
                return configs;
            }, "Fetch game configs", cancellationToken);
        }

        public async Task<List<GameConfigSchema>> GetConfigsByVersionAsync(string tag = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async () =>
            {
                var url = new StringBuilder().Append(Client.GetApiUrl()).Append("/v1/game_config/version");

                if (!string.IsNullOrEmpty(tag))
                    url.Append("?tag=").Append(tag);
                
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GameConfigSchema>>>(url.ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                var configs = response.Result;
                await ApplyPatchesToConfigsAsync(configs, cancellationToken);
                return configs;
            }, "Fetch game configs by version", cancellationToken);
        }

        public async Task<GameConfigSchema> GetConfigByIdAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");
            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<GameConfigSchema>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_config/")
                        .Append(configId)
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);

                var config = response.Result;
                await ApplyPatchToConfigAsync(config, cancellationToken);
                return config;
            }, new StringBuilder().Append("Fetch config ").Append(configId).ToString(), cancellationToken);
        }

        public async Task<List<GamePatchSchema>> GetConfigPatchesAsync(string configId, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(configId, "Config ID");

            return await ExecuteAsync(async () =>
            {
                var response = await FlockHttpClient.GetAsync<GenericResponse<List<GamePatchSchema>>>(
                    new StringBuilder().Append(Client.GetApiUrl())
                        .Append("/v1/game_config/")
                        .Append(configId)
                        .Append("/patches")
                        .ToString(), Client.GetBaseHeaders(), cancellationToken);
                ValidateResponse(response);
                return response.Result;
            }, new StringBuilder().Append("Fetch patches for config ").Append(configId).ToString(), cancellationToken);
        }

        private async Task ApplyPatchToConfigAsync(GameConfigSchema config, CancellationToken cancellationToken)
        {
            if (config?.Data == null || string.IsNullOrEmpty(config.Id))
                return;

            var patches = await GetConfigPatchesAsync(config.Id, cancellationToken);
            if (patches == null || patches.Count == 0)
                return;

            patches.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));

            foreach (var patch in patches)
            {
                if (patch.Data == null) continue;
                foreach (var kvp in patch.Data)
                    config.Data[kvp.Key] = kvp.Value;
            }
        }

        private async Task ApplyPatchesToConfigsAsync(List<GameConfigSchema> configs, CancellationToken cancellationToken)
        {
            if (configs == null || configs.Count == 0)
                return;

            //Apply patch directly
            foreach (var config in configs)
            {
                await ApplyPatchToConfigAsync(config, cancellationToken);
            }
        }
    }
}
