using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Auth;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Interfaces;
using Flock.Logging;
using Flock.Models;
using Flock.Analytics;
using Flock.Providers;
using UnityEngine;

namespace Flock
{
    public class FlockClient : IFlockClient
    {
        /// <summary>
        /// API version segment appended to <see cref="GetApiUrl"/> for all SDK HTTP calls.
        /// Single source of truth — bump here (and in the Unreal SDK for parity) when the
        /// backend cuts a new major API version.
        /// </summary>
        public const string ApiVersion = "v1";

        private static FlockClient _instance;

        /// <summary>
        /// Global singleton instance. Set by <see cref="CreateAsync"/> and used as the
        /// entry point for all SDK calls (e.g. <c>FlockClient.Instance.Authentication</c>).
        /// Throws <see cref="FlockException"/> if accessed before <see cref="CreateAsync"/>
        /// has completed — initialize the SDK at startup before any feature uses it.
        /// </summary>
        public static FlockClient Instance
        {
            get
            {
                if (_instance == null)
                    throw new FlockException(
                        "FlockClient has not been initialized. Call 'await FlockClient.CreateAsync(config)' once at startup before accessing FlockClient.Instance.");
                return _instance;
            }
        }

        /// <summary>True once <see cref="CreateAsync"/> has successfully run.</summary>
        public static bool IsInitialized => _instance != null;

        private readonly FlockInitConfig _initConfig;
        private readonly IFlockLogger _logger;
        private readonly RetryHandler _retryHandler;
        private readonly FlockSnapshotStore _snapshotStore;
        //To avoid refresh deadlocks
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        private string _accessToken;
        private string _refreshToken;
        private JwtTokenClaims _tokenClaims;

        // Clears the static singleton when domain reload is disabled on enter-play-mode,
        // so a fresh play session always starts with no SDK state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
        }

        /// <summary>Token refresh failed — re-login required. Prefer <see cref="FlockEvents.OnAuthExpired"/>; kept for back-compat.</summary>
        public event Action OnSessionExpired;

        private FlockAuthProvider _authentication;
#if !FLOCK_NO_CONFIG
        private FlockConfigProvider _config;
#endif
#if !FLOCK_NO_SCHEMA
        private FlockSchemaProvider _schema;
#endif
#if !FLOCK_NO_GAME
        private FlockGameProvider _game;
#endif
#if !FLOCK_NO_PLAYER
        private PlayerProvider _playerData;
#endif
#if !FLOCK_NO_COMMANDS
        private FlockCommandProvider _commands;
#endif
#if !FLOCK_NO_SHOP
        private FlockShopProvider _shop;
#endif
#if !FLOCK_NO_ASSET
        private FlockAssetProvider _asset;
#endif
        private FlockSession _session;
#if !FLOCK_NO_ANALYTICS
        private IAnalyticProvider _analytics;
#endif

        private FlockClient(FlockInitConfig initConfig, IFlockLogger logger)
        {
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
            _logger = logger ?? (initConfig.EnableDebugLogs ? new UnityFlockLogger() : new NullFlockLogger());
            FlockEvents.Logger = _logger;
            _logger.LogInfo("Initializing Flock SDK");
            _logger.LogInfo($"Token persistence provider: {initConfig.TokenStore?.GetType().Name ?? "<disabled>"}");
            _retryHandler = new RetryHandler(initConfig.RetryPolicy, _logger);
            _snapshotStore = initConfig.EnableOfflineCache
                ? new FlockSnapshotStore(initConfig.OfflineCacheDirectory, _logger)
                : null;
        }

