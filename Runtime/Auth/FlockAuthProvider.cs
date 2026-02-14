using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Models;
using Flock.Http;

namespace Flock.Auth
{
    public class FlockAuthProvider
    {
        private readonly string _apiUrl;
        private readonly Dictionary<string, string> _headers;

        public FlockAuthProvider(string apiUrl, Dictionary<string, string> headers)
        {
            _apiUrl = apiUrl;
            _headers = headers;
        }

        // POST /v1/player/login
        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            return await HttpClient.PostAsync<PlayerLoginResponse>(
                $"{_apiUrl}/v1/player/login",
                new PlayerLoginRequest
                {
                    LoginType = "email",
                    Email = email,
                    Password = password
                },
                _headers,
                cancellationToken
            );
        }

        // POST /v1/player/register
        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null, CancellationToken cancellationToken = default)
        {
            return await HttpClient.PostAsync<PlayerLoginResponse>(
                $"{_apiUrl}/v1/player/register",
                new PlayerEmailRegistrationRequest
                {
                    Email = email,
                    Password = password,
                    Name = name
                },
                _headers,
                cancellationToken
            );
        }

        // POST /v1/player/login/device
        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceType, string deviceId, CancellationToken cancellationToken = default)
        {
            return await HttpClient.PostAsync<PlayerLoginResponse>(
                $"{_apiUrl}/v1/player/login/device",
                new PlayerDeviceLoginRequest
                {
                    DeviceType = deviceType,
                    DeviceId = deviceId
                },
                _headers,
                cancellationToken
            );
        }

        // POST /v1/player/register/device
        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceType, string deviceId, string name = null, CancellationToken cancellationToken = default)
        {
            return await HttpClient.PostAsync<PlayerLoginResponse>(
                $"{_apiUrl}/v1/player/register/device",
                new PlayerDeviceRegistrationRequest
                {
                    DeviceType = deviceType,
                    DeviceId = deviceId,
                    Name = name
                },
                _headers,
                cancellationToken
            );
        }
    }
}
