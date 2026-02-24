using UnityEngine;

namespace Flock.Config
{
    [CreateAssetMenu(fileName = "FlockConfig", menuName = "Flock/SDK Configuration", order = 1)]
    public class FlockConfigAsset : ScriptableObject
    {
        [Header("Required Settings")]
        [Tooltip("API endpoint URL")]
        public string apiUrl = "https://api-flock.qwacks.com";

        [Tooltip("Your Flock API Key")]
        [SerializeField, HideInInspector]
        private string apiKey;

        [Tooltip("Your Game ID")]
        public string gameId;

        [Tooltip("Your Game Version ID")]
        public string gameVersionId;

        [Header("Optional Settings")]
        [Tooltip("Enable detailed debug logging")]
        public bool enableDebugLogs = false;

        public string ApiKey
        {
            get => apiKey;
            set => apiKey = value;
        }

        public FlockInitConfig ToInitConfig()
        {
            return new FlockInitConfig(apiUrl, apiKey, gameId, gameVersionId, enableDebugLogs);
        }

        public bool IsValid(out string errorMessage)
        {
            if (string.IsNullOrEmpty(apiUrl))
            {
                errorMessage = "API URL is required";
                return false;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                errorMessage = "API Key is required";
                return false;
            }

            if (string.IsNullOrEmpty(gameId))
            {
                errorMessage = "Game ID is required";
                return false;
            }

            if (string.IsNullOrEmpty(gameVersionId))
            {
                errorMessage = "Game Version ID is required";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }
    }
}
