using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Flock.Auth
{
    public interface IFlockAuthProvider
    {
        Task<AuthResult> AuthenticateAsync();
        Task<bool> IsAuthenticatedAsync();
        Task LogoutAsync();
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string ErrorMessage { get; set; }
        public AuthProviderType ProviderType { get; set; }
    }

    public enum AuthProviderType
    {
        Email,
        Steam,
        GameCenter,
        PlayStore,
        DeviceId
    }

    public class EmailAuthProvider : IFlockAuthProvider
    {
        private readonly string _email;
        private readonly string _password;
        private readonly FlockClient _client;

        public EmailAuthProvider(FlockClient client, string email, string password)
        {
            _client = client;
            _email = email;
            _password = password;
        }

        public async Task<AuthResult> AuthenticateAsync()
        {
            try
            {
                var response = await _client.LoginAsync(_email, _password, null);
                return new AuthResult
                {
                    Success = true,
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ProviderType = AuthProviderType.Email
                };
            }
            catch (Exception ex)
            {
                return new AuthResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ProviderType = AuthProviderType.Email
                };
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            return !string.IsNullOrEmpty(_client.GetAccessToken());
        }

        public async Task LogoutAsync()
        {
            _client.ClearTokens();
        }
    }

    public class SteamAuthProvider : IFlockAuthProvider
    {
        private readonly FlockClient _client;
        private readonly string _steamTicket;

        public SteamAuthProvider(FlockClient client, string steamTicket)
        {
            _client = client;
            _steamTicket = steamTicket;
        }

        public async Task<AuthResult> AuthenticateAsync()
        {
            try
            {
                var response = await _client.AuthenticateWithSteamAsync(_steamTicket);
                return new AuthResult
                {
                    Success = true,
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ProviderType = AuthProviderType.Steam
                };
            }
            catch (Exception ex)
            {
                return new AuthResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ProviderType = AuthProviderType.Steam
                };
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            return !string.IsNullOrEmpty(_client.GetAccessToken());
        }

        public async Task LogoutAsync()
        {
            _client.ClearTokens();
        }
    }

    public class GameCenterAuthProvider : IFlockAuthProvider
    {
        private readonly FlockClient _client;

        public GameCenterAuthProvider(FlockClient client)
        {
            _client = client;
        }

        public async Task<AuthResult> AuthenticateAsync()
        {
            try
            {
                var response = await _client.AuthenticateWithGameCenterAsync();
                return new AuthResult
                {
                    Success = true,
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ProviderType = AuthProviderType.GameCenter
                };
            }
            catch (Exception ex)
            {
                return new AuthResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ProviderType = AuthProviderType.GameCenter
                };
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            return !string.IsNullOrEmpty(_client.GetAccessToken());
        }

        public async Task LogoutAsync()
        {
            _client.ClearTokens();
        }
    }

    public class PlayStoreAuthProvider : IFlockAuthProvider
    {
        private readonly FlockClient _client;
        private readonly string _playStoreToken;

        public PlayStoreAuthProvider(FlockClient client, string playStoreToken)
        {
            _client = client;
            _playStoreToken = playStoreToken;
        }

        public async Task<AuthResult> AuthenticateAsync()
        {
            try
            {
                var response = await _client.AuthenticateWithPlayStoreAsync(_playStoreToken);
                return new AuthResult
                {
                    Success = true,
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ProviderType = AuthProviderType.PlayStore
                };
            }
            catch (Exception ex)
            {
                return new AuthResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ProviderType = AuthProviderType.PlayStore
                };
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            return !string.IsNullOrEmpty(_client.GetAccessToken());
        }

        public async Task LogoutAsync()
        {
            _client.ClearTokens();
        }
    }

    public class DeviceIdAuthProvider : IFlockAuthProvider
    {
        private readonly FlockClient _client;

        public DeviceIdAuthProvider(FlockClient client)
        {
            _client = client;
        }

        public async Task<AuthResult> AuthenticateAsync()
        {
            try
            {
                var deviceId = SystemInfo.deviceUniqueIdentifier;
                var response = await _client.AuthenticateWithDeviceIdAsync(deviceId);
                return new AuthResult
                {
                    Success = true,
                    AccessToken = response.AccessToken,
                    RefreshToken = response.RefreshToken,
                    ProviderType = AuthProviderType.DeviceId
                };
            }
            catch (Exception ex)
            {
                return new AuthResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ProviderType = AuthProviderType.DeviceId
                };
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            return !string.IsNullOrEmpty(_client.GetAccessToken());
        }

        public async Task LogoutAsync()
        {
            _client.ClearTokens();
        }
    }
} 