using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using System.Text;
using Newtonsoft.Json;

namespace Flock
{
    internal static class HttpClient
    {
        public static async Task<T> GetAsync<T>(string url, string accessToken = null)
        {
            using var request = new UnityWebRequest(url, "GET");
            request.downloadHandler = new DownloadHandlerBuffer();
            
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            }

            try
            {
                await request.SendWebRequest();
                
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new FlockException($"HTTP Error: {request.error}", request.responseCode);
                }

                return JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
            }
            catch (Exception ex) when (!(ex is FlockException))
            {
                throw new FlockException($"Network Error: {ex.Message}", 0);
            }
        }

        public static async Task<T> PostAsync<T>(string url, object data, string accessToken = null)
        {
            var json = JsonConvert.SerializeObject(data);
            using var request = new UnityWebRequest(url, "POST");
            
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            }

            try
            {
                await request.SendWebRequest();
                
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new FlockException($"HTTP Error: {request.error}", request.responseCode);
                }

                return JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
            }
            catch (Exception ex) when (!(ex is FlockException))
            {
                throw new FlockException($"Network Error: {ex.Message}", 0);
            }
        }

        public static async Task<T> PutAsync<T>(string url, object data, string accessToken = null)
        {
            var json = JsonConvert.SerializeObject(data);
            using var request = new UnityWebRequest(url, "PUT");
            
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            }

            try
            {
                await request.SendWebRequest();
                
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new FlockException($"HTTP Error: {request.error}", request.responseCode);
                }

                return JsonConvert.DeserializeObject<T>(request.downloadHandler.text);
            }
            catch (Exception ex) when (!(ex is FlockException))
            {
                throw new FlockException($"Network Error: {ex.Message}", 0);
            }
        }
    }

    public class FlockException : Exception
    {
        public long StatusCode { get; }

        public FlockException(string message, long statusCode) : base(message)
        {
            StatusCode = statusCode;
        }
    }
} 