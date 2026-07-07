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

    public class PlayerGoogleLoginRequest
    {
        [JsonProperty("id_token")]
        public string IdToken { get; set; }
    }

    public class PlayerGoogleRegistrationRequest
    {
        [JsonProperty("id_token")]
        public string IdToken { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class PlayerAppleLoginRequest
    {
        [JsonProperty("identity_token")]
        public string IdentityToken { get; set; }
    }

    public class PlayerAppleRegistrationRequest
    {
        [JsonProperty("identity_token")]
        public string IdentityToken { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class PlayerSteamLoginRequest
    {
        [JsonProperty("session_ticket")]
        public string SessionTicket { get; set; }
    }

    public class PlayerSteamRegistrationRequest
    {
        [JsonProperty("session_ticket")]
        public string SessionTicket { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class PlayerLoginResponse
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }
    }

    public class PlayerRefreshTokenRequest
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }
    }

    public class PlayerPasswordForgotRequest
    {
        [JsonProperty("email")]
        public string Email { get; set; }
    }

    public class PlayerPasswordResetRequest
    {
        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("new_password")]
        public string NewPassword { get; set; }
    }

    public class PlayerEmailVerifyRequest
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    public class PlayerAuthActionResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }
    }

    public class PlayerTokenRevokeResponse
    {
        [JsonProperty("revoked")]
        public bool Revoked { get; set; }
    }

    public class PlayerNameAvailableResponse
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("available")]
        public bool Available { get; set; }
    }
}
