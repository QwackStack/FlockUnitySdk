using Flock.Logging;
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
    // Behavioral coverage for account linking through the real FlockClient + FlockAuthProvider,
    // driven by a scripted fake transport. Asserts the outgoing request SHAPE (route segments and
    // exact snake_case body fields), the root — not enveloped — response contract, the bearer-only
    // guards, the linked-account events, and how a linked email credential opens the password-reset
    // gate. Hermetic — no backend.
    public class FlockAccountLinkingTests
    {
        private const string PrefAccessToken = "Flock.AccessToken";
        private const string PrefRefreshToken = "Flock.RefreshToken";
        private const string PrefAuthMethod = "flock_auth_method";

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
            // Never assume a clean singleton. Create throws if anything before this test left one alive —
            // another test class, or an editor that entered play mode — and cleaning up only in TearDown
            // makes the first test of the class depend on whatever ran before it.
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ClearAuthPrefs();
        }

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            ClearAuthPrefs();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
        }

        private static void ClearAuthPrefs()
        {
            PlayerPrefs.DeleteKey(PrefAccessToken);
            PlayerPrefs.DeleteKey(PrefRefreshToken);
            PlayerPrefs.DeleteKey(PrefAuthMethod);
        }

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

            FlockClient client = FlockClient.Create(initConfig, new NullFlockLogger());
            FlockHttpClient.Configure(adapter);
            return client;
        }

        private static T Run<T>(Func<Task<T>> action) => action().GetAwaiter().GetResult();
        private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();

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

        // Root-shaped payload: these routes return the model directly, with no {error,response,result} envelope.
        private static string AccountsBody(params string[] providers)
        {
            List<string> entries = new List<string>();
            foreach (string provider in providers)
                entries.Add($"{{\"provider\":\"{provider}\",\"provider_user_id\":\"uid-{provider}\",\"email\":null,\"email_verified\":false}}");
            return "{\"accounts\":[" + string.Join(",", entries) + "]}";
        }

        private static bool IsAccountRoute(string url)
            => url.Contains(FlockEndpoints.PlayerAccounts)
               || url.Contains(FlockEndpoints.PlayerLinkEmail)
               || url.Contains(FlockEndpoints.PlayerLinkDevice)
               || url.Contains("/link/oauth/")
               || url.Contains("/unlink/");

        // Answers login with a real token and every linking route with `accountsBody`; anything else 200 {}.
        private static ScriptedAdapter LinkAdapter(string accountsBody)
            => new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "refresh-1"));
                if (IsAccountRoute(request.Url))
                    return Ok(accountsBody);
                return Ok("{}");
            });

        private static FlockClient SignedInClient(ScriptedAdapter adapter)
        {
            FlockClient client = CreateClient(adapter);
            Run(() => client.Authentication.LoginWithDeviceAsync("device-1"));
            return client;
        }

        private static Task<List<PlayerLinkedAccount>> LinkOAuth(FlockClient client, FlockCredentialProvider provider, string token)
        {
            switch (provider)
            {
                case FlockCredentialProvider.Google: return client.Authentication.LinkGoogleAsync(token);
                case FlockCredentialProvider.Apple: return client.Authentication.LinkAppleAsync(token);
                case FlockCredentialProvider.Steam: return client.Authentication.LinkSteamAsync(token);
                case FlockCredentialProvider.Facebook: return client.Authentication.LinkFacebookAsync(token);
                default: return client.Authentication.LinkDiscordAsync(token);
            }
        }

        // ---- Response contract: root shape, not the GenericResponse envelope ----

        [Test]
        public void GetLinkedAccounts_ParsesRootShape()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "google"));
            FlockClient client = SignedInClient(adapter);

            List<PlayerLinkedAccount> accounts = Run(() => client.Authentication.GetLinkedAccountsAsync());

            Assert.AreEqual(2, accounts.Count);
            Assert.AreEqual("device_id", accounts[0].Provider);
            Assert.AreEqual(FlockCredentialProvider.DeviceId, accounts[0].ProviderType);
            Assert.AreEqual(FlockCredentialProvider.Google, accounts[1].ProviderType);
            Assert.AreEqual("uid-google", accounts[1].ProviderUserId);

            FlockHttpRequest listed = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerAccounts));
            Assert.AreEqual("GET", listed.Method);
        }

        [Test]
        public void GetLinkedAccounts_EnvelopedBody_Throws()
        {
            // Guards the wire asymmetry: if someone re-wraps these routes in GenericResponse<T>,
            // an enveloped payload would start passing here and fail only against a real backend.
            ScriptedAdapter adapter = LinkAdapter("{\"error\":null,\"response\":\"ok\",\"result\":{\"accounts\":[]}}");
            FlockClient client = SignedInClient(adapter);

            Assert.Throws<FlockNetworkException>(() => Run(() => client.Authentication.GetLinkedAccountsAsync()));
        }

        [Test]
        public void UnknownProvider_InResponse_ParsesAsUnknown()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("nintendo"));
            FlockClient client = SignedInClient(adapter);

            List<PlayerLinkedAccount> accounts = Run(() => client.Authentication.GetLinkedAccountsAsync());

            Assert.AreEqual(FlockCredentialProvider.Unknown, accounts[0].ProviderType);
            Assert.AreEqual("nintendo", accounts[0].Provider);
        }

        // ---- Request shape ----

        [Test]
        public void LinkEmail_PostsEmailAndPassword()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "email"));
            FlockClient client = SignedInClient(adapter);

            Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));

            FlockHttpRequest link = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerLinkEmail));
            Assert.AreEqual("POST", link.Method);
            StringAssert.Contains("\"email\":\"p@x.com\"", link.JsonBody);
            StringAssert.Contains("\"password\":\"pw\"", link.JsonBody);
        }

        [Test]
        public void LinkDevice_PostsSnakeCaseDeviceFields()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("email", "device_id"));
            FlockClient client = SignedInClient(adapter);

            Run(() => client.Authentication.LinkDeviceAsync("device-2"));

            FlockHttpRequest link = adapter.Requests.Find(r => r.Url.Contains(FlockEndpoints.PlayerLinkDevice));
            StringAssert.Contains("\"device_id\":\"device-2\"", link.JsonBody);
            StringAssert.Contains($"\"device_type\":\"{SystemInfo.deviceType}\"", link.JsonBody);
        }

        [TestCase(FlockCredentialProvider.Google, "google")]
        [TestCase(FlockCredentialProvider.Apple, "apple")]
        [TestCase(FlockCredentialProvider.Steam, "steam")]
        [TestCase(FlockCredentialProvider.Facebook, "facebook")]
        [TestCase(FlockCredentialProvider.Discord, "discord")]
        public void LinkOAuth_PostsBareToken_ToProviderRoute(FlockCredentialProvider provider, string wire)
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", wire));
            FlockClient client = SignedInClient(adapter);

            Run(() => LinkOAuth(client, provider, "tok-1"));

            FlockHttpRequest link = adapter.Requests.Find(r => r.Url.Contains($"/link/oauth/{wire}"));
            Assert.IsNotNull(link, $"Expected a POST to player/link/oauth/{wire}");
            Assert.AreEqual("POST", link.Method);
            // The link routes take a bare `token` for every provider — not the login routes' id_token/identity_token/session_ticket.
            StringAssert.Contains("\"token\":\"tok-1\"", link.JsonBody);
            StringAssert.DoesNotContain("id_token", link.JsonBody);
            StringAssert.DoesNotContain("session_ticket", link.JsonBody);
        }

        [TestCase(FlockCredentialProvider.DeviceId, "device_id")]
        [TestCase(FlockCredentialProvider.Email, "email")]
        [TestCase(FlockCredentialProvider.Google, "google")]
        public void Unlink_UsesWireProviderSegment(FlockCredentialProvider provider, string wire)
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("steam"));
            FlockClient client = SignedInClient(adapter);

            Run(() => client.Authentication.UnlinkAsync(provider));

            FlockHttpRequest unlink = adapter.Requests.Find(r => r.Url.Contains($"/unlink/{wire}"));
            Assert.IsNotNull(unlink, $"Expected a POST to player/unlink/{wire}");
            Assert.AreEqual("POST", unlink.Method);
        }

        [Test]
        public void Unlink_UnknownProvider_ThrowsWithoutHittingNetwork()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("email"));
            FlockClient client = SignedInClient(adapter);
            int before = adapter.Requests.Count;

            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.UnlinkAsync(FlockCredentialProvider.Unknown)));
            Assert.AreEqual(before, adapter.Requests.Count);
        }

        // ---- Bearer-only guards ----

        [Test]
        public void LinkingMethods_SignedOut_ThrowWithoutHittingNetwork()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("email"));
            FlockClient client = CreateClient(adapter);

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.GetLinkedAccountsAsync()));
            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw")));
            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.LinkDeviceAsync("device-1")));
            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.LinkGoogleAsync("tok")));
            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.UnlinkAsync(FlockCredentialProvider.Google)));

            Assert.IsFalse(adapter.Requests.Exists(r => IsAccountRoute(r.Url)));
        }

        [Test]
        public void LinkEmail_EmptyArguments_ThrowWithoutHittingNetwork()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id"));
            FlockClient client = SignedInClient(adapter);

            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.LinkEmailAsync("", "pw")));
            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.LinkEmailAsync("p@x.com", "")));
            Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.LinkGoogleAsync("")));

            Assert.IsFalse(adapter.Requests.Exists(r => IsAccountRoute(r.Url)));
        }

        // ---- Events ----

        [Test]
        public void LinkEmail_FiresOnAccountLinked()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "email"));
            FlockClient client = SignedInClient(adapter);

            FlockCredentialProvider? linked = null;
            Action<FlockCredentialProvider> handler = p => linked = p;
            FlockEvents.OnAccountLinked += handler;
            try
            {
                Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));
                Assert.AreEqual(FlockCredentialProvider.Email, linked);
            }
            finally
            {
                FlockEvents.OnAccountLinked -= handler;
            }
        }

        [Test]
        public void Unlink_FiresOnAccountUnlinked()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id"));
            FlockClient client = SignedInClient(adapter);

            FlockCredentialProvider? unlinked = null;
            Action<FlockCredentialProvider> handler = p => unlinked = p;
            FlockEvents.OnAccountUnlinked += handler;
            try
            {
                Run(() => client.Authentication.UnlinkAsync(FlockCredentialProvider.Google));
                Assert.AreEqual(FlockCredentialProvider.Google, unlinked);
            }
            finally
            {
                FlockEvents.OnAccountUnlinked -= handler;
            }
        }

        [Test]
        public void FailedLink_DoesNotFireOnAccountLinked()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "refresh-1"));
                if (IsAccountRoute(request.Url))
                    return Coded(409, "player.account_already_linked");
                return Ok("{}");
            });
            FlockClient client = SignedInClient(adapter);

            bool fired = false;
            Action<FlockCredentialProvider> handler = p => fired = true;
            FlockEvents.OnAccountLinked += handler;
            try
            {
                Assert.Throws<FlockNetworkException>(() => Run(() => client.Authentication.LinkGoogleAsync("tok")));
                Assert.IsFalse(fired);
            }
            finally
            {
                FlockEvents.OnAccountLinked -= handler;
            }
        }

        // ---- Coded errors ----

        [Test]
        public void Link_AlreadyLinked_SurfacesCodedError()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "refresh-1"));
                if (IsAccountRoute(request.Url))
                    return Coded(409, "player.account_already_linked");
                return Ok("{}");
            });
            FlockClient client = SignedInClient(adapter);

            FlockException ex = Assert.Throws<FlockNetworkException>(() => Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw")));
            Assert.AreEqual(FlockErrorCode.PlayerAccountAlreadyLinked, ex.ErrorCode);
        }

        [Test]
        public void Unlink_LastCredential_SurfacesCodedError()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "refresh-1"));
                if (IsAccountRoute(request.Url))
                    return Coded(400, "player.cannot_unlink_last_credential");
                return Ok("{}");
            });
            FlockClient client = SignedInClient(adapter);

            FlockException ex = Assert.Throws<FlockValidationException>(() => Run(() => client.Authentication.UnlinkAsync(FlockCredentialProvider.DeviceId)));
            Assert.AreEqual(FlockErrorCode.PlayerCannotUnlinkLastCredential, ex.ErrorCode);
        }

        [Test]
        public void Unlink_NotLinked_SurfacesCodedError()
        {
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "refresh-1"));
                if (IsAccountRoute(request.Url))
                    return Coded(404, "player.account_not_linked");
                return Ok("{}");
            });
            FlockClient client = SignedInClient(adapter);

            FlockException ex = Assert.Throws<FlockNetworkException>(() => Run(() => client.Authentication.UnlinkAsync(FlockCredentialProvider.Google)));
            Assert.AreEqual(FlockErrorCode.PlayerAccountNotLinked, ex.ErrorCode);
        }

        // ---- Password-reset gate widening ----

        [Test]
        public void LinkEmail_UnblocksPasswordReset_OnDeviceSession()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "email"));
            FlockClient client = SignedInClient(adapter);

            // Device login alone leaves the email-only flow gated.
            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));

            Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));

            Assert.DoesNotThrow(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }

        [Test]
        public void GetLinkedAccounts_WithoutEmail_KeepsPasswordResetGated()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "google"));
            FlockClient client = SignedInClient(adapter);

            Run(() => client.Authentication.GetLinkedAccountsAsync());

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }

        [Test]
        public void UnlinkEmail_ReclosesPasswordResetGate()
        {
            // The server hands back the post-unlink list, so the flag re-derives itself on every call.
            int calls = 0;
            ScriptedAdapter adapter = new ScriptedAdapter(request =>
            {
                if (request.Url.Contains(FlockEndpoints.PlayerLogin))
                    return Ok(LoginBody("player-a", MakeJwt("player-a", 3600), "refresh-1"));
                if (IsAccountRoute(request.Url))
                {
                    calls++;
                    return Ok(calls == 1 ? AccountsBody("device_id", "email") : AccountsBody("device_id"));
                }
                return Ok("{}");
            });
            FlockClient client = SignedInClient(adapter);

            Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));
            Assert.DoesNotThrow(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));

            Run(() => client.Authentication.UnlinkAsync(FlockCredentialProvider.Email));
            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }

        [Test]
        public void PlayerSwitchWithoutLogout_ClearsLinkedEmailGate()
        {
            // Signing in as someone else must not carry the previous player's linked-email answer over,
            // even when the game never called Logout() in between.
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "email"));
            FlockClient client = SignedInClient(adapter);
            Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));
            Assert.DoesNotThrow(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));

            Run(() => client.Authentication.LoginWithDeviceAsync("device-2"));

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }

        [Test]
        public void SessionRestore_ClearsLinkedEmailGate()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "email"));
            FlockClient client = SignedInClient(adapter);
            Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));

            // A restore re-establishes the session from disk, where the device method was persisted —
            // the flag is not persisted alongside it, so it must not survive.
            Assert.IsTrue(Run(() => client.Authentication.TryRestoreSessionAsync()));

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }

        [Test]
        public void Logout_ClearsLinkedEmailGate()
        {
            ScriptedAdapter adapter = LinkAdapter(AccountsBody("device_id", "email"));
            FlockClient client = SignedInClient(adapter);
            Run(() => client.Authentication.LinkEmailAsync("p@x.com", "pw"));

            client.Authentication.Logout();
            Run(() => client.Authentication.LoginWithDeviceAsync("device-1"));

            Assert.Throws<FlockAuthException>(() => Run(() => client.Authentication.ResetPasswordAsync("p@x.com", "123456", "new-pw")));
        }
    }
}