        /// <summary>
        /// Creates and initializes a <see cref="FlockClient"/>. Resolves
        /// <see cref="FlockInitConfig.GameVersion"/> against the backend to populate
        /// <see cref="FlockInitConfig.GameVersionId"/> (used in the
        /// <c>X-Game-Version-ID</c> header), then wires up service providers.
        /// Raises <see cref="FlockEvents.OnInitialized"/> or <see cref="FlockEvents.OnInitializationFailed"/> (still throws).
        /// </summary>
        public static async Task<FlockClient> CreateAsync(
            FlockInitConfig initConfig,
            IFlockLogger logger = null,
            CancellationToken cancellationToken = default)
        {
            // Misuse guard — no OnInitializationFailed: the SDK is already initialized and working.
            if (_instance != null)
                throw new FlockException(
                    "FlockClient is already initialized. Call FlockClient.Shutdown() first if you need to reinitialize with a different config.");

            try
            {
                FlockClient client = new FlockClient(initConfig, logger);
                await client.ResolveGameVersionAsync(cancellationToken);
                client._snapshotStore?.PruneOtherVersions(client._initConfig.GameVersionId);
                client.InitializeServices();
#if !FLOCK_NO_SCHEMA
                CodeGenValidator.WarnIfDrifted(client._initConfig.GameVersionId, client._logger);
#endif
                _instance = client;
            }
            catch (Exception ex)
            {
                FlockEvents.RaiseInitializationFailed(ex);
                throw;
            }

            FlockEvents.RaiseInitialized();
            return _instance;
        }

        /// <summary>
        /// Clears the global <see cref="Instance"/>, allowing <see cref="CreateAsync"/> to be
        /// called again. Logs out the current player first so the token state is dropped.
        /// Raises <see cref="FlockEvents.OnShutdown"/> last, then clears all <see cref="FlockEvents"/> subscriptions.
        /// </summary>
        public static void Shutdown()
        {
            //Should we allow this?
            if (_instance == null) return;
            _instance.ClearTokens();
            _instance = null;
            FlockEvents.RaiseShutdown();
            FlockEvents.ClearAll();
            FlockEvents.Logger = null;
        }

        private async Task ResolveGameVersionAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_initConfig.GameVersion))
                throw new FlockValidationException("GameVersion is required to initialize the Flock SDK");

            string snapshotKey = $"{_initConfig.ApiUrl}|{_initConfig.GameVersion}";

            if (_snapshotStore != null
                && Application.internetReachability == NetworkReachability.NotReachable
                && _snapshotStore.TryRead(FlockSnapshotStore.BootstrapScope, snapshotKey, out GameVersionSchema offline))
            {
                _initConfig.GameVersionId = offline.Id;
                _logger.LogInfo($"Offline: using cached game version '{_initConfig.GameVersion}' -> id '{offline.Id}'");
                return;
            }

            string url = $"{GetVersionedApiUrl()}/game_version/by-name/{Uri.EscapeDataString(_initConfig.GameVersion)}";
            try
            {
                GenericResponse<GameVersionSchema> response = await _retryHandler.ExecuteAsync(
                    () => FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(
                        url, _initConfig.GetBootstrapHeaders(), cancellationToken),
                    cancellationToken);

                if (response?.Result == null || string.IsNullOrEmpty(response.Result.Id))
                    throw new FlockException($"Could not resolve GameVersionId for game version '{_initConfig.GameVersion}'");

                _initConfig.GameVersionId = response.Result.Id;
                _snapshotStore?.Write(FlockSnapshotStore.BootstrapScope, snapshotKey, response.Result);
                _logger.LogInfo($"Resolved game version '{_initConfig.GameVersion}' -> id '{_initConfig.GameVersionId}'");
            }
            catch (FlockNetworkException e)
            {
                if (_snapshotStore != null
                    && !FlockNetworkException.IsPermanentStatus(e.StatusCode)
                    && _snapshotStore.TryRead(FlockSnapshotStore.BootstrapScope, snapshotKey, out GameVersionSchema cached))
                {
                    _initConfig.GameVersionId = cached.Id;
                    _logger.LogWarning($"Could not reach server; using cached game version '{_initConfig.GameVersion}' -> id '{cached.Id}'");
                    return;
                }

                _logger.LogError("Initialization failed", e);
                throw;
            }
            catch (FlockException e)
            {
                // Auth/network/validation errors must surface — otherwise InitializeServices runs
                // with GameVersionId == null and every subsequent API call breaks silently.
                _logger.LogError("Initialization failed", e);
                throw;
            }
            catch (Exception ex)
            {
                throw new FlockException($"Failed to resolve GameVersionId for '{_initConfig.GameVersion}'", ex);
            }
        }

        private void InitializeServices()
        {
#if !FLOCK_NO_PLAYER
            _playerData = new PlayerProvider(this);
#endif
#if !FLOCK_NO_CONFIG
            _config = new FlockConfigProvider(this);
#endif
#if !FLOCK_NO_SCHEMA
            _schema = new FlockSchemaProvider(this);
#endif
#if !FLOCK_NO_GAME
            _game = new FlockGameProvider(this);
#endif
#if !FLOCK_NO_COMMANDS
            _commands = new FlockCommandProvider(this);
#endif
#if !FLOCK_NO_SHOP
            _shop = new FlockShopProvider(this);
#endif
#if !FLOCK_NO_ASSET
            _asset = new FlockAssetProvider(this);
#endif
            _authentication = new FlockAuthProvider(this);

            _session = new FlockSession(_initConfig.AnalyticsConfig, _logger);
#if !FLOCK_NO_ANALYTICS
            if (_initConfig.AnalyticsConfig.Enabled)
                _analytics = new FlockAnalyticsProvider(this);
            else
                _analytics = new NullAnalyticsProvider(this);
#endif
        }

        internal IFlockLogger Logger => _logger;
        internal RetryHandler RetryHandler => _retryHandler;
        internal FlockInitConfig InitConfig => _initConfig;
        internal FlockSnapshotStore SnapshotStore => _snapshotStore;
        
        public FlockAuthProvider Authentication => _authentication;
