using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Flock.Http
{
    public static class HttpClient
    {
        private static readonly System.Net.Http.HttpClient _client = new System.Net.Http.HttpClient();

        public static async Task<T> PostAsync<T>(string url, object data)
        {
            var content = new StringContent(
                JsonConvert.SerializeObject(data),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(responseContent);
        }

        public static async Task<T> GetAsync<T>(string url)
        {
            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(content);
        }

        public static async Task<T> PutAsync<T>(string url, object data)
        {
            var content = new StringContent(
                JsonConvert.SerializeObject(data),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _client.PutAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(responseContent);
        }

        public static async Task<T> DeleteAsync<T>(string url)
        {
            var response = await _client.DeleteAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(content);
        }
    }
} 