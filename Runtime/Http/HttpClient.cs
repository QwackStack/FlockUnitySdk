using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Flock.Exceptions;

namespace Flock.Http
{
    public static class HttpClient
    {
        private static readonly System.Net.Http.HttpClient Client = new System.Net.Http.HttpClient();

        public static async Task<T> GetAsync<T>(string url, string accessToken = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                }

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

                return JsonConvert.DeserializeObject<T>(content);
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

        public static async Task<T> PostAsync<T>(string url, object data, string accessToken = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                }

                string json = JsonConvert.SerializeObject(data);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode}");
                    }

                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 422)
                    {
                        throw new FlockValidationException($"Validation failed: {errorContent}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

                return JsonConvert.DeserializeObject<T>(content);
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

        public static async Task<T> PutAsync<T>(string url, object data, string accessToken = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                }

                string json = JsonConvert.SerializeObject(data);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode}");
                    }

                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 422)
                    {
                        throw new FlockValidationException($"Validation failed: {errorContent}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

                return JsonConvert.DeserializeObject<T>(content);
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

        public static async Task<T> DeleteAsync<T>(string url, string accessToken = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {accessToken}");
                }

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

                return JsonConvert.DeserializeObject<T>(content);
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
    }
} 