#if !FLOCK_NO_CONFIG
        public FlockConfigProvider Config => _config;
#endif
#if !FLOCK_NO_SCHEMA
        public FlockSchemaProvider Schema => _schema;
#endif
#if !FLOCK_NO_GAME
        public FlockGameProvider Game => _game;
#endif
#if !FLOCK_NO_PLAYER
        public PlayerProvider Player  => _playerData;
#endif
#if !FLOCK_NO_COMMANDS
        public FlockCommandProvider Commands => _commands;
#endif
#if !FLOCK_NO_SHOP
        public FlockShopProvider Shop => _shop;
#endif
#if !FLOCK_NO_ASSET
        public FlockAssetProvider Asset => _asset;
#endif
#if !FLOCK_NO_ANALYTICS
        public IAnalyticProvider Analytics => _analytics;
#endif
        internal FlockSession Session => _session;
        public bool HasActiveSession => _session?.IsActive ?? false;
        public string CurrentSessionId => _session?.ServerSessionId ?? _session?.SessionId;

        public string CurrentPlayerId => _tokenClaims?.PlayerId;
        public string GameId => _initConfig.GameId;
        public string GameVersionId => _initConfig.GameVersionId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
        public bool IsTokenExpired =>
            _tokenClaims?.ExpirationTime.HasValue == true &&
            _tokenClaims.ExpirationTime.Value <= DateTime.UtcNow;
        public JwtTokenClaims TokenClaims => _tokenClaims;

        internal Dictionary<string, string> GetBaseHeaders()
        {
            var headers = new Dictionary<string, string>(_initConfig.GetBaseHeaders());
            if (!string.IsNullOrEmpty(_accessToken))
                headers["Authorization"] = $"Bearer {_accessToken}";
            return headers;
        }

        /// <summary>
        /// Explicitly refreshes the access token using the stored refresh token.
        /// Throws <see cref="FlockAuthException"/> if no refresh token is available or if the refresh fails.
        /// </summary>
        public async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                throw new FlockAuthException("No refresh token available. Please log in first.");

            bool success = await TryRefreshTokenAsync(cancellationToken);
            if (!success)
                _logger.LogException(new FlockAuthException("Token refresh failed. Please log in again."));
            
            return success;
        }

        internal async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                return false;

            string refreshSnapshot = _refreshToken;
            string playerIdSnapshot = CurrentPlayerId;

            await _refreshSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrEmpty(_refreshToken))
                    return false;

                if (_refreshToken != refreshSnapshot && !string.IsNullOrEmpty(_accessToken))
                    return true;

                PlayerRefreshTokenRequest refreshRequest = new PlayerRefreshTokenRequest { PlayerId = playerIdSnapshot, RefreshToken = refreshSnapshot };
                _logger.LogDebug($"Refresh POST {GetVersionedApiUrl()}/player/token/refresh body={Newtonsoft.Json.JsonConvert.SerializeObject(refreshRequest)}");

                PlayerLoginResponse response = await FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{GetVersionedApiUrl()}/player/token/refresh",
                    refreshRequest,
                    _initConfig.GetBaseHeaders(), cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    ClearTokens();
                    OnSessionExpired?.Invoke();
                    FlockEvents.RaiseAuthExpired();
                    return false;
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _logger.LogInfo("Token refresh successful");
                FlockEvents.RaiseTokenRefreshed();
                return true;
            }
            catch (FlockAuthException e)
            {
                _logger.LogWarning("Token refresh failed: session expired");
                _logger.LogException(e);
                ClearTokens();
                OnSessionExpired?.Invoke();
                FlockEvents.RaiseAuthExpired();
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("Token refresh failed", ex);
                return false;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        internal void ClearTokens()
        {
            _logger.LogInfo("Clearing authentication tokens");

            if (_session != null && _session.IsActive)
            {
                _session.Reset(FlockSessionEndReason.Logout);
            }

            _accessToken = null;
            _refreshToken = null;
            _tokenClaims = null;

            ClearPersistedTokens();
        }

        public string GetApiUrl()
        {
            return _initConfig.ApiUrl;
        }

        /// <summary>
        /// API base URL with the current <see cref="ApiVersion"/> segment appended
        /// (e.g. <c>https://api.flock.example/v1</c>). Use this for every versioned
        /// endpoint call so the version lives in exactly one place.
        /// </summary>
        public string GetVersionedApiUrl()
        {
            return $"{_initConfig.ApiUrl}/{ApiVersion}";
        }

        /// <summary>
        /// Sets in-memory auth state from the given tokens and persists them via
        /// <see cref="FlockInitConfig.TokenStore"/>.
        /// Throws <see cref="FlockAuthException"/> if the access token cannot be parsed
        /// as a JWT — the SDK can't operate without claims, and silent fallback would
        /// leave <see cref="CurrentPlayerId"/> null with no obvious cause.
        /// </summary>
        internal void SetTokens(string accessToken, string refreshToken)
        {
            JwtTokenClaims claims = null;
            if (!string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    claims = JwtTokenParser.Parse(accessToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to parse JWT access token", ex);
                    throw new FlockAuthException(
                        "Server returned an unparseable JWT access token. Cannot establish player " +
                        "session — the auth response was malformed. Verify ApiUrl points at a " +
                        "supported Flock backend.",
                        ex);
                }
            }

            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _tokenClaims = claims;

            if (claims != null)
                _logger.LogDebug($"Token set for PlayerId: {claims.PlayerId}");

            PersistTokens();
        }

        internal StoredTokens LoadPersistedTokens()
        {
            ITokenStore store = _initConfig.TokenStore;
            if (store == null) return null;
            try
            {
                StoredTokens loaded = store.Load();
                _logger.LogDebug($"[{store.GetType().Name}] Load -> {(loaded == null ? "no stored session" : "tokens restored")}");
                return loaded;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{store.GetType().Name}] Load failed: {ex.Message}");
                return null;
            }
        }

        private void PersistTokens()
        {
            ITokenStore store = _initConfig.TokenStore;
            if (store == null) return;
            try
            {
                if (string.IsNullOrEmpty(_accessToken))
                {
                    store.Clear();
                    _logger.LogDebug($"[{store.GetType().Name}] Cleared (empty access token)");
                }
                else
                {
                    store.Save(_accessToken, _refreshToken);
                    _logger.LogDebug($"[{store.GetType().Name}] Saved tokens");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{store.GetType().Name}] Persist failed: {ex.Message}");
            }
        }

        private void ClearPersistedTokens()
        {
            ITokenStore store = _initConfig.TokenStore;
            if (store == null) return;
            try
            {
                store.Clear();
                _logger.LogDebug($"[{store.GetType().Name}] Cleared");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{store.GetType().Name}] Clear failed: {ex.Message}");
            }
        }
    }
}
