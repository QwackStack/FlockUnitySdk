using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Auth;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Interfaces;
using Flock.Logging;
using Flock.Models;
using Flock.Services;
using UnityEngine;

namespace Flock
{
    public class FlockClient : IFlockClient
    {
        private readonly FlockInitConfig _initConfig;
        private readonly IFlockLogger _logger;
        private readonly RetryHandler _retryHandler;
        private string _accessToken;
        private string _refreshToken;
        private JwtTokenClaims _tokenClaims;

        private FlockConfigProvider _config;
        private FlockSchemaProvider _schema;
        private FlockGameService _game;
        private PlayerDataService _playerData;
        private FlockCommandProvider _commands;
        private FlockShopProvider _shop;

        public FlockClient(FlockInitConfig initConfig, IFlockLogger logger = null)
        {
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
            _logger = logger ?? (initConfig.EnableDebugLogs ? new UnityFlockLogger() : new NullFlockLogger());
            _logger.LogInfo("Initializing Flock SDK");
            _retryHandler = new RetryHandler(initConfig.RetryPolicy, _logger);
            InitializeServices();
        }

        private void InitializeServices()
        {
            _playerData = new PlayerDataService(this);
            _config = new FlockConfigProvider(this);
            _schema = new FlockSchemaProvider(this);
            _game = new FlockGameService(this);
            _commands = new FlockCommandProvider(this);
            _shop = new FlockShopProvider(this);
        }

        internal IFlockLogger Logger => _logger;
        internal RetryHandler RetryHandler => _retryHandler;
        internal FlockInitConfig InitConfig => _initConfig;

        public FlockConfigProvider Config => _config;
        public FlockSchemaProvider Schema => _schema;
        public FlockGameService Game => _game;
        public PlayerDataService PlayerData => _playerData;
        public FlockCommandProvider Commands => _commands;
        public FlockShopProvider Shop => _shop;

        public string CurrentPlayerId => _tokenClaims?.PlayerId;
        public string GameId => _initConfig.GameId;
        public string GameVersionId => _initConfig.GameVersionId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
        public JwtTokenClaims TokenClaims => _tokenClaims;

        internal Dictionary<string, string> GetBaseHeaders()
        {
            var headers = _initConfig.GetBaseHeaders();
            if (!string.IsNullOrEmpty(_accessToken))
                headers["Authorization"] = new StringBuilder().Append("Bearer ").Append(_accessToken).ToString();
            return headers;
        }

        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    new StringBuilder().Append(_initConfig.ApiUrl).Append("/v1/player/login").ToString(),
                    new PlayerLoginRequest { LoginType = "email", Email = email, Password = password },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Email login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    new StringBuilder().Append(_initConfig.ApiUrl).Append("/v1/player/login/device").ToString(),
                    new PlayerDeviceLoginRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Device login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    new StringBuilder().Append(_initConfig.ApiUrl).Append("/v1/player/register").ToString(),
                    new PlayerEmailRegistrationRequest { Email = email, Password = password, Name = name },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Email registration", cancellationToken);
        }

        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceId, string name = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    new StringBuilder().Append(_initConfig.ApiUrl).Append("/v1/player/register/device").ToString(),
                    new PlayerDeviceRegistrationRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId, Name = name },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Device registration", cancellationToken);
        }

        private async Task<PlayerLoginResponse> ExecuteAuthAsync(
            Func<Task<PlayerLoginResponse>> operation, string context, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _retryHandler.ExecuteAsync(operation, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                    throw new FlockAuthException(new StringBuilder().Append("Invalid ")
                        .Append(context.ToLower())
                        .Append(" response from server")
                        .ToString());

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo(new StringBuilder().Append(context)
                    .Append(" successful for player: ")
                    .Append(CurrentPlayerId)
                    .ToString());
                return response;
            }
            catch (FlockException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(new StringBuilder().Append(context).Append(" failed").ToString(), ex);
                throw new FlockAuthException(new StringBuilder().Append(context).Append(" failed").ToString(), ex);
            }
        }

        public void ClearTokens()
        {
            _logger.LogInfo("Clearing authentication tokens");
            _accessToken = null;
            _refreshToken = null;
            _tokenClaims = null;
        }

        public string GetApiUrl()
        {
            return _initConfig.ApiUrl;
        }

        private void SetTokens(string accessToken, string refreshToken)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;

            if (!string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    _tokenClaims = JwtTokenParser.Parse(accessToken);
                    _logger.LogDebug(new StringBuilder().Append("Token set for PlayerId: ")
                        .Append(_tokenClaims.PlayerId)
                        .ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(new StringBuilder().Append("Failed to parse JWT token: ")
                        .Append(ex.Message)
                        .ToString());
                    _tokenClaims = null;
                }
            }
        }
    }
}
