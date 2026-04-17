using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;

namespace Flock.Http
{
    public abstract class FlockProviderBase
    {
        protected readonly FlockClient Client;

        protected FlockProviderBase(FlockClient client)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
        }

        protected async Task<T> ExecuteAsync<T>(
            Func<Task<T>> operation,
            string context,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Client.RetryHandler.ExecuteAsync(operation, cancellationToken);
            }
            //only try refresh if the auth is even successful
            catch (FlockAuthException) when (Client.IsAuthenticated)
            {
                Client.Logger.LogDebug("Access token expired, attempting silent refresh");
                bool refreshed = await Client.TryRefreshTokenAsync(cancellationToken);
                if (!refreshed)
                    throw;

                try
                {
                    return await Client.RetryHandler.ExecuteAsync(operation, cancellationToken);
                }
                catch (FlockException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Client.Logger.LogError($"{context} failed", ex);
                    throw new FlockNetworkException($"{context} failed", ex);
                }
            }
            catch (FlockException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Client.Logger.LogError($"{context} failed", ex);
                throw new FlockNetworkException($"{context} failed", ex);
            }
        }

        protected void RequireNotEmpty(string value, string name)
        {
            // for cases that require params not to be null
            if (string.IsNullOrEmpty(value))
                throw new FlockValidationException($"{name} cannot be null or empty");
        }

        protected void RequireRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new FlockValidationException($"{name} must be between {min} and {max}");
        }

        protected void ValidateResponse<T>(GenericResponse<T> response) where T : class
        {
            if (response?.Result == null)
                throw new FlockNetworkException("Invalid response from server");
        }
    }
}
