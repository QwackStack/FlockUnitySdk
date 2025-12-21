using System;
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
        private PlayerDataService _playerData;

        public FlockClient(FlockInitConfig initConfig, IFlockLogger logger = null)
        {
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
            if (logger == null)
            {
                if (initConfig.EnableDebugLogs)
                {
                    _logger =new UnityFlockLogger();
                }
                else
                {
                    _logger = new NullFlockLogger();
                } 
                _logger.LogInfo($"Initializing Flock SDK - Environment: {initConfig.Environment}");
            }
            _retryHandler = new RetryHandler(initConfig.RetryPolicy, _logger);
            InitializeServices();
        }

        private void InitializeServices()
        {
            _achievements = new FlockAchievementProvider(this);
            _leaderboards = new FlockLeaderboardProvider(this);
            _playerData = new PlayerDataService(this);
            _config = new FlockConfigProvider(this);
        }

        // Internal accessors for services
        internal IFlockLogger Logger => _logger;
        internal RetryHandler RetryHandler => _retryHandler;

        // Service accessors
        public FlockAchievementProvider Achievements => _achievements;
        public FlockLeaderboardProvider Leaderboards => _leaderboards;
        public FlockConfigProvider Config => _config;
        public PlayerDataService PlayerData => _playerData;

        // Public properties
        public string CurrentPlayerId => _tokenClaims?.PlayerId;
        public string GameId => _tokenClaims?.GameId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && !IsTokenExpired();
        public JwtTokenClaims TokenClaims => _tokenClaims;

        public async Task<LoginResponse> LoginAsync(string email, string password, string otp = null, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting login for email: {email}");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<LoginResponse>(
                        $"{_initConfig.ApiUrl}/auth/login",
                        new LoginRequest
                        {
                            Email = email,
                            Password = password,
                            Otp = otp
                        },
                        null,
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid login response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Login successful for player: {CurrentPlayerId}");

                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Login failed", ex);
                throw new FlockAuthException("Login failed", ex);
            }
        }

        public async Task<RegisterResponse> RegisterAsync(string email, string password, string confirmPassword, string otp = null, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting registration for email: {email}");

                return await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<RegisterResponse>(
                        $"{_initConfig.ApiUrl}/auth/register",
                        new RegisterRequest
                        {
                            Email = email,
                            Password = password,
                            ConfirmPassword = confirmPassword,
                            Otp = otp
                        },
                        null,
                        cancellationToken
                    );
                }, cancellationToken);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Registration failed", ex);
                throw new FlockAuthException("Registration failed", ex);
            }
        }

        public async Task<AuthResponse> AuthenticateWithSteamAsync(string steamTicket, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Attempting Steam authentication");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<AuthResponse>(
                        $"{_initConfig.ApiUrl}/auth/steam",
                        new SteamAuthRequest { Ticket = steamTicket },
                        null,
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid Steam auth response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Steam authentication successful for player: {CurrentPlayerId}");

                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Steam authentication failed", ex);
                throw new FlockAuthException("Steam authentication failed", ex);
            }
        }

        public async Task<AuthResponse> AuthenticateWithGameCenterAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Attempting Game Center authentication");

                var response = await _retryHandler.ExecuteAsync(async () => await HttpClient.PostAsync<AuthResponse>(
                    $"{_initConfig.ApiUrl}/auth/gamecenter",
                    new GameCenterAuthRequest(), null, cancellationToken
                ), cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid Game Center auth response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Game Center authentication successful for player: {CurrentPlayerId}");

                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Game Center authentication failed", ex);
                throw new FlockAuthException("Game Center authentication failed", ex);
            }
        }

        public async Task<AuthResponse> AuthenticateWithPlayStoreAsync(string playStoreToken, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Attempting Play Store authentication");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<AuthResponse>(
                        $"{_initConfig.ApiUrl}/auth/playstore",
                        new PlayStoreAuthRequest { Token = playStoreToken },
                        null,
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid Play Store auth response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Play Store authentication successful for player: {CurrentPlayerId}");

                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Play Store authentication failed", ex);
                throw new FlockAuthException("Play Store authentication failed", ex);
            }
        }

        public async Task<AuthResponse> AuthenticateWithDeviceIdAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug($"Attempting Device ID authentication");

                var response = await _retryHandler.ExecuteAsync(async () =>
                {
                    return await HttpClient.PostAsync<AuthResponse>(
                        $"{_initConfig.ApiUrl}/auth/device",
                        new DeviceAuthRequest { DeviceId = deviceId },
                        null,
                        cancellationToken
                    );
                }, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockAuthException("Invalid Device ID auth response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo($"Device ID authentication successful for player: {CurrentPlayerId}");

                return response;
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Device ID authentication failed", ex);
                throw new FlockAuthException("Device ID authentication failed", ex);
            }
        }

        /// <summary>
        /// Refreshes the access token using the refresh token
        /// </summary>
        public async Task<RefreshTokenResponse> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_refreshToken))
            {
                throw new FlockTokenRefreshException("No refresh token available");
            }

            _isRefreshingToken = true;

            try
            {
                _logger.LogDebug("Refreshing access token");

                var response = await HttpClient.PostAsync<RefreshTokenResponse>(
                    $"{_initConfig.ApiUrl}/auth/refresh",
                    new RefreshTokenRequest { RefreshToken = _refreshToken },
                    null,
                    cancellationToken
                );

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    throw new FlockTokenRefreshException("Invalid refresh token response from server");
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo("Token refresh successful");

                return response;
            }
            catch (FlockException)
            {
                _logger.LogError("Token refresh failed - clearing tokens");
                ClearTokens();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Token refresh failed", ex);
                ClearTokens();
                throw new FlockTokenRefreshException("Token refresh failed", ex);
            }
            finally
            {
                _isRefreshingToken = false;
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

        /// <summary>
        /// Checks if the current token is expired
        /// </summary>
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

        /// <summary>
        /// Gets the time remaining until token expiration
        /// </summary>
        public TimeSpan? GetTimeUntilTokenExpiration()
        {
            if (_tokenExpirationTime.HasValue)
            {
                var remaining = _tokenExpirationTime.Value - DateTime.UtcNow;
                return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
            }
            return null;
        }

        /// <summary>
        /// Gets an access token, automatically refreshing if expired
        /// </summary>
        public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            if (IsTokenExpired() && !string.IsNullOrEmpty(_refreshToken))
            {
                if (!_isRefreshingToken)
                {
                    try
                    {
                        await RefreshTokenAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Failed to refresh token automatically", ex);
                        throw;
                    }
                }
            }

            if (string.IsNullOrEmpty(_accessToken))
            {
                throw new FlockAuthException("No valid access token available. Please authenticate first.");
            }

            return _accessToken;
        }

        /// <summary>
        /// Sets the access and refresh tokens and parses JWT claims
        /// </summary>
        private void SetTokens(string accessToken, string refreshToken)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;

            // Parse JWT token
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
