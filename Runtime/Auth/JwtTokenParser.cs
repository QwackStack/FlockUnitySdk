using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Flock.Auth
{
    /// <summary>
    /// JWT token parser and decoder
    /// </summary>
    public class JwtTokenParser
    {
        /// <summary>
        /// Parses a JWT token and extracts claims
        /// </summary>
        public static JwtTokenClaims Parse(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("Token cannot be null or empty", nameof(token));
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                throw new ArgumentException("Invalid JWT token format. Expected 3 parts separated by dots.");
            }

            try
            {
                // Decode the payload (second part)
                var payload = parts[1];
                var jsonPayload = Base64UrlDecode(payload);
                var claims = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonPayload);

                return new JwtTokenClaims
                {
                    PlayerId = GetClaimValue(claims, "sub", "playerId", "player_id", "userId", "user_id"),
                    GameId = GetClaimValue(claims, "gameId", "game_id", "gid"),
                    Email = GetClaimValue(claims, "email"),
                    Username = GetClaimValue(claims, "username", "name"),
                    Role = GetClaimValue(claims, "role"),
                    ExpirationTime = GetExpirationTime(claims),
                    IssuedAt = GetIssuedAtTime(claims),
                    Issuer = GetClaimValue(claims, "iss"),
                    Audience = GetClaimValue(claims, "aud"),
                    RawClaims = claims
                };
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    new StringBuilder().Append("Failed to parse JWT token: ").Append(ex.Message).ToString(), ex);
            }
        }

        private static string GetClaimValue(Dictionary<string, object> claims, params string[] possibleKeys)
        {
            foreach (var key in possibleKeys)
            {
                if (claims.ContainsKey(key) && claims[key] != null)
                {
                    return claims[key].ToString();
                }
            }
            return null;
        }

        private static DateTime? GetExpirationTime(Dictionary<string, object> claims)
        {
            if (claims.ContainsKey("exp") && claims["exp"] != null)
            {
                try
                {
                    long exp = Convert.ToInt64(claims["exp"]);
                    return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private static DateTime? GetIssuedAtTime(Dictionary<string, object> claims)
        {
            if (claims.ContainsKey("iat") && claims["iat"] != null)
            {
                try
                {
                    long iat = Convert.ToInt64(claims["iat"]);
                    return DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime;
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private static string Base64UrlDecode(string input)
        {
            string output = input;
            output = output.Replace('-', '+'); // 62nd char of encoding
            output = output.Replace('_', '/'); // 63rd char of encoding

            // Pad with trailing '='s
            switch (output.Length % 4)
            {
                case 0: break; // No pad chars in this case
                case 2: output += "=="; break; // Two pad chars
                case 3: output += "="; break; // One pad char
                default:
                    throw new ArgumentException("Invalid base64url string");
            }

            var converted = Convert.FromBase64String(output);
            return Encoding.UTF8.GetString(converted);
        }
    }

    /// <summary>
    /// Represents the claims extracted from a JWT token
    /// </summary>
    public class JwtTokenClaims
    {
        public string PlayerId { get; set; }
        public string GameId { get; set; }
        public string Email { get; set; }
        public string Username { get; set; }
        public string Role { get; set; }
        public DateTime? ExpirationTime { get; set; }
        public DateTime? IssuedAt { get; set; }
        public string Issuer { get; set; }
        public string Audience { get; set; }
        public Dictionary<string, object> RawClaims { get; set; }
    }
}
