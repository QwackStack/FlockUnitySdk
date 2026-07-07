using Flock.Http;
using Flock.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Flock.Tests
{
    // Locks the new auth wire surface: endpoint path strings (incl. builder escaping) and the
    // request/response JSON contracts for password reset, email verification, revoke and name-available.
    public class FlockAuthEndpointsTests
    {
        // Endpoint constants — a rename or typo here silently changes the wire path.

        [Test]
        public void AuthEndpoints_MatchWirePaths()
        {
            Assert.AreEqual("player/token/refresh", FlockEndpoints.PlayerTokenRefresh);
            Assert.AreEqual("player/token/revoke", FlockEndpoints.PlayerTokenRevoke);
            Assert.AreEqual("player/password/forgot", FlockEndpoints.PlayerPasswordForgot);
            Assert.AreEqual("player/password/reset", FlockEndpoints.PlayerPasswordReset);
            Assert.AreEqual("player/email/send-verification", FlockEndpoints.PlayerEmailSendVerification);
            Assert.AreEqual("player/email/verify", FlockEndpoints.PlayerEmailVerify);
            Assert.AreEqual("player/login", FlockEndpoints.PlayerLogin);
            Assert.AreEqual("player/register", FlockEndpoints.PlayerRegister);
        }

        [Test]
        public void CommandEndpoints_MatchOfflineReplayPaths()
        {
            // These strings are persisted in the offline write queue — changing them orphans queued replays.
            Assert.AreEqual("game_command/update_player_data", FlockEndpoints.CommandUpdatePlayerData);
            Assert.AreEqual("game_command/update_player_data_key", FlockEndpoints.CommandUpdatePlayerDataKey);
            Assert.AreEqual("game_command/unlock_achievement", FlockEndpoints.CommandUnlockAchievement);
            Assert.AreEqual("game_command/add_game_funds", FlockEndpoints.CommandAddGameFunds);
        }

        [Test]
        public void NameAvailable_EscapesQueryValue()
        {
            Assert.AreEqual("player/name-available?name=plain", FlockEndpoints.PlayerNameAvailable("plain"));
            Assert.AreEqual("player/name-available?name=a%20b", FlockEndpoints.PlayerNameAvailable("a b"));
            Assert.AreEqual("player/name-available?name=a%26b%3Dc", FlockEndpoints.PlayerNameAvailable("a&b=c"));
        }

        // Request models — property names must match the backend schema exactly.

        [Test]
        public void PasswordForgotRequest_SerializesWireNames()
        {
            JObject json = JObject.FromObject(new PlayerPasswordForgotRequest { Email = "p@x.com" });
            Assert.AreEqual("p@x.com", (string)json["email"]);
        }

        [Test]
        public void PasswordResetRequest_SerializesWireNames()
        {
            JObject json = JObject.FromObject(new PlayerPasswordResetRequest
            {
                Email = "p@x.com", Code = "123456", NewPassword = "hunter2!"
            });
            Assert.AreEqual("p@x.com", (string)json["email"]);
            Assert.AreEqual("123456", (string)json["code"]);
            Assert.AreEqual("hunter2!", (string)json["new_password"]);
        }

        [Test]
        public void EmailVerifyRequest_SerializesWireNames()
        {
            JObject json = JObject.FromObject(new PlayerEmailVerifyRequest { Code = "654321" });
            Assert.AreEqual("654321", (string)json["code"]);
        }

        // Response models — must deserialize the documented 200 bodies.

        [Test]
        public void AuthActionResponse_Deserializes()
        {
            PlayerAuthActionResponse response = JsonConvert.DeserializeObject<PlayerAuthActionResponse>("{\"success\":true}");
            Assert.IsTrue(response.Success);
        }

        [Test]
        public void TokenRevokeResponse_Deserializes()
        {
            PlayerTokenRevokeResponse response = JsonConvert.DeserializeObject<PlayerTokenRevokeResponse>("{\"revoked\":true}");
            Assert.IsTrue(response.Revoked);
        }

        [Test]
        public void NameAvailableResponse_Deserializes()
        {
            PlayerNameAvailableResponse response =
                JsonConvert.DeserializeObject<PlayerNameAvailableResponse>("{\"name\":\"duck\",\"available\":false}");
            Assert.AreEqual("duck", response.Name);
            Assert.IsFalse(response.Available);
        }
    }
}
