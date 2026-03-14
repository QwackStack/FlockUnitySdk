using System;
using System.Text;
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
            catch (FlockException) { throw; }
            catch (Exception ex)
            {
                Client.Logger.LogError(new StringBuilder().Append(context).Append(" failed").ToString(), ex);
                throw new FlockNetworkException(new StringBuilder().Append(context).Append(" failed").ToString(), ex);
            }
        }

        protected void RequireNotEmpty(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
                throw new FlockValidationException(new StringBuilder().Append(name).Append(" cannot be null or empty").ToString());
        }

        protected void RequireRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new FlockValidationException(new StringBuilder().Append(name)
                    .Append(" must be between ")
                    .Append(min)
                    .Append(" and ")
                    .Append(max)
                    .ToString());
        }

        protected void ValidateResponse<T>(GenericResponse<T> response) where T : class
        {
            if (response?.Result == null)
                throw new FlockNetworkException("Invalid response from server");
        }
    }
}
