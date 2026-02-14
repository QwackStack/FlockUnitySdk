using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Flock.Models
{
    // ---- OpenAPI Auth Request/Response Models ----

    public class PlayerLoginRequest
    {
        [JsonProperty("login_type")]
        public string LoginType { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("google_id")]
        public string GoogleId { get; set; }

        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        [JsonProperty("device_type")]
        public string DeviceType { get; set; }

        [JsonProperty("apple_id")]
        public string AppleId { get; set; }

        [JsonProperty("facebook_id")]
        public string FacebookId { get; set; }

        [JsonProperty("steam_id")]
        public string SteamId { get; set; }

        [JsonProperty("discord_id")]
        public string DiscordId { get; set; }
    }

    public class PlayerEmailRegistrationRequest
    {
        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class PlayerDeviceLoginRequest
    {
        [JsonProperty("device_type")]
        public string DeviceType { get; set; }

        [JsonProperty("device_id")]
        public string DeviceId { get; set; }
    }

    public class PlayerDeviceRegistrationRequest
    {
        [JsonProperty("device_type")]
        public string DeviceType { get; set; }

        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class PlayerLoginResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }
    }

    // ---- Game Models ----

    public class GameSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("read_me")]
        public string ReadMe { get; set; }

        [JsonProperty("stage")]
        public string Stage { get; set; }

        [JsonProperty("studio_id")]
        public string StudioId { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonProperty("deleted_at")]
        public DateTime? DeletedAt { get; set; }
    }

    public class GameVersionSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("release_type")]
        public string ReleaseType { get; set; }

        [JsonProperty("env")]
        public string Env { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    // ---- Game Config Models ----

    public class GameConfigSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("game_id")]
        public string GameId { get; set; }

        [JsonProperty("game_version_id")]
        public string GameVersionId { get; set; }

        [JsonProperty("schema")]
        public Dictionary<string, object> Schema { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("tag")]
        public string Tag { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    // ---- Game Patch Models ----

    public class GamePatchSchema
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("game_config_id")]
        public string GameConfigId { get; set; }

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }

    // ---- Achievement Models ----

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

    // ---- Leaderboard Models ----

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

    // ---- Player Data Models ----

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

    // ---- Common Models ----

    public class PaginatedResponse<T>
    {
        public T[] Items { get; set; }
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }
}
