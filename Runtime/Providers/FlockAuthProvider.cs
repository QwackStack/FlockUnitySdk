using System;
using System.Threading;
using System.Threading.Tasks;
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
            try
            {
                PlayerLoginResponse response = await Client.RetryHandler.ExecuteAsync(operation, cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                    throw new FlockAuthException($"Invalid {context.ToLower()} response from server");

                Client.SetTokens(response.AccessToken, response.RefreshToken);
                Client.Logger.LogInfo($"{context} successful for player: {Client.CurrentPlayerId}");

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

        public async Task<PlayerLoginResponse> LoginWithEmailAsync(string email, string password,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/login",
                    new PlayerLoginRequest { LoginType = "email", Email = email, Password = password },
                    Client.GetBaseHeaders(), cancellationToken),
                "Email login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> LoginWithDeviceAsync(string deviceId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/login/device",
                    new PlayerDeviceLoginRequest { DeviceType = SystemInfo.deviceType.ToString(), DeviceId = deviceId },
                    Client.GetBaseHeaders(), cancellationToken),
                "Device login", cancellationToken);
        }

        public async Task<PlayerLoginResponse> RegisterWithEmailAsync(string email, string password, string name = null,
            CancellationToken cancellationToken = default)
        {
            //TODO
            //once exception data is there , check if already registered is thrown
            //otherwise a temp fix is to parse the exception and check if it is a regi issue
            //do this for all registration methods?
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/register",
                    new PlayerEmailRegistrationRequest { Email = email, Password = password, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Email registration", cancellationToken);
        }

        public async Task<PlayerLoginResponse> RegisterWithDeviceAsync(string deviceId, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/register/device",
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
                    $"{Client.GetApiUrl()}/v1/player/login/google",
                    new PlayerGoogleLoginRequest { IdToken = idToken },
                    Client.GetBaseHeaders(), cancellationToken),
                "Google login", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> RegisterWithGoogleAsync(string idToken, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/register/google",
                    new PlayerGoogleRegistrationRequest { IdToken = idToken, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Google registration", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> LoginWithAppleAsync(string identityToken,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/login/apple",
                    new PlayerAppleLoginRequest { IdentityToken = identityToken },
                    Client.GetBaseHeaders(), cancellationToken),
                "Apple login", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> RegisterWithAppleAsync(string identityToken, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/register/apple",
                    new PlayerAppleRegistrationRequest { IdentityToken = identityToken, Name = name },
                    Client.GetBaseHeaders(), cancellationToken),
                "Apple registration", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> LoginWithSteamAsync(string sessionTicket,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/login/steam",
                    new PlayerSteamLoginRequest { SessionTicket = sessionTicket },
                    Client.GetBaseHeaders(), cancellationToken),
                "Steam login", cancellationToken);
        }
        
        public async Task<PlayerLoginResponse> RegisterWithSteamAsync(string sessionTicket, string name = null,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAuthAsync(
                () => FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{Client.GetApiUrl()}/v1/player/register/steam",
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