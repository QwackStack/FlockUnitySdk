using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Config;
using Flock.Auth;
using Flock.Achievements;
using Flock.Leaderboard;
using Flock.Services;
using Flock.Logging;
using Flock.Exceptions;

namespace Flock
{
    public class FlockClient
    {
        private readonly FlockInitConfig _initConfig;
        private readonly IFlockLogger _logger;
        private readonly RetryHandler _retryHandler;
        private string _accessToken;
        private string _refreshToken;
        private JwtTokenClaims _tokenClaims;
        private DateTime? _tokenExpirationTime;
        private bool _isRefreshingToken = false;

        // Service providers
        private FlockAchievementProvider _achievements;
        private FlockLeaderboardProvider _leaderboards;
        private FlockConfigProvider _config;
        private FlockGamePatchProvider _patches;
        private FlockGameService _game;
        private PlayerDataService _playerData;

        public FlockClient(FlockInitConfig initConfig, IFlockLogger logger = null)
        {
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
            if (logger == null)
            {
                if (initConfig.EnableDebugLogs)
                {
                    _logger = new UnityFlockLogger();
                }
                else
                {
                    _logger = new NullFlockLogger();
                }
            }
            else
            {
                _logger = logger;
            }
            _logger.LogInfo($"Initializing Flock SDK - Environment: {initConfig.Environment}");
            _retryHandler = new RetryHandler(initConfig.RetryPolicy, _logger);
            InitializeServices();
        }

        private void InitializeServices()
        {
            _achievements = new FlockAchievementProvider(this);
            _leaderboards = new FlockLeaderboardProvider(this);
            _playerData = new PlayerDataService(this);
            _config = new FlockConfigProvider(this);
            _patches = new FlockGamePatchProvider(this);
            _game = new FlockGameService(this);
        }

        // Internal accessors for services
        internal IFlockLogger Logger => _logger;
        internal RetryHandler RetryHandler => _retryHandler;
        internal FlockInitConfig InitConfig => _initConfig;

        // Service accessors
        public FlockAchievementProvider Achievements => _achievements;
        public FlockLeaderboardProvider Leaderboards => _leaderboards;
        public FlockConfigProvider Config => _config;
        public FlockGamePatchProvider Patches => _patches;
        public FlockGameService Game => _game;
        public PlayerDataService PlayerData => _playerData;

        // Public properties
        public string CurrentPlayerId => _tokenClaims?.PlayerId;
        public string GameId => _initConfig.GameId;
        public string GameVersionId => _initConfig.GameVersionId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && !IsTokenExpired();
        public JwtTokenClaims TokenClaims => _tokenClaims;

        internal Dictionary<string, string> GetBaseHeaders()
        {
            return _initConfig.GetBaseHeaders();
        }

        internal Dictionary<string, string> GetAuthenticatedHeaders()
        {
            return _initConfig.GetAuthenticatedHeaders(_accessToken);
        }

        // ---- Auth: Email Login (POST /v1/player/login) ----
        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting email login for: {email}");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<PlayerLoginResponse>(
                        $"{_initConfig.ApiUrl}/v1/player/login",
                        new PlayerLoginRequest
                        {
                            LoginType = "email",
                            Email = email,
                            Password = password
                        },
                        _initConfig.GetBaseHeaders(),
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid login response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Email login successful for player: {CurrentPlayerId}");
                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Email login failed", ex);
                throw new FlockAuthException("Email login failed", ex);
            }
        }

        // ---- Auth: Email Register (POST /v1/player/register) ----
        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting email registration for: {email}");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<PlayerLoginResponse>(
                        $"{_initConfig.ApiUrl}/v1/player/register",
                        new PlayerEmailRegistrationRequest
                        {
                            Email = email,
                            Password = password,
                            Name = name
                        },
                        _initConfig.GetBaseHeaders(),
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid registration response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Email registration successful for player: {CurrentPlayerId}");
                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Email registration failed", ex);
                throw new FlockAuthException("Email registration failed", ex);
            }
        }

        // ---- Auth: Device Login (POST /v1/player/login/device) ----
        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceType, string deviceId, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting device login for device: {deviceId}");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<PlayerLoginResponse>(
                        $"{_initConfig.ApiUrl}/v1/player/login/device",
                        new PlayerDeviceLoginRequest
                        {
                            DeviceType = deviceType,
                            DeviceId = deviceId
                        },
                        _initConfig.GetBaseHeaders(),
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid device login response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Device login successful for player: {CurrentPlayerId}");
                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Device login failed", ex);
                throw new FlockAuthException("Device login failed", ex);
            }
        }

        // ---- Auth: Device Register (POST /v1/player/register/device) ----
        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceType, string deviceId, string name = null, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting device registration for device: {deviceId}");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<PlayerLoginResponse>(
                        $"{_initConfig.ApiUrl}/v1/player/register/device",
                        new PlayerDeviceRegistrationRequest
                        {
                            DeviceType = deviceType,
                            DeviceId = deviceId,
                            Name = name
                        },
                        _initConfig.GetBaseHeaders(),
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid device registration response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Device registration successful for player: {CurrentPlayerId}");
                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Device registration failed", ex);
                throw new FlockAuthException("Device registration failed", ex);
            }
        }

        public string GetAccessToken()
        {
            return _accessToken;
        }

        public void ClearTokens()
        {
            _logger.LogInfo("Clearing authentication tokens");
            _accessToken = null;
            _refreshToken = null;
            _tokenClaims = null;
            _tokenExpirationTime = null;
        }

        public string GetApiUrl()
        {
            return _initConfig.ApiUrl;
        }

        public bool IsTokenExpired()
        {
            if (string.IsNullOrEmpty(_accessToken))
                return true;

            if (_tokenExpirationTime.HasValue)
            {
                return DateTime.UtcNow >= _tokenExpirationTime.Value;
            }

            return false;
        }

        public TimeSpan? GetTimeUntilTokenExpiration()
        {
            if (_tokenExpirationTime.HasValue)
            {
                var remaining = _tokenExpirationTime.Value - DateTime.UtcNow;
                return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
            }
            return null;
        }

        public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                throw new FlockAuthException("No valid access token available. Please authenticate first.");
            }

            return _accessToken;
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
                    _tokenExpirationTime = _tokenClaims.ExpirationTime;
                    _logger.LogDebug($"Token set for PlayerId: {_tokenClaims.PlayerId}, Expires: {_tokenExpirationTime}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse JWT token: {ex.Message}");
                    _tokenClaims = null;
                    _tokenExpirationTime = null;
                }
            }
        }
    }
}
