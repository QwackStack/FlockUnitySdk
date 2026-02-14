using System;
using System.Collections.Generic;
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

        private static void ApplyHeaders(HttpRequestMessage request, Dictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (var kvp in headers)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
            }
        }

        public static async Task<T> GetAsync<T>(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(request, headers);

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode} - {errorContent}");
                    }

                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 422)
                    {
                        throw new FlockValidationException($"Validation failed: {errorContent}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode} - {errorContent}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

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

        public static async Task<T> PostAsync<T>(string url, object data, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyHeaders(request, headers);

                string json = JsonConvert.SerializeObject(data);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode} - {errorContent}");
                    }

                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 422)
                    {
                        throw new FlockValidationException($"Validation failed: {errorContent}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode} - {errorContent}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

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

        public static async Task<T> PutAsync<T>(string url, object data, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, url);
                ApplyHeaders(request, headers);

                string json = JsonConvert.SerializeObject(data);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode} - {errorContent}");
                    }

                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 422)
                    {
                        throw new FlockValidationException($"Validation failed: {errorContent}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode} - {errorContent}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

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

        public static async Task<T> DeleteAsync<T>(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, url);
                ApplyHeaders(request, headers);

                HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();

                    if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    {
                        throw new FlockAuthException($"Authentication failed: {response.StatusCode} - {errorContent}");
                    }

                    throw new FlockNetworkException($"HTTP request failed: {response.StatusCode} - {errorContent}", (int)response.StatusCode);
                }

                string content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrEmpty(content))
                {
                    throw new FlockNetworkException("Empty response from server");
                }

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
    }
}
