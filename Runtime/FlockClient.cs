using System;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;
using Flock.Config;
using Flock.Auth;

namespace Flock
{
    public class FlockClient
    {
        private readonly FlockConfig _config;
        private string _accessToken;
        private string _refreshToken;

        public FlockClient(FlockConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public string GameId => _config.GameId;

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