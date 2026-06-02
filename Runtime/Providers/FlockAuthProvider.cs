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
        public FlockAuthProvider(FlockClient client) : base(client)
        {
        }

        private async Task<PlayerLoginResponse> ExecuteAuthAsync(
            Func<Task<PlayerLoginResponse>> operation, string context, CancellationToken cancellationToken)
        {
            Client.Logger.LogInfo($"{context} starting...");
            try
            {
                PlayerLoginResponse response = await Client.RetryHandler.ExecuteAsync(operation, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                    throw new FlockAuthException($"Invalid {context.ToLower()} response from server");

                Client.SetTokens(response.AccessToken, response.RefreshToken);
                Client.Logger.LogInfo($"{context} successful for player: {Client.CurrentPlayerId}");


#if !FLOCK_NO_ANALYTICS
                if (Client.Analytics != null)
                {
                    try
                    {
                        await Client.Analytics.InitializeAsync(cancellationToken);
                    }
                    catch (Exception analyticsEx)
                    {
                        Client.Logger.LogWarning($"Analytics initialization failed (non-fatal): {analyticsEx.Message}");
                    }
                }
#endif

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
            Func<Task<PlayerLoginResponse>> operation, string context, CancellationToken cancellationToken)
        {
            try
            {
                return await ExecuteAuthAsync(operation, context, cancellationToken);
            }
            catch (FlockException ex) when (IsAlreadyRegisteredError(ex))
            {
                Client.Logger.LogWarning($"{context} skipped: player already registered.");
                return null;
            }
        }

        // Temp string-match until the API returns a structured "already registered" code.
        private static bool IsAlreadyRegisteredError(Exception ex)
        {
            string msg = ex?.Message;
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0
                && (msg.IndexOf("registered", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("exists", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("taken", StringComparison.OrdinalIgnoreCase) >= 0);
               
         }
        private async Task TryInitializeAnalyticsAsync(CancellationToken cancellationToken)
        {
            if (Client.Analytics == null) return;
            try
            {
                await Client.Analytics.InitializeAsync(cancellationToken);
            }
            catch (Exception analyticsEx)
            {
                Client.Logger.LogWarning($"Analytics initialization failed (non-fatal): {analyticsEx.Message}");
            }
        }

        /// <summary>
        /// Attempts to resume a previously persisted session from
        /// <see cref="Flock.Config.FlockInitConfig.TokenStore"/>. If the stored access
        /// token is still valid it is used as-is; if expired, a refresh is attempted.
        /// Returns <c>true</c> when the player is signed in afterward, <c>false</c>
        /// otherwise (no stored tokens, parse failure, or refresh rejected).
        /// </summary>
        public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
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
            await TryInitializeAnalyticsAsync(cancellationToken);
            return true;
        }

        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/login",
                    new PlayerLoginRequest { LoginType = "email", Email = email, Password = password },
                    Client.GetBaseHeaders(), cancellationToken),
                "Email login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/login/device",
                    new PlayerDeviceLoginRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId },
                    Client.GetBaseHeaders(), cancellationToken),
                "Device login", cancellationToken);
        }

        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/register",
                    new PlayerEmailRegistrationRequest { Email = email, Password = password, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Email registration", cancellationToken);
        }

        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceId, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/register/device",
                    new PlayerDeviceRegistrationRequest
                        { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Device registration", cancellationToken);
        }
        public async Task<PlayerLoginResponse> LoginWithGoogleAsync(string idToken,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/login/google",
                    new PlayerGoogleLoginRequest { IdToken = idToken },
                    Client.GetBaseHeaders(), cancellationToken),
                "Google login", cancellationToken);
        }
        
        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithGoogleAsync(string idToken, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/register/google",
                    new PlayerGoogleRegistrationRequest { IdToken = idToken, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Google registration", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> LoginWithAppleAsync(string identityToken,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/login/apple",
                    new PlayerAppleLoginRequest { IdentityToken = identityToken },
                    Client.GetBaseHeaders(), cancellationToken),
                "Apple login", cancellationToken);
        }
        
        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithAppleAsync(string identityToken, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/register/apple",
                    new PlayerAppleRegistrationRequest { IdentityToken = identityToken, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Apple registration", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> LoginWithSteamAsync(string sessionTicket,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/login/steam",
                    new PlayerSteamLoginRequest { SessionTicket = sessionTicket },
                    Client.GetBaseHeaders(), cancellationToken),
                "Steam login", cancellationToken);
        }
        
        /// <param name="name">Optional display name. <b>Server-enforced unique</b>; recommended to pass <c>null</c> until the backend returns structured error codes — see the README "Backend backlog" entry on structured registration errors.</param>
        public async Task<PlayerLoginResponse> RegisterWithSteamAsync(string sessionTicket, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRegistrationAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetVersionedApiUrl()}/player/register/steam",
                    new PlayerSteamRegistrationRequest { SessionTicket = sessionTicket, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Steam registration", cancellationToken);
        }

        /// <summary>
        /// Logs the current player out by clearing local authentication state.
        /// Safe to call when no player is signed in.
        /// </summary>
        public void Logout()
        {
            Client.ClearTokens();
        }
    }
}