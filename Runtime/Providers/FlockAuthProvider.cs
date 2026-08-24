using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Auth;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    public class FlockAuthProvider : FlockProviderBase
    {
        private const string PrefKeyAuthMethod = "flock_auth_method";

        // How the current session was established; null when signed out. Gates email-only flows like ResetPasswordAsync.
        private FlockAuthMethod? _currentAuthMethod;

        // Best-effort: true once a linked-accounts read or an email link proves the account has an email credential. Session-scoped, never persisted — false after a restore until the caller reads accounts.
        private bool _hasEmailCredential;

        public FlockAuthProvider(FlockClient client) : base(client)
        {
        }

        private async Task<PlayerLoginResponse> ExecuteAuthAsync(
            Func<Task<PlayerLoginResponse>> operation, string context, FlockAuthMethod method,
            CancellationToken cancellationToken)
        {
            Client.Logger.LogInfo($"{context} starting...");
            try
            {
                PlayerLoginResponse response = await Client.RetryHandler.ExecuteAsync(operation, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                    throw new FlockAuthException($"Invalid {context.ToLower()} response from server");

                Client.SetTokens(response.AccessToken, response.RefreshToken);
                _currentAuthMethod = method;
                // A new session knows nothing about linked credentials yet — never carry the previous player's answer over.
                _hasEmailCredential = false;
                PersistAuthMethod(method);
                Client.Logger.LogInfo($"{context} successful for player: {Client.CurrentPlayerId}");

                FlockEvents.InvokeAuthenticated(new FlockAuthInfo(Client.CurrentPlayerId, method));

                await TryInitializeAnalyticsAsync(cancellationToken);

                return response;
            }
            catch (FlockException ex)
            {
                // The credential decides what a login failure means, so refine the HTTP layer's context-free hint here.
                if (string.IsNullOrEmpty(ex.Operation))
                    ex.Operation = context;
                ex.Hint = FlockErrorHints.ForAuth(ex.ErrorCode, method) ?? ex.Hint;
                throw;
            }
            catch (Exception ex)
            {
                Client.Logger.LogError($"{context} failed", ex);
                throw new FlockAuthException($"{context} failed", ex);
            }
        }


        private async Task<PlayerLoginResponse> ExecuteRegistrationAsync(
            Func<Task<PlayerLoginResponse>> operation, string context, FlockAuthMethod method,
            CancellationToken cancellationToken)
        {
            try
            {
                return await ExecuteAuthAsync(operation, context, method, cancellationToken);
            }
            catch (FlockException ex) when (IsAlreadyRegisteredError(ex))
            {
                Client.Logger.LogWarning($"{context} skipped: player already registered.");
                return null;
            }
        }

        // The register routes' "already registered" codes, one per auth method.
        private static bool IsAlreadyRegisteredError(Exception ex) => (ex as FlockException)?.IsAlreadyRegistered() ?? false;
        private async Task TryInitializeAnalyticsAsync(CancellationToken cancellationToken)
        {
#if !FLOCK_NO_ANALYTICS
            if (Client.Analytics == null) return;
            try
            {
                await Client.Analytics.InitializeAsync(cancellationToken);
            }
            catch (Exception analyticsEx)
            {
                Client.Logger.LogWarning($"Analytics initialization failed (non-fatal): {analyticsEx.Message}");
            }
#else
            await Task.CompletedTask;
#endif
        }

        /// <summary>Resumes a persisted session (refreshing an expired token), toggling <see cref="FlockClient.IsRestoringSession"/> and raising <see cref="FlockEvents.OnSessionRestored"/>. Returns true if signed in afterward.</summary>
        public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
        {
            FlockClient.IsRestoringSession = true;
            bool restored = false;
            try
            {
                restored = await RestoreSessionCoreAsync(cancellationToken);
            }
            finally
            {
                FlockClient.IsRestoringSession = false;
                FlockEvents.InvokeSessionRestored(restored);
            }
            return restored;
        }

        private async Task<bool> RestoreSessionCoreAsync(CancellationToken cancellationToken)
        {
            StoredTokens stored = Client.LoadPersistedTokens();
            if (stored == null || string.IsNullOrEmpty(stored.AccessToken))
                return false;

            try
            {
                Client.SetTokens(stored.AccessToken, stored.RefreshToken);
            }
            catch (FlockAuthException ex)
            {
                Client.Logger.LogWarning($"Stored tokens unusable, clearing them: {ex.Message}");
                Client.ClearTokens();
                return false;
            }

            if (Client.IsTokenExpired)
            {
                bool refreshed = await Client.TryRefreshTokenAsync(cancellationToken);
                if (!refreshed) return false;
            }

            Client.Logger.LogInfo($"Restored session for PlayerId: {Client.CurrentPlayerId}");
            // Carry the original login method forward so email-gated flows keep working after a restore.
            _currentAuthMethod = LoadPersistedAuthMethod() ?? FlockAuthMethod.SessionRestore;
            _hasEmailCredential = false;
            FlockEvents.InvokeAuthenticated(new FlockAuthInfo(Client.CurrentPlayerId, FlockAuthMethod.SessionRestore));
            await TryInitializeAnalyticsAsync(cancellationToken);
            return true;
        }

        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLogin}",
                    new PlayerLoginRequest { LoginType = "email", Email = email, Password = password },
                    Client.GetBaseHeaders(), cancellationToken),
                "Email login", FlockAuthMethod.Email, cancellationToken);
        }

        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLoginDevice}",
                    new PlayerDeviceLoginRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId },
                    Client.GetBaseHeaders(), cancellationToken),
                "Device login", FlockAuthMethod.Device, cancellationToken);
        }

        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerRegister}",
                    new PlayerEmailRegistrationRequest { Email = email, Password = password, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Email registration", FlockAuthMethod.Email, cancellationToken);
        }

        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceId, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerRegisterDevice}",
                    new PlayerDeviceRegistrationRequest
                        { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Device registration", FlockAuthMethod.Device, cancellationToken);
        }
        public async Task<PlayerLoginResponse> LoginWithGoogleAsync(string idToken,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLoginGoogle}",
                    new PlayerGoogleLoginRequest { IdToken = idToken },
                    Client.GetBaseHeaders(), cancellationToken),
                "Google login", FlockAuthMethod.Google, cancellationToken);
        }
        
        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithGoogleAsync(string idToken, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerRegisterGoogle}",
                    new PlayerGoogleRegistrationRequest { IdToken = idToken, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Google registration", FlockAuthMethod.Google, cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> LoginWithAppleAsync(string identityToken,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLoginApple}",
                    new PlayerAppleLoginRequest { IdentityToken = identityToken },
                    Client.GetBaseHeaders(), cancellationToken),
                "Apple login", FlockAuthMethod.Apple, cancellationToken);
        }
        
        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithAppleAsync(string identityToken, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerRegisterApple}",
                    new PlayerAppleRegistrationRequest { IdentityToken = identityToken, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Apple registration", FlockAuthMethod.Apple, cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> LoginWithSteamAsync(string sessionTicket,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLoginSteam}",
                    new PlayerSteamLoginRequest { SessionTicket = sessionTicket },
                    Client.GetBaseHeaders(), cancellationToken),
                "Steam login", FlockAuthMethod.Steam, cancellationToken);
        }
        
        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithSteamAsync(string sessionTicket, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>($"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerRegisterSteam}",
                    new PlayerSteamRegistrationRequest { SessionTicket = sessionTicket, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Steam registration", FlockAuthMethod.Steam, cancellationToken);
        }

        // Facebook/Discord have no dedicated route — post to the generic /player/login with the provider id (login only; no server-side register route).
        public async Task<PlayerLoginResponse> LoginWithFacebookAsync(string facebookId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLogin}",
                    new PlayerLoginRequest { LoginType = "facebook", FacebookId = facebookId },
                    Client.GetBaseHeaders(), cancellationToken),
                "Facebook login", FlockAuthMethod.Facebook, cancellationToken);
        }

        public async Task<PlayerLoginResponse> LoginWithDiscordAsync(string discordId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerLogin}",
                    new PlayerLoginRequest { LoginType = "discord", DiscordId = discordId },
                    Client.GetBaseHeaders(), cancellationToken),
                "Discord login", FlockAuthMethod.Discord, cancellationToken);
        }

        /// <summary>Emails a password-reset code. The backend always reports success (it never reveals whether the email exists).</summary>
        public async Task<bool> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(email, nameof(email));
            PlayerAuthActionResponse response = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerAuthActionResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerPasswordForgot}",
                    new PlayerPasswordForgotRequest { Email = email },
                    Client.GetBaseHeaders(), cancellationToken),
                "Password forgot", cancellationToken, idempotent: false);
            if (response == null)
                throw new FlockNetworkException("Invalid response from server");
            return response.Success;
        }

        /// <summary>Sets a new password using the code emailed by <see cref="ForgotPasswordAsync"/>. Requires being signed in with email (restored email sessions count); throws on a bad or expired code.</summary>
        public async Task ResetPasswordAsync(string email, string code, string newPassword, CancellationToken cancellationToken = default)
        {
            RequireEmailLogin();
            RequireNotEmpty(email, nameof(email));
            RequireNotEmpty(code, nameof(code));
            RequireNotEmpty(newPassword, nameof(newPassword));
            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerAuthActionResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerPasswordReset}",
                    new PlayerPasswordResetRequest { Email = email, Code = code, NewPassword = newPassword },
                    Client.GetBaseHeaders(), cancellationToken),
                "Password reset", cancellationToken, idempotent: false);
        }

        /// <summary>Emails a verification code to the player's address. No sign-in guard — the bearer token rides along automatically when present.</summary>
        public async Task SendEmailVerificationAsync(CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerAuthActionResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerEmailSendVerification}",
                    new object(),
                    Client.GetBaseHeaders(), cancellationToken),
                "Send email verification", cancellationToken, idempotent: false);
        }

        /// <summary>Marks the player's email verified using the code from <see cref="SendEmailVerificationAsync"/>; throws on a bad or expired code.</summary>
        public async Task VerifyEmailAsync(string code, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(code, nameof(code));
            await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerAuthActionResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerEmailVerify}",
                    new PlayerEmailVerifyRequest { Code = code },
                    Client.GetBaseHeaders(), cancellationToken),
                "Verify email", cancellationToken, idempotent: false);
        }

        /// <summary>Revokes the signed-in player's refresh token server-side (logout hardening / killing a stolen token).
        /// Issued access tokens live out their TTL. Local session is untouched — call <see cref="Logout"/> after for a full sign-out.
        /// </summary>
        public async Task RevokeTokenAsync(CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();
            PlayerTokenRevokeResponse response = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerTokenRevokeResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRevoke}",
                    new object(),
                    Client.GetBaseHeaders(), cancellationToken),
                "Token revoke", cancellationToken, idempotent: false);

            if (response == null || !response.Revoked)
                throw new FlockAuthException("Token revoke was not confirmed by the server");
        }

        /// <summary>Registration preflight: checks whether a display name is still free. A race can still lose at register time — treat as advisory.</summary>
        public async Task<bool> IsNameAvailableAsync(string name, CancellationToken cancellationToken = default)
        {
            RequireNotEmpty(name, nameof(name));
            PlayerNameAvailableResponse response = await ExecuteAsync(
                () => FlockHttpClient.GetAsync<PlayerNameAvailableResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerNameAvailable(name)}",
                    Client.GetBaseHeaders(), cancellationToken),
                "Name availability", cancellationToken);

            if (response == null)
                throw new FlockNetworkException("Invalid response from server");
            return response.Available;
        }

        //Account linking

        /// <summary>Lists the signed-in player's linked credentials (no secrets). Always fetched fresh — credential state is never cached.</summary>
        public async Task<List<PlayerLinkedAccount>> GetLinkedAccountsAsync(CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();
            PlayerAccountsResponse response = await ExecuteAsync(
                () => FlockHttpClient.GetAsync<PlayerAccountsResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerAccounts}",
                    Client.GetBaseHeaders(), cancellationToken),
                "List linked accounts", cancellationToken);
            return ReadAccounts(response);
        }

        /// <summary>Attaches an email/password credential to the signed-in player. Returns the updated credential list; throws with <see cref="FlockErrorCode.PlayerAccountAlreadyLinked"/> when the email belongs to another player.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkEmailAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();
            RequireNotEmpty(email, nameof(email));
            RequireNotEmpty(password, nameof(password));
            return await LinkAsync(FlockEndpoints.PlayerLinkEmail,
                new PlayerLinkEmailRequest { Email = email, Password = password },
                FlockCredentialProvider.Email, "Link email", cancellationToken);
        }

        /// <summary>Attaches a device credential to the signed-in player — the "keep my guest progress" flow's counterpart.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();
            RequireNotEmpty(deviceId, nameof(deviceId));
            return await LinkAsync(FlockEndpoints.PlayerLinkDevice,
                new PlayerLinkDeviceRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId },
                FlockCredentialProvider.DeviceId, "Link device", cancellationToken);
        }

        /// <summary>Attaches a Google credential to the signed-in player.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkGoogleAsync(string idToken, CancellationToken cancellationToken = default)
        {
            return await LinkOAuthAsync(FlockCredentialProvider.Google, idToken, nameof(idToken), "Link Google", cancellationToken);
        }

        /// <summary>Attaches an Apple credential to the signed-in player.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkAppleAsync(string identityToken, CancellationToken cancellationToken = default)
        {
            return await LinkOAuthAsync(FlockCredentialProvider.Apple, identityToken, nameof(identityToken), "Link Apple", cancellationToken);
        }

        /// <summary>Attaches a Steam credential to the signed-in player.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkSteamAsync(string sessionTicket, CancellationToken cancellationToken = default)
        {
            return await LinkOAuthAsync(FlockCredentialProvider.Steam, sessionTicket, nameof(sessionTicket), "Link Steam", cancellationToken);
        }

        /// <summary>Attaches a Facebook credential to the signed-in player.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkFacebookAsync(string facebookToken, CancellationToken cancellationToken = default)
        {
            return await LinkOAuthAsync(FlockCredentialProvider.Facebook, facebookToken, nameof(facebookToken), "Link Facebook", cancellationToken);
        }

        /// <summary>Attaches a Discord credential to the signed-in player.</summary>
        public async Task<List<PlayerLinkedAccount>> LinkDiscordAsync(string discordToken, CancellationToken cancellationToken = default)
        {
            return await LinkOAuthAsync(FlockCredentialProvider.Discord, discordToken, nameof(discordToken), "Link Discord", cancellationToken);
        }

        /// <summary>Removes a credential from the signed-in player. The server refuses the last remaining one, throwing with <see cref="FlockErrorCode.PlayerCannotUnlinkLastCredential"/>.</summary>
        public async Task<List<PlayerLinkedAccount>> UnlinkAsync(FlockCredentialProvider provider, CancellationToken cancellationToken = default)
        {
            RequireAuthenticated();
            string wire = FlockCredentialProviders.ToWire(provider);
            PlayerAccountsResponse response = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerAccountsResponse>(
                    $"{Client.GetVersionedApiUrl()}/{FlockEndpoints.PlayerUnlink(wire)}",
                    new object(), Client.GetBaseHeaders(), cancellationToken),
                $"Unlink {wire}", cancellationToken, idempotent: false);

            List<PlayerLinkedAccount> accounts = ReadAccounts(response);
            Client.Logger.LogInfo($"Unlinked {wire} from player: {Client.CurrentPlayerId}");
            FlockEvents.InvokeAccountUnlinked(provider);
            return accounts;
        }

        // Every OAuth provider links with a bare token, so one path serves all five.
        private async Task<List<PlayerLinkedAccount>> LinkOAuthAsync(FlockCredentialProvider provider, string token, string tokenArgName, string context, CancellationToken cancellationToken)
        {
            RequireAuthenticated();
            RequireNotEmpty(token, tokenArgName);
            return await LinkAsync(FlockEndpoints.PlayerLinkOAuth(FlockCredentialProviders.ToWire(provider)),
                new PlayerLinkOAuthRequest { Token = token }, provider, context, cancellationToken);
        }

        // idempotent:false — a link that may have landed is never re-sent, since the retry would come back as account_already_linked.
        private async Task<List<PlayerLinkedAccount>> LinkAsync(string endpoint, object body, FlockCredentialProvider provider, string context, CancellationToken cancellationToken)
        {
            PlayerAccountsResponse response = await ExecuteAsync(
                () => FlockHttpClient.PostAsync<PlayerAccountsResponse>(
                    $"{Client.GetVersionedApiUrl()}/{endpoint}", body, Client.GetBaseHeaders(), cancellationToken),
                context, cancellationToken, idempotent: false);

            List<PlayerLinkedAccount> accounts = ReadAccounts(response);
            Client.Logger.LogInfo($"{context} succeeded for player: {Client.CurrentPlayerId}");
            FlockEvents.InvokeAccountLinked(provider);
            return accounts;
        }

        // These routes return the model at the root, not in a GenericResponse envelope. Every one hands back the full list, so the email flag re-derives itself on any of them.
        private List<PlayerLinkedAccount> ReadAccounts(PlayerAccountsResponse response)
        {
            if (response?.Accounts == null)
                throw new FlockNetworkException("Invalid response from server (missing accounts)");

            bool hasEmail = false;
            foreach (PlayerLinkedAccount account in response.Accounts)
            {
                if (account != null && account.ProviderType == FlockCredentialProvider.Email)
                    hasEmail = true;
            }
            _hasEmailCredential = hasEmail;
            return response.Accounts;
        }

        // Bearer-only endpoints — fail fast instead of a guaranteed server 401.
        private void RequireAuthenticated()
        {
            if (!Client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in");
        }

        // Password reset is scoped to an email account — an email login (restored ones count) or a linked email credential this session.
        private void RequireEmailLogin()
        {
            RequireAuthenticated();
            if (_currentAuthMethod != FlockAuthMethod.Email && !_hasEmailCredential)
                throw new FlockAuthException("Password reset requires an email credential on this account");
        }

        // Non-secret, so PlayerPrefs (not the token store) — lets a restored session keep its original method.
        private static void PersistAuthMethod(FlockAuthMethod method)
        {
            PlayerPrefs.SetString(PrefKeyAuthMethod, method.ToString());
            PlayerPrefs.Save();
        }

        private static FlockAuthMethod? LoadPersistedAuthMethod()
        {
            string stored = PlayerPrefs.GetString(PrefKeyAuthMethod, null);
            if (Enum.TryParse(stored, out FlockAuthMethod method)) return method;
            return null;
        }

        /// <summary>
        /// Logs the current player out by clearing local authentication state.
        /// Safe to call when no player is signed in. Server-side token revocation is separate — see <see cref="RevokeTokenAsync"/>.
        /// </summary>
        public void Logout()
        {
            bool wasAuthenticated = Client.IsAuthenticated;
            _currentAuthMethod = null;
            _hasEmailCredential = false;
            PlayerPrefs.DeleteKey(PrefKeyAuthMethod);
            Client.ClearTokens();
            if (wasAuthenticated)
                FlockEvents.InvokeLoggedOut();
        }
    }
}