using System;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Config;
using Flock.Services;
using Flock.Auth;

namespace Flock
{
    public class FlockClient
    {
        private readonly FlockConfig _config;
        private string _accessToken;
        private string _refreshToken;
        private IFlockAuthProvider _currentAuthProvider;
        
        public readonly GameConfigService GameConfigs;
        public readonly LeaderboardService Leaderboards;
        public readonly AchievementService Achievements;
        public readonly DocumentService Documents;
        public readonly EventService Events;
        public readonly CurrencyService Currencies;
        public readonly ShopService Shop;
        public readonly SegmentService Segments;
        public readonly AssetService Assets;
        public readonly GameService Games;
        public readonly VersionService Versions;
        public readonly PatchService Patches;
        public readonly PlayerService Players;
        public readonly PlayerDataService PlayerData;

        public FlockClient(FlockConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            // Initialize all services
            GameConfigs = new GameConfigService(this);
            Leaderboards = new LeaderboardService(this);
            Achievements = new AchievementService(this);
            Documents = new DocumentService(this);
            Events = new EventService(this);
            Currencies = new CurrencyService(this);
            Shop = new ShopService(this);
            Segments = new SegmentService(this);
            Assets = new AssetService(this);
            Games = new GameService(this);
            Versions = new VersionService(this);
            Patches = new PatchService(this);
            Players = new PlayerService(this);
            PlayerData = new PlayerDataService(this);
        }

        public async Task<LoginResponse> LoginAsync(string email, string password, string otp = null)
        {
            var response = await HttpClient.PostAsync<LoginResponse>(
                $"{_config.ApiUrl}/auth/login",
                new LoginRequest
                {
                    Email = email,
                    Password = password,
                    Otp = otp
                }
            );

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;
            
            return response;
        }

        public async Task<RegisterResponse> RegisterAsync(string email, string password, string confirmPassword, string otp = null)
        {
            return await HttpClient.PostAsync<RegisterResponse>(
                $"{_config.ApiUrl}/auth/register",
                new RegisterRequest
                {
                    Email = email,
                    Password = password,
                    ConfirmPassword = confirmPassword,
                    Otp = otp
                }
            );
        }

        public async Task<AuthResponse> AuthenticateWithSteamAsync(string steamTicket)
        {
            var response = await HttpClient.PostAsync<AuthResponse>(
                $"{_config.ApiUrl}/auth/steam",
                new SteamAuthRequest { Ticket = steamTicket }
            );

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;
            
            return response;
        }

        public async Task<AuthResponse> AuthenticateWithGameCenterAsync()
        {
            var response = await HttpClient.PostAsync<AuthResponse>(
                $"{_config.ApiUrl}/auth/gamecenter",
                new GameCenterAuthRequest()
            );

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;
            
            return response;
        }

        public async Task<AuthResponse> AuthenticateWithPlayStoreAsync(string playStoreToken)
        {
            var response = await HttpClient.PostAsync<AuthResponse>(
                $"{_config.ApiUrl}/auth/playstore",
                new PlayStoreAuthRequest { Token = playStoreToken }
            );

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;
            
            return response;
        }

        public async Task<AuthResponse> AuthenticateWithDeviceIdAsync(string deviceId)
        {
            var response = await HttpClient.PostAsync<AuthResponse>(
                $"{_config.ApiUrl}/auth/device",
                new DeviceAuthRequest { DeviceId = deviceId }
            );

            _accessToken = response.AccessToken;
            _refreshToken = response.RefreshToken;
            
            return response;
        }

        public void SetAuthProvider(IFlockAuthProvider provider)
        {
            _currentAuthProvider = provider;
        }

        public async Task<AuthResult> AuthenticateAsync()
        {
            if (_currentAuthProvider == null)
            {
                throw new InvalidOperationException("No auth provider set. Call SetAuthProvider first.");
            }

            return await _currentAuthProvider.AuthenticateAsync();
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            if (_currentAuthProvider == null)
            {
                return false;
            }

            return await _currentAuthProvider.IsAuthenticatedAsync();
        }

        public async Task LogoutAsync()
        {
            if (_currentAuthProvider != null)
            {
                await _currentAuthProvider.LogoutAsync();
            }
        }

        public string GetAccessToken()
        {
            return _accessToken;
        }

        public void ClearTokens()
        {
            _accessToken = null;
            _refreshToken = null;
        }

        internal string GetApiUrl()
        {
            return _config.ApiUrl;
        }
    }
} 