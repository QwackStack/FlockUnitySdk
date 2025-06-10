using System;
using Flock.Auth;

namespace Flock.Config
{
    public class FlockConfig
    {
        public string GameId { get; }
        public string ApiUrl { get; }
        public bool EnableDebugLogs { get; }
        public TimeSpan Timeout { get; }
        public bool[] EnabledAuthMethods { get; }

        private FlockConfig(string gameId, string apiUrl, bool enableDebugLogs, TimeSpan timeout, bool[] enabledAuthMethods)
        {
            GameId = gameId;
            ApiUrl = apiUrl;
            EnableDebugLogs = enableDebugLogs;
            Timeout = timeout;
            EnabledAuthMethods = enabledAuthMethods;
        }

        public bool IsAuthMethodEnabled(AuthProviderType providerType)
        {
            return EnabledAuthMethods[(int)providerType];
        }

        public class Builder
        {
            private string _gameId;
            private string _apiUrl = "https://api.flock.qwacks.com";
            private bool _enableDebugLogs;
            private TimeSpan _timeout = TimeSpan.FromSeconds(30);
            private bool[] _enabledAuthMethods = new bool[Enum.GetValues(typeof(AuthProviderType)).Length];

            public Builder SetGameId(string gameId)
            {
                _gameId = gameId;
                return this;
            }

            public Builder SetApiUrl(string apiUrl)
            {
                _apiUrl = apiUrl;
                return this;
            }

            public Builder SetEnableDebugLogs(bool enableDebugLogs)
            {
                _enableDebugLogs = enableDebugLogs;
                return this;
            }

            public Builder SetTimeout(TimeSpan timeout)
            {
                _timeout = timeout;
                return this;
            }

            public Builder SetEnabledAuthMethods(bool[] enabledAuthMethods)
            {
                if (enabledAuthMethods == null || enabledAuthMethods.Length != Enum.GetValues(typeof(AuthProviderType)).Length)
                {
                    throw new ArgumentException("Invalid enabled auth methods array");
                }
                _enabledAuthMethods = enabledAuthMethods;
                return this;
            }

            public Builder EnableAuthMethod(AuthProviderType providerType)
            {
                _enabledAuthMethods[(int)providerType] = true;
                return this;
            }

            public Builder DisableAuthMethod(AuthProviderType providerType)
            {
                _enabledAuthMethods[(int)providerType] = false;
                return this;
            }

            public FlockConfig Build()
            {
                if (string.IsNullOrEmpty(_gameId))
                {
                    throw new InvalidOperationException("Game ID is required");
                }

                return new FlockConfig(_gameId, _apiUrl, _enableDebugLogs, _timeout, _enabledAuthMethods);
            }
        }
    }
} 