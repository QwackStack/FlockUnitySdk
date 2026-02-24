using Newtonsoft.Json;

namespace Flock.Models
{
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
}
