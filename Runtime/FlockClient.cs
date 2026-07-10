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

        /// <summary>The initialized SDK singleton. Throws <see cref="FlockException"/> if accessed before <see cref="Create"/>.</summary>
        public static FlockClient Instance
        {
            get
            {
                if (_instance == null)
                    throw new FlockException(
                        "FlockClient has not been initialized. Call 'FlockClient.Create(config)' once at startup before accessing FlockClient.Instance.");
                return _instance;
            }
        }

        /// <summary>True once <see cref="Create"/> has successfully run.</summary>
        public static bool IsInitialized => _instance != null;

        /// <summary>True while a persisted session is being restored — bind UI to this for a startup spinner.</summary>
        public static bool IsRestoringSession { get; internal set; }

        /// <summary>The exception from the last failed <see cref="Create"/> attempt; null after a success or before any attempt. Set even on the auto-init path (which logs instead of throwing) — check it alongside <see cref="IsInitialized"/> to detect a failed startup.</summary>
        public static Exception InitializationError { get; private set; }

        private readonly FlockInitConfig _initConfig;
        private readonly IFlockLogger _logger;
        private readonly RetryHandler _retryHandler;
        private readonly FlockSnapshotStore _snapshotStore;
        //To avoid refresh deadlocks
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        // Bumped on every successful refresh so queued waiters can detect "someone already refreshed" without
        // relying on the refresh token rotating (the backend may return the same one).
        private int _refreshGeneration;
        private string _accessToken;
        private string _refreshToken;
        private JwtTokenClaims _tokenClaims;

        // Clears the static singleton when domain reload is disabled on enter-play-mode,
        // so a fresh play session always starts with no SDK state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _instance = null;
            IsRestoringSession = false;
            InitializationError = null;
        }

        /// <summary>Token refresh failed — re-login required. Prefer <see cref="FlockEvents.OnAuthExpired"/>; kept for back-compat.</summary>
        public event Action OnSessionExpired;

        private FlockAuthProvider _authentication;
#if !FLOCK_NO_CONFIG
        private FlockConfigProvider _config;
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
            FlockHttpClient.Configure(initConfig.HttpTimeout);
            _snapshotStore = initConfig.EnableOfflineCache
                ? new FlockSnapshotStore(initConfig.OfflineCacheDirectory, _logger)
                : null;
        }

        /// <summary>Synchronously creates the SDK from a config with a baked Game Version ID — no network. Throws if the ID is missing; raises <see cref="FlockEvents.OnInitialized"/>/<see cref="FlockEvents.OnInitializationFailed"/>.</summary>
        public static FlockClient Create(FlockInitConfig initConfig, IFlockLogger logger = null)
        {
            // Misuse guard — no OnInitializationFailed: the SDK is already initialized and working.
            if (_instance != null)
                throw new FlockException(
                    "FlockClient is already initialized. Call FlockClient.Shutdown() first if you need to reinitialize with a different config.");

            try
            {
                FlockClient client = new FlockClient(initConfig, logger);

                if (string.IsNullOrEmpty(client._initConfig.GameVersionId))
                    throw new FlockValidationException(
                        "Game Version not resolved. Open Flock > Settings while online to resolve your " +
                        "Game Version, then rebuild. The Game Version ID is baked into FlockConfig at " +
                        "edit time — runtime init never contacts the server.");

                client._snapshotStore?.PruneOtherVersions(client._initConfig.GameVersionId);
                client.InitializeServices();
                _instance = client;
                InitializationError = null;
            }
            catch (Exception ex)
            {
                InitializationError = ex;
                FlockEvents.InvokeInitializationFailed(ex);
                throw;
            }

            FlockEvents.InvokeInitialized();
            return _instance;
        }

        /// <summary>
        /// Clears the global <see cref="Instance"/>, allowing <see cref="Create"/> to be
        /// called again. Logs out the current player first so the token state is dropped.
        /// Invokes <see cref="FlockEvents.OnShutdown"/> last, then clears all <see cref="FlockEvents"/> subscriptions.
        /// </summary>
        public static void Shutdown()
        {
            InitializationError = null;
            if (_instance == null) return;
#if !FLOCK_NO_ANALYTICS
            (_instance._analytics as FlockAnalyticsProvider)?.UninstallGlobalExceptionHook();
#endif
#if !FLOCK_NO_COMMANDS
            _instance._commands?.UnsubscribeFlushTriggers();
#endif
            _instance.ClearTokens();
            _instance = null;
            FlockEvents.InvokeShutdown();
            FlockEvents.ClearAll();
            FlockEvents.Logger = null;
        }

        private void InitializeServices()
        {
#if !FLOCK_NO_PLAYER
            _playerData = new PlayerProvider(this);
#endif
#if !FLOCK_NO_CONFIG
            _config = new FlockConfigProvider(this);
#endif
#if !FLOCK_NO_GAME
            _game = new FlockGameProvider(this);
#endif
#if !FLOCK_NO_COMMANDS
            _commands = new FlockCommandProvider(this);
            _commands.SubscribeFlushTriggers();
#endif
#if !FLOCK_NO_SHOP
            _shop = new FlockShopProvider(this);
#endif
#if !FLOCK_NO_ASSET
            _asset = new FlockAssetProvider(this);
#endif
            _authentication = new FlockAuthProvider(this);

#if !FLOCK_NO_ANALYTICS
            if (_initConfig.AnalyticsConfig.Enabled)
            {
                _session = new FlockSession(_initConfig.AnalyticsConfig, _logger);
                _analytics = new FlockAnalyticsProvider(this);
            }
            else
            {
                _analytics = new NullAnalyticsProvider(this);
            }
#endif
        }

        internal IFlockLogger Logger => _logger;
        internal RetryHandler RetryHandler => _retryHandler;
        internal FlockInitConfig InitConfig => _initConfig;
        internal FlockSnapshotStore SnapshotStore => _snapshotStore;

        // Reachability seam: production reads Application.internetReachability; tests override to force offline.
        // Non-behavioral — the default is identical to the previous inline check.
        internal Func<bool> ReachabilityProbe = () =>
            Application.internetReachability != NetworkReachability.NotReachable;

        internal bool IsReachable() => ReachabilityProbe();

        public FlockAuthProvider Authentication => _authentication;
#if !FLOCK_NO_CONFIG
        public FlockConfigProvider Config => _config;
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
            int generationSnapshot = _refreshGeneration;

            await _refreshSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrEmpty(_refreshToken))
                    return false;

                // Someone refreshed while we waited — piggyback on their result instead of POSTing again.
                // Generation-based so it works even when the backend re-issues the same refresh token.
                if (_refreshGeneration != generationSnapshot && !string.IsNullOrEmpty(_accessToken))
                    return true;

                PlayerRefreshTokenRequest refreshRequest = new PlayerRefreshTokenRequest { PlayerId = playerIdSnapshot, RefreshToken = refreshSnapshot };
                _logger.LogDebug($"Refresh POST {GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh} body={Newtonsoft.Json.JsonConvert.SerializeObject(refreshRequest)}");

                PlayerLoginResponse response = await FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh}",
                    refreshRequest,
                    _initConfig.GetBaseHeaders(), cancellationToken);

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    ClearTokens();
                    OnSessionExpired?.Invoke();
                    FlockEvents.InvokeAuthExpired();
                    return false;
                }

                SetTokens(response.AccessToken, response.RefreshToken);
                _refreshGeneration++;
                _logger.LogInfo("Token refresh successful");
                FlockEvents.InvokeTokenRefreshed();
                return true;
            }
            catch (FlockAuthException e)
            {
                _logger.LogWarning("Token refresh failed: session expired");
                _logger.LogException(e);
                ClearTokens();
                OnSessionExpired?.Invoke();
                FlockEvents.InvokeAuthExpired();
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

#if !FLOCK_NO_ANALYTICS
            (_analytics as FlockAnalyticsProvider)?.HandleAuthCleared();
#endif

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
