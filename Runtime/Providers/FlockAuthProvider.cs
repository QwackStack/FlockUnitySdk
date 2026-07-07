using System;
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
                PersistAuthMethod(method);
                Client.Logger.LogInfo($"{context} successful for player: {Client.CurrentPlayerId}");

                FlockEvents.InvokeAuthenticated(new FlockAuthInfo(Client.CurrentPlayerId, method));

                await TryInitializeAnalyticsAsync(cancellationToken);

                return response;
            }
            catch (FlockException)
            {
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
        private static bool IsAlreadyRegisteredError(Exception ex)
        {
            switch ((ex as FlockException)?.ErrorCode)
            {
                case FlockErrorCode.PlayerEmailAlreadyRegistered:
                case FlockErrorCode.PlayerDeviceAlreadyRegistered:
                case FlockErrorCode.PlayerGoogleAccountAlreadyRegistered:
                case FlockErrorCode.PlayerAppleAccountAlreadyRegistered:
                case FlockErrorCode.PlayerSteamAccountAlreadyRegistered:
                    return true;
                default:
                    return false;
            }
        }
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

        // Bearer-only endpoints — fail fast instead of a guaranteed server 401.
        private void RequireAuthenticated()
        {
            if (!Client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in");
        }

        // Password reset is scoped to the signed-in email account — restored email sessions count, social/device logins don't.
        private void RequireEmailLogin()
        {
            RequireAuthenticated();
            if (_currentAuthMethod != FlockAuthMethod.Email)
                throw new FlockAuthException("Password reset requires being signed in with email");
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
            PlayerPrefs.DeleteKey(PrefKeyAuthMethod);
            Client.ClearTokens();
            if (wasAuthenticated)
                FlockEvents.InvokeLoggedOut();
        }
    }
}