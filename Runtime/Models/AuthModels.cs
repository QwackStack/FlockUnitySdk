using System;
using System.Collections.Generic;

namespace Flock.Models
{
    // Auth Models
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
        public string PlayerId { get; set; }
        public string Email { get; set; }
        public long ExpiresIn { get; set; }
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
        public string PlayerId { get; set; }
        public string Email { get; set; }
        public long ExpiresIn { get; set; }
    }

    public class AuthResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public string PlayerId { get; set; }
        public long ExpiresIn { get; set; }
    }

    public class RefreshTokenRequest
    {
        public string RefreshToken { get; set; }
    }

    public class RefreshTokenResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public long ExpiresIn { get; set; }
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

    // Achievement Models
    public class Achievement
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Points { get; set; }
        public string IconUrl { get; set; }
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockedAt { get; set; }
        public float Progress { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class UnlockAchievementRequest
    {
        public string PlayerId { get; set; }
        public string AchievementId { get; set; }
    }

    public class UpdateAchievementProgressRequest
    {
        public string PlayerId { get; set; }
        public string AchievementId { get; set; }
        public float Progress { get; set; }
    }

    // Leaderboard Models
    public class LeaderboardEntry
    {
        public string Id { get; set; }
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public long Score { get; set; }
        public int Rank { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class SubmitScoreRequest
    {
        public string PlayerId { get; set; }
        public string LeaderboardId { get; set; }
        public long Score { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class LeaderboardInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // Player Data Models
    public class PlayerData
    {
        public string Id { get; set; }
        public string PlayerId { get; set; }
        public string Username { get; set; }
        public int Level { get; set; }
        public int Experience { get; set; }
        public Dictionary<string, object> CustomData { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    internal class PlayerDataRequest
    {
        public string GameId { get; set; }
        public string PlayerId { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    internal class UpdatePlayerDataRequest
    {
        public Dictionary<string, object> Data { get; set; }
    }

    // Config Models
    public class GameConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }
        public string Version { get; set; }
    }

    // Common Models
    public class PaginatedResponse<T>
    {
        public T[] Items { get; set; }
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }
} 