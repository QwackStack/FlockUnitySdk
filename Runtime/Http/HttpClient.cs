using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Newtonsoft.Json;

namespace Flock.Http
{
    public static class FlockHttpClient
    {
        private static readonly HttpClient Client = new HttpClient();

        public static Task<T> GetAsync<T>(string url, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, headers);
            return SendAsync<T>(request, cancellationToken);
        }

        public static Task<T> PostAsync<T>(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            ApplyHeaders(request, headers);
            request.Content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            return SendAsync<T>(request, cancellationToken);
        }

        public static Task<T> PutAsync<T>(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            ApplyHeaders(request, headers);
            request.Content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            return SendAsync<T>(request, cancellationToken);
        }

        public static Task<T> PatchAsync<T>(string url, object data, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), url);
            ApplyHeaders(request, headers);
            request.Content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            return SendAsync<T>(request, cancellationToken);
        }

        public static Task<T> DeleteAsync<T>(string url, Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, url);
            ApplyHeaders(request, headers);
            return SendAsync<T>(request, cancellationToken);
        }

        private static async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                var response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    var code = (int)response.StatusCode;

                    if (code == 401 || code == 403)
                        throw new FlockAuthException(new StringBuilder().Append("Authentication failed: ")
                            .Append(response.StatusCode)
                            .Append(" - ")
                            .Append(errorContent)
                            .ToString());

                    if (code == 400 || code == 422)
                        throw new FlockValidationException(new StringBuilder().Append("Validation failed: ")
                            .Append(errorContent)
                            .ToString());

                    throw new FlockNetworkException(
                        new StringBuilder().Append("HTTP request failed: ")
                            .Append(response.StatusCode)
                            .Append(" - ")
                            .Append(errorContent)
                            .ToString(),
                        code);
                }

                var content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(content))
                    throw new FlockNetworkException("Empty response from server");

                return JsonConvert.DeserializeObject<T>(content);
            }
            catch (FlockException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new FlockNetworkException("Network request failed", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FlockNetworkException("Request timeout", ex);
            }
        }

        private static void ApplyHeaders(HttpRequestMessage request, Dictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (var kvp in headers)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                    request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }
    }
}