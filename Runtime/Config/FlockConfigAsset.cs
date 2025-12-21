using UnityEngine;

namespace Flock.Config
{
    [CreateAssetMenu(fileName = "FlockConfig", menuName = "Flock/SDK Configuration", order = 1)]
    public class FlockConfigAsset : ScriptableObject
    {
        [Header("Required Settings")]
        [Tooltip("API endpoint URL")]
        public string apiUrl = "https://api-flock.qwacks.com";

        [Tooltip("Your Flock API Key (stored securely)")]
        [SerializeField, HideInInspector]
        private string apiKey;

        [Tooltip("Target environment")]
        public FlockEnvironment environment = FlockEnvironment.Production;

        [Header("Optional Settings")]
        [Tooltip("Enable detailed debug logging")]
        public bool enableDebugLogs = false;

        /// <summary>
        /// Gets or sets the API Key (hidden in inspector for security)
        /// </summary>
        public string ApiKey
        {
            get => apiKey;
            set => apiKey = value;
        }

        /// <summary>
        /// Converts this asset to a FlockInitConfig instance
        /// </summary>
        public FlockInitConfig ToInitConfig()
        {
            return new FlockInitConfig(apiUrl, apiKey, environment, enableDebugLogs);
        }

        /// <summary>
        /// Validates the configuration
        /// </summary>
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

            errorMessage = string.Empty;
            return true;
        }
    }
}
