using System;

namespace Flock.Models
{
    public class LoginRequest
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public string Otp { get; set; }
    }

    public class LoginResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }

    public class RegisterRequest
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public string ConfirmPassword { get; set; }
        public string Otp { get; set; }
    }

    public class RegisterResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }

    public class AuthResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }

    public class SteamAuthRequest
    {
        public string Ticket { get; set; }
    }

    public class GameCenterAuthRequest
    {
    }

    public class PlayStoreAuthRequest
    {
        public string Token { get; set; }
    }

    public class DeviceAuthRequest
    {
        public string DeviceId { get; set; }
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
    }

    public class PaginatedResponse<T>
    {
        public T[] Items { get; set; }
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }
} 