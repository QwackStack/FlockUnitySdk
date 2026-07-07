using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Analytics;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    // Behavioral coverage for the auth feature through the real FlockClient + FlockAuthProvider,
    // driven by a scripted fake transport: login/register/logout/restore lifecycle (including the
    // login -> data -> logout -> re-login flow), silent 401 refresh, and the guard rails on the
    // password-reset / email-verification / revoke / name-available methods. Hermetic — no backend.
    public class FlockAuthProviderTests
    {
        private const string PrefAccessToken = "Flock.AccessToken";   // OtherTokenStore (editor store)
        private const string PrefRefreshToken = "Flock.RefreshToken";
        private const string PrefAuthMethod = "flock_auth_method";    // FlockAuthProvider persisted login method

        // Scripted transport: routes each request through the test's responder and records
        // everything sent so tests can assert on URLs, Authorization headers, and bodies.
        private sealed class ScriptedAdapter : IFlockHttpAdapter
        {
            public readonly List<FlockHttpRequest> Requests = new List<FlockHttpRequest>();
            private readonly Func<FlockHttpRequest, FlockHttpResponse> _respond;
            public ScriptedAdapter(Func<FlockHttpRequest, FlockHttpResponse> respond) { _respond = respond; }

            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(_respond(request));
            }
        }

        [SetUp]
        public void SetUp()
        {
            ClearAuthPrefs();
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ClearAuthPrefs();
            // Restore the real platform transport so a fake can't leak into the next test.
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
        }

        private static void ClearAuthPrefs()
        {
            PlayerPrefs.DeleteKey(PrefAccessToken);
            PlayerPrefs.DeleteKey(PrefRefreshToken);
            PlayerPrefs.DeleteKey(PrefAuthMethod);
        }

        // Mirrors FlockAnalyticsConsentTests: the fake adapter must be (re-)applied after Create,
        // because FlockClient's constructor rebuilds the real transport from HttpTimeout.
        private static FlockClient CreateClient(ScriptedAdapter adapter)
        {
            FlockAnalyticsConfig analyticsConfig = new FlockAnalyticsConfig
            {
                PersistSessionOnDisk = false,
                AutoStartSession = false,
                HeartbeatIntervalSeconds = 0f,
                EventBufferFlushIntervalSeconds = 0f
            };

            FlockInitConfig initConfig = new FlockInitConfig(
                "https://test.invalid", "test-key", "test-game", "1.0.0",
                analyticsConfig: analyticsConfig,
                retryPolicy: new RetryPolicy { MaxRetries = 0, InitialDelay = TimeSpan.Zero })
            {
                GameVersionId = "test-gvid",
                EnableOfflineCache = false
            };

            FlockClient client = FlockClient.Create(initConfig);
            FlockHttpClient.Configure(adapter);
            return client;
        }

        // Safe to block inline: the scripted adapter always returns completed tasks, so every await
        // resolves synchronously — and login touches SystemInfo/Time APIs that need the main thread.
        private static T Run<T>(Func<Task<T>> action) => action().GetAwaiter().GetResult();
        private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();

        // Unsigned JWT with just the claims the SDK reads; nonce keeps two tokens for the same player distinct.
        private static string MakeJwt(string playerId, int expiresInSeconds, string nonce = "0")
        {
            long exp = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds();
            string payload = $"{{\"sub\":\"{playerId}\",\"exp\":{exp},\"nonce\":\"{nonce}\"}}";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"header.{encoded}.signature";
        }

        private static FlockHttpResponse Ok(string body)
            => new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = body };

        private static FlockHttpResponse Coded(int status, string code)
            => new FlockHttpResponse
            {
                Result = FlockHttpResult.Success,
                StatusCode = status,
                Body = "{\"detail\":{\"code\":\"" + code + "\",\"message\":\"test\"}}"
            };

        private static string LoginBody(string playerId, string jwt, string refreshToken)
            => $"{{\"player_id\":\"{playerId}\",\"access_token\":\"{jwt}\",\"refresh_token\":\"{refreshToken}\"}}";

        // Answers login with the given token, everything else with 200 {} (analytics init etc.).
        private static ScriptedAdapter LoginAdapter(string jwt, string refreshToken = "refresh-1")
            => new ScriptedAdapter(request =>
                request.Url.Contains(FlockEndpoints.PlayerLogin) || request.Url.Contains(FlockEndpoints.PlayerLoginDevice)
                    ? Ok(LoginBody("player-a", jwt, refreshToken))
                    : Ok("{}"));

        // ---- Login happy path & failure shapes ----

        [Test]
        public void LoginWithEmail_Success_SetsSession_And_FiresOnAuthenticated()
        {
            string jwt = MakeJwt("player-a", 3600);
            ScriptedAdapter adapter = LoginAdapter(jwt);
            FlockClient client = CreateClient(adapter);

            FlockAuthInfo received = null;
            Action<FlockAuthInfo> handler = info => received = info;
            FlockEvents.OnAuthenticated += handler;
            try
            {
                PlayerLoginResponse response = Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));

                Assert.AreEqual(jwt, response.AccessToken);
                Assert.IsTrue(client.IsAuthenticated);
                Assert.AreEqual("player-a", client.CurrentPlayerId);
                Assert.IsNotNull(received);
                Assert.AreEqual("player-a", received.PlayerId);
                Assert.AreEqual(FlockAuthMethod.Email, received.Method);

                FlockHttpRequest login = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerLogin));
                StringAssert.Contains("\"login_type\":\"email\"", login.JsonBody);
                Assert.IsFalse(login.Headers.ContainsKey("Authorization"), "Login must be pre-auth (API key only).");
            }
            finally
            {
                FlockEvents.OnAuthenticated -= handler;
            }
        }

        [Test]
        public void Login_EmptyAccessTokenInResponse_ThrowsAuth()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok(LoginBody("player-a", "", "r")));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw")));
            Assert.IsFalse(client.IsAuthenticated);
        }

        [Test]
        public void Login_UnparseableJwt_ThrowsAuth()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok(LoginBody("player-a", "not-a-jwt", "r")));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw")));
            Assert.IsFalse(client.IsAuthenticated);
        }

        // ---- Register: already-registered swallow ----

        [Test]
        public void Register_AlreadyRegisteredCode_ReturnsNullInsteadOfThrowing()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Coded(400, "player.email_already_registered"));
            FlockClient client = CreateClient(adapter);

            PlayerLoginResponse response = Run(() => client.Authentication.RegisterWithEmailAsync("p@x.com", "pw"));
            Assert.IsNull(response);
        }

        [Test]
        public void Register_OtherCodedError_Throws()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Coded(400, "player.invalid_registration_request"));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.RegisterWithEmailAsync("p@x.com", "pw")));
        }

        // ---- Logout ----

        [Test]
        public void Logout_ClearsSession_FiresOnLoggedOut_And_ClearsPersistedState()
        {
            FlockClient client = CreateClient(LoginAdapter(MakeJwt("player-a", 3600)));
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));
            Assert.IsTrue(PlayerPrefs.HasKey(PrefAccessToken), "Login should persist tokens via the editor store.");

            int loggedOutCount = 0;
            Action handler = () => loggedOutCount++;
            FlockEvents.OnLoggedOut += handler;
            try
            {
                client.Authentication.Logout();
            }
            finally
            {
                FlockEvents.OnLoggedOut -= handler;
            }

            Assert.AreEqual(1, loggedOutCount);
            Assert.IsFalse(client.IsAuthenticated);
            Assert.IsNull(client.CurrentPlayerId);
            Assert.IsFalse(PlayerPrefs.HasKey(PrefAccessToken));
            Assert.IsFalse(PlayerPrefs.HasKey(PrefAuthMethod));
        }

        [Test]
        public void Logout_WhenSignedOut_IsSafe_And_FiresNoEvent()
        {
            FlockClient client = CreateClient(new ScriptedAdapter(request => Ok("{}")));

            int loggedOutCount = 0;
            Action handler = () => loggedOutCount++;
            FlockEvents.OnLoggedOut += handler;
            try
            {
                client.Authentication.Logout();
            }
            finally
            {
                FlockEvents.OnLoggedOut -= handler;
            }

            Assert.AreEqual(0, loggedOutCount);
        }

        // ---- The lifecycle flow: login -> get data -> logout -> re-login -> get data ----

        [Test]
        public void FullLifecycle_Login_GetData_Logout_Relogin_GetsDataWithFreshToken()
        {
            string jwtFirst = MakeJwt("player-a", 3600, "first");
            string jwtSecond = MakeJwt("player-a", 3600, "second");
            int loginCount = 0;
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                {
                    loginCount++;
                    return Ok(LoginBody("player-a", loginCount == 1 ? jwtFirst : jwtSecond, $"refresh-{loginCount}"));
                }
                if (request.Url.Contains("player_data/pd-1"))
                    return Ok("{\"result\":{\"id\":\"pd-1\",\"player_id\":\"player-a\"}}");
                return Ok("{}");
            });
            FlockClient client = CreateClient(adapter);

            // Login and read data as player-a.
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));
            PlayerData first = Run(() => client.Player.GetDataByIdAsync("pd-1"));
            Assert.AreEqual("pd-1", first.Id);
            FlockHttpRequest firstGet = adapter.Requests.Find(r => r.Url.Contains("player_data/pd-1"));
            Assert.AreEqual($"Bearer {jwtFirst}", firstGet.Headers["Authorization"]);

            // Logout: session gone locally.
            client.Authentication.Logout();
            Assert.IsFalse(client.IsAuthenticated);

            // Re-login: same player, fresh token — data reads again under the new credentials only.
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));
            Assert.AreEqual("player-a", client.CurrentPlayerId);
            PlayerData second = Run(() => client.Player.GetDataByIdAsync("pd-1"));
            Assert.AreEqual("pd-1", second.Id);

            FlockHttpRequest lastGet = adapter.Requests.FindLast(r => r.Url.Contains("player_data/pd-1"));
            Assert.AreEqual($"Bearer {jwtSecond}", lastGet.Headers["Authorization"]);
        }

        // ---- Silent 401 -> refresh -> retry plumbing ----

        [Test]
        public void DataRequest_401_SilentlyRefreshes_And_Retries()
        {
            string jwtOld = MakeJwt("player-a", 3600, "old");
            string jwtNew = MakeJwt("player-a", 3600, "new");
            int dataCalls = 0;
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", jwtOld, "refresh-1"));
                if (request.Url.Contains(FlockEndpoints.PlayerTokenRefresh))
                    return Ok(LoginBody("player-a", jwtNew, "refresh-2"));
                if (request.Url.Contains("player_data/pd-1"))
                {
                    dataCalls++;
                    return dataCalls == 1 ? Coded(401, "player.invalid_refresh_token") : Ok("{\"result\":{\"id\":\"pd-1\"}}");
                }
                return Ok("{}");
            });
            FlockClient client = CreateClient(adapter);
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));

            PlayerData data = Run(() => client.Player.GetDataByIdAsync("pd-1"));

            Assert.AreEqual("pd-1", data.Id);
            Assert.AreEqual(2, dataCalls, "Expected the 401'd request to be retried once after refresh.");
            Assert.IsTrue(adapter.Requests.Exists(r => r.Url.Contains(FlockEndpoints.PlayerTokenRefresh)));
            FlockHttpRequest retried = adapter.Requests.FindLast(r => r.Url.Contains("player_data/pd-1"));
            Assert.AreEqual($"Bearer {jwtNew}", retried.Headers["Authorization"]);
        }

        // ---- Session restore ----

        [Test]
        public void TryRestoreSession_NoStoredTokens_ReturnsFalse_And_ReportsIt()
        {
            FlockClient client = CreateClient(new ScriptedAdapter(request => Ok("{}")));

            bool? reported = null;
            Action<bool> handler = restored => reported = restored;
            FlockEvents.OnSessionRestored += handler;
            try
            {
                bool restored = Run(() => client.Authentication.TryRestoreSessionAsync());
                Assert.IsFalse(restored);
                Assert.AreEqual(false, reported);
            }
            finally
            {
                FlockEvents.OnSessionRestored -= handler;
            }
        }

        [Test]
        public void TryRestoreSession_GarbageStoredTokens_ClearsThem_And_ReturnsFalse()
        {
            PlayerPrefs.SetString(PrefAccessToken, "garbage-token");
            PlayerPrefs.SetString(PrefRefreshToken, "garbage-refresh");
            FlockClient client = CreateClient(new ScriptedAdapter(request => Ok("{}")));

            bool restored = Run(() => client.Authentication.TryRestoreSessionAsync());

            Assert.IsFalse(restored);
            Assert.IsFalse(PlayerPrefs.HasKey(PrefAccessToken), "Unusable tokens should be cleared, not kept for a retry loop.");
        }

        [Test]
        public void TryRestoreSession_ValidStoredTokens_RestoresPlayer()
        {
            PlayerPrefs.SetString(PrefAccessToken, MakeJwt("player-r", 3600));
            PlayerPrefs.SetString(PrefRefreshToken, "refresh-r");
            FlockClient client = CreateClient(new ScriptedAdapter(request => Ok("{}")));

            bool restored = Run(() => client.Authentication.TryRestoreSessionAsync());

            Assert.IsTrue(restored);
            Assert.AreEqual("player-r", client.CurrentPlayerId);
        }

        [Test]
        public void TryRestoreSession_ExpiredToken_RefreshesThenRestores()
        {
            string jwtNew = MakeJwt("player-r", 3600, "renewed");
            PlayerPrefs.SetString(PrefAccessToken, MakeJwt("player-r", -3600, "expired"));
            PlayerPrefs.SetString(PrefRefreshToken, "refresh-r");
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
                request.Url.Contains(FlockEndpoints.PlayerTokenRefresh)
                    ? Ok(LoginBody("player-r", jwtNew, "refresh-r2"))
                    : Ok("{}"));
            FlockClient client = CreateClient(adapter);

            bool restored = Run(() => client.Authentication.TryRestoreSessionAsync());

            Assert.IsTrue(restored);
            Assert.IsTrue(adapter.Requests.Exists(r => r.Url.Contains(FlockEndpoints.PlayerTokenRefresh)));
            Assert.AreEqual("player-r", client.CurrentPlayerId);
        }

        [Test]
        public void Relaunch_LoginThenRecreateClient_RestoreSucceeds()
        {
            // First "launch": login persists tokens through the real editor store.
            FlockClient first = CreateClient(LoginAdapter(MakeJwt("player-a", 3600)));
            Run(() => first.Authentication.LoginWithEmailAsync("p@x.com", "pw"));

            // A real relaunch is process death — Shutdown() never runs and tokens stay on disk.
            // Shutdown() is an explicit teardown that clears the persisted session (FlockClient.Shutdown
            // -> ClearTokens -> store.Clear), so capture and re-seed what a dead process leaves behind.
            string savedAccess = PlayerPrefs.GetString(PrefAccessToken);
            string savedRefresh = PlayerPrefs.GetString(PrefRefreshToken);
            FlockClient.Shutdown();
            PlayerPrefs.SetString(PrefAccessToken, savedAccess);
            PlayerPrefs.SetString(PrefRefreshToken, savedRefresh);

            // Second "launch": a fresh client restores the same player from disk.
            FlockClient second = CreateClient(new ScriptedAdapter(request => Ok("{}")));
            bool restored = Run(() => second.Authentication.TryRestoreSessionAsync());

            Assert.IsTrue(restored);
            Assert.AreEqual("player-a", second.CurrentPlayerId);
        }

        // ---- Password reset gating (email-login-only) ----

        [Test]
        public void ResetPassword_SignedOut_ThrowsWithoutHittingNetwork()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{}"));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123", "new-pw")));
            Assert.IsFalse(adapter.Requests.Exists(r => r.Url.Contains(FlockEndpoints.PlayerPasswordReset)));
        }

        [Test]
        public void ResetPassword_DeviceLogin_Throws_EmailGated()
        {
            FlockClient client = CreateClient(LoginAdapter(MakeJwt("player-a", 3600)));
            Run(() => client.Authentication.LoginWithDeviceAsync("device-1"));

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123", "new-pw")));
        }

        [Test]
        public void ResetPassword_EmailLogin_Succeeds()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
                request.Url.Contains(FlockEndpoints.PlayerLogin)
                    ? Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "r1"))
                    : Ok("{\"success\":true}"));
            FlockClient client = CreateClient(adapter);
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));

            Assert.DoesNotThrow(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
            FlockHttpRequest reset = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerPasswordReset));
            StringAssert.Contains("\"new_password\":\"new-pw\"", reset.JsonBody);
        }

        [Test]
        public void ResetPassword_RestoredEmailSession_StillCounts()
        {
            // A restored session carries the persisted login method forward — email-gated flows keep working.
            PlayerPrefs.SetString(PrefAccessToken, MakeJwt("player-a", 3600));
            PlayerPrefs.SetString(PrefRefreshToken, "refresh-a");
            PlayerPrefs.SetString(PrefAuthMethod, FlockAuthMethod.Email.ToString());
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{\"success\":true}"));
            FlockClient client = CreateClient(adapter);
            Assert.IsTrue(Run(() => client.Authentication.TryRestoreSessionAsync()));

            Assert.DoesNotThrow(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }

        // ---- Revoke ----

        [Test]
        public void RevokeToken_SignedOut_FailsFast_WithoutHittingNetwork()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{}"));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.RevokeTokenAsync()));
            Assert.IsFalse(adapter.Requests.Exists(r => r.Url.Contains(FlockEndpoints.PlayerTokenRevoke)));
        }

        [Test]
        public void RevokeToken_NotConfirmed_Throws()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
                request.Url.Contains(FlockEndpoints.PlayerLogin)
                    ? Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "r1"))
                    : Ok("{\"revoked\":false}"));
            FlockClient client = CreateClient(adapter);
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.RevokeTokenAsync()));
        }

        [Test]
        public void RevokeToken_Confirmed_SendsBearer_And_KeepsLocalSession()
        {
            string jwt = MakeJwt("player-a", 3600);
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
                request.Url.Contains(FlockEndpoints.PlayerLogin)
                    ? Ok(LoginBody("player-a", jwt, "r1"))
                    : Ok("{\"revoked\":true}"));
            FlockClient client = CreateClient(adapter);
            Run(() => client.Authentication.LoginWithEmailAsync("p@x.com", "pw"));

            Run(() => client.Authentication.RevokeTokenAsync());

            FlockHttpRequest revoke = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerTokenRevoke));
            Assert.AreEqual($"Bearer {jwt}", revoke.Headers["Authorization"]);
            Assert.IsTrue(client.IsAuthenticated, "Revoke is server-side only; local session is Logout()'s job.");
        }

        // ---- Forgot password / verify email / name availability ----

        [Test]
        public void ForgotPassword_WorksSignedOut_And_ReturnsSuccessFlag()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{\"success\":true}"));
            FlockClient client = CreateClient(adapter);

            bool success = Run(() => client.Authentication.ForgotPasswordAsync("p@x.com"));

            Assert.IsTrue(success);
            FlockHttpRequest forgot = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerPasswordForgot));
            StringAssert.Contains("\"email\":\"p@x.com\"", forgot.JsonBody);
        }

        [Test]
        public void EmailVerification_NoClientAuthGuard_WorksSignedOut()
        {
            // Intentional (decisions.md §2): the spec marks Authorization optional on both routes,
            // so the SDK sends them signed-out too instead of guarding stricter than the wire contract.
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{\"success\":true}"));
            FlockClient client = CreateClient(adapter);

            Assert.DoesNotThrow(() => Run(() => client.Authentication.SendEmailVerificationAsync()));
            Assert.DoesNotThrow(() => Run(() => client.Authentication.VerifyEmailAsync("123456")));

            FlockHttpRequest send = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerEmailSendVerification));
            FlockHttpRequest verify = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerEmailVerify));
            Assert.IsNotNull(send);
            Assert.IsNotNull(verify);
            Assert.IsFalse(send.Headers.ContainsKey("Authorization"));
            Assert.IsFalse(verify.Headers.ContainsKey("Authorization"));
        }

        [Test]
        public void EmptyArguments_ThrowValidation_WithoutHittingNetwork()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{}"));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.ForgotPasswordAsync("")));
            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.VerifyEmailAsync("")));
            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.IsNameAvailableAsync("")));
            Assert.AreEqual(0, adapter.Requests.Count);
        }

        [Test]
        public void IsNameAvailable_ReturnsFlag_And_EscapesName()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request => Ok("{\"name\":\"duck x\",\"available\":false}"));
            FlockClient client = CreateClient(adapter);

            bool available = Run(() => client.Authentication.IsNameAvailableAsync("duck x"));

            Assert.IsFalse(available);
            StringAssert.Contains("player/name-available?name=duck%20x", adapter.Requests[0].Url);
        }

        // ---- Facebook / Discord ride the generic login route with assumed type literals ----

        [Test]
        public void FacebookAndDiscordLogins_SendGenericRoute_WithTypeLiterals()
        {
            ScriptedAdapter adapter = LoginAdapter(MakeJwt("player-a", 3600));
            FlockClient client = CreateClient(adapter);

            Run(() => client.Authentication.LoginWithFacebookAsync("fb-1"));
            FlockHttpRequest facebook = adapter.Requests.FindLast(r => r.Url.Contains(FlockEndpoints.PlayerLogin));
            StringAssert.Contains("\"login_type\":\"facebook\"", facebook.JsonBody);
            StringAssert.Contains("\"facebook_id\":\"fb-1\"", facebook.JsonBody);

            Run(() => client.Authentication.LoginWithDiscordAsync("dc-1"));
            FlockHttpRequest discord = adapter.Requests.FindLast(r => r.Url.Contains(FlockEndpoints.PlayerLogin));
            StringAssert.Contains("\"login_type\":\"discord\"", discord.JsonBody);
            StringAssert.Contains("\"discord_id\":\"dc-1\"", discord.JsonBody);
        }
    }
}
