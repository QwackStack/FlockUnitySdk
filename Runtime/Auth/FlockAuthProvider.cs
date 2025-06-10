using System;
using System.Threading.Tasks;
using Flock.Models;

namespace Flock.Auth
{
    public class FlockAuthProvider
    {
        private readonly string _apiUrl;
        private readonly string _accessToken;

        public FlockAuthProvider(string apiUrl, string accessToken)
        {
            _apiUrl = apiUrl;
            _accessToken = accessToken;
        }

        public async Task<AuthResult> LoginAsync(string email, string password, string otp = null)
        {
            var response = await HttpClient.PostAsync<LoginResponse>(
                $"{_apiUrl}/auth/login",
                new LoginRequest
                {
                    Email = email,
                    Password = password,
                    Otp = otp
                }
            );

            return new AuthResult
            {
                Success = true,
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken
            };
        }

        public async Task<AuthResult> RegisterAsync(string email, string password, string confirmPassword, string otp = null)
        {
            var response = await HttpClient.PostAsync<RegisterResponse>(
                $"{_apiUrl}/auth/register",
                new RegisterRequest
                {
                    Email = email,
                    Password = password,
                    ConfirmPassword = confirmPassword,
                    Otp = otp
                }
            );

            return new AuthResult
            {
                Success = true,
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken
            };
        }
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string Error { get; set; }
    }
} 