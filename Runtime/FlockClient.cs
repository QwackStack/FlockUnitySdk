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
using Flock.Analytics;
using Flock.Providers;
using UnityEngine;

namespace Flock
{
    public class FlockClient : IFlockClient
    {
        private readonly FlockInitConfig _initConfig;
        private readonly IFlockLogger _logger;
        private readonly RetryHandler _retryHandler;
        //To avoid refresh deadlocks
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        private string _accessToken;
        private string _refreshToken;
        private JwtTokenClaims _tokenClaims;

        /// <summary>
        /// Fired when a token refresh fails, meaning the player must log in again.
        /// Subscribe to this to show your re-login UI.
        /// </summary>
        public event Action OnSessionExpired;

        private FlockConfigProvider _config;
        private FlockSchemaProvider _schema;
        private FlockGameProvider _game;
        private PlayerProvider _playerData;
        private FlockCommandProvider _commands;
        private FlockShopProvider _shop;
        private FlockBanProvider _ban;
        private FlockSession _session;
        private IAnalyticProvider _analytics;

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
            _playerData = new PlayerProvider(this);
            _config = new FlockConfigProvider(this);
            _schema = new FlockSchemaProvider(this);
            _game = new FlockGameProvider(this);
            _commands = new FlockCommandProvider(this);
            _shop = new FlockShopProvider(this);
            _ban = new FlockBanProvider(this);

            if (_initConfig.AnalyticsConfig.Enabled)
            {
                _session = new FlockSession(_initConfig.AnalyticsConfig, _logger);
                _analytics = new FlockAnalyticsProvider(this);
            }
            else
            {
                _session = new FlockSession(_initConfig.AnalyticsConfig, _logger);
                _analytics = new NullAnalyticsProvider(this);
            }
        }

        internal IFlockLogger Logger => _logger;
        internal RetryHandler RetryHandler => _retryHandler;
        internal FlockInitConfig InitConfig => _initConfig;

        public FlockConfigProvider Config => _config;
        public FlockSchemaProvider Schema => _schema;
        public FlockGameProvider Game => _game;
        public PlayerProvider Player  => _playerData;
        public FlockCommandProvider Commands => _commands;
        public FlockShopProvider Shop => _shop;
        public FlockBanProvider Ban => _ban;
        public IAnalyticProvider Analytics => _analytics;

        internal FlockSession Session => _session;
        public bool HasActiveSession => _session?.IsActive ?? false;
        public string CurrentSessionId => _session?.ServerSessionId ?? _session?.SessionId;

        public string CurrentPlayerId => _tokenClaims?.PlayerId;
        public string GameId => _initConfig.GameId;
        public string GameVersionId => _initConfig.GameVersionId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
        public bool IsTokenExpired =>
            _tokenClaims?.ExpirationTime.HasValue == true &&
            _tokenClaims.ExpirationTime.Value <= DateTime.UtcNow;
        public JwtTokenClaims TokenClaims => _tokenClaims;

        internal Dictionary<string, string> GetBaseHeaders()
        {
            var headers = new Dictionary<string, string>(_initConfig.GetBaseHeaders());
            if (!string.IsNullOrEmpty(_accessToken))
                headers["Authorization"] = $"Bearer {_accessToken}";
            return headers;
        }

        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{_initConfig.ApiUrl}/v1/player/login",
                    new PlayerLoginRequest { LoginType = "email", Email = email, Password = password },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Email login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{_initConfig.ApiUrl}/v1/player/login/device",
                    new PlayerDeviceLoginRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Device login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{_initConfig.ApiUrl}/v1/player/register",
                    new PlayerEmailRegistrationRequest { Email = email, Password = password, Name = name },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Email registration", cancellationToken);
        }

        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceId, string name = null, CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{_initConfig.ApiUrl}/v1/player/register/device",
                    new PlayerDeviceRegistrationRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId, Name = name },
                    _initConfig.GetBaseHeaders(), cancellationToken),
                "Device registration", cancellationToken);
        }

        private async Task<PlayerLoginResponse> ExecuteAuthAsync(
            Func<Task<PlayerLoginResponse>> operation, string context, CancellationToken cancellationToken)
        {
            try
            {
                PlayerLoginResponse response = await _retryHandler.ExecuteAsync(operation, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                    throw new FlockAuthException($"Invalid {context.ToLower()} response from server");

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"{context} successful for player: {CurrentPlayerId}");

                if (_analytics != null)
                {
                    try
                    {
                        await _analytics.InitializeAsync(cancellationToken);
                    }
                    catch (Exception analyticsEx)
                    {
                        _logger.LogWarning($"Analytics initialization failed (non-fatal): {analyticsEx.Message}");
                    }
                }

                return response;
            }
            catch (FlockException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError($"{context} failed", ex);
                throw new FlockAuthException($"{context} failed", ex);
            }
        }

        /// <summary>
        /// Explicitly refreshes the access token using the stored refresh token.
        /// Throws <see cref="FlockAuthException"/> if no refresh token is available or if the refresh fails.
        /// </summary>
        public async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                throw new FlockAuthException("No refresh token available. Please log in first.");

            bool success = await TryRefreshTokenAsync(cancellationToken);
            if (!success)
                _logger.LogException(new FlockAuthException("Token refresh failed. Please log in again."));
            
            return success;
        }

        internal async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                return false;

            string refreshSnapshot = _refreshToken;
            string playerIdSnapshot = CurrentPlayerId;

            await _refreshSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrEmpty(_refreshToken))
                    return false;

                if (_refreshToken != refreshSnapshot && !string.IsNullOrEmpty(_accessToken))
                    return true;

                var refreshRequest = new PlayerRefreshTokenRequest { PlayerId = playerIdSnapshot, RefreshToken = refreshSnapshot };
                _logger.LogDebug($"Refresh POST {_initConfig.ApiUrl}/v1/player/token/refresh body={Newtonsoft.Json.JsonConvert.SerializeObject(refreshRequest)}");

                PlayerLoginResponse response = await FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{_initConfig.ApiUrl}/v1/player/token/refresh",
                    refreshRequest,
                    _initConfig.GetBaseHeaders(), cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    ClearTokens();
                    OnSessionExpired?.Invoke();
                    return false;
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo("Token refresh successful");
                return true;
            }
            catch (FlockAuthException e)
            {
                _logger.LogWarning("Token refresh failed: session expired");
                _logger.LogException(e);
                ClearTokens();
                OnSessionExpired?.Invoke();
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("Token refresh failed", ex);
                return false;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        public void ClearTokens()
        {
            _logger.LogInfo("Clearing authentication tokens");

            if (_session != null && _session.IsActive)
            {
                _session.Reset();
            }

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
                    _logger.LogDebug($"Token set for PlayerId: {_tokenClaims.PlayerId}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse JWT token: {ex.Message}");
                    _tokenClaims = null;
                }
            }
        }
    }
}
