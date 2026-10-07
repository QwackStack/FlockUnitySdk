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
        /// Single source of truth — bump here when the backend cuts a new major API version.
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
        // Moves on every sign-in and sign-out, never on a refresh, so a reply sent for one sign-in can tell that it ended.
        private int _signInNumber;
        private string _accessToken;
        private string _refreshToken;
        private JwtTokenClaims _tokenClaims;

        // Clears the static singleton when domain reload is disabled on enter-play-mode,
        // so a fresh play session always starts with no SDK state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
#if !FLOCK_NO_ANALYTICS
            // Unity's log events are static, so the previous play session's capture would go on hearing this one's exceptions.
            (_instance?._analytics as FlockAnalyticsProvider)?.StopListeningForExceptions();
#endif
            // The previous play session's launch lets go of its folder, so this one can take it over.
            _instance?._analyticsLaunches?.Dispose();
#if !FLOCK_NO_MULTIPLAYER
            _instance?._multiplayer?.StopForShutdown();
#endif
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
#if !FLOCK_NO_LEADERBOARD
        private FlockLeaderboardProvider _leaderboard;
#endif
#if !FLOCK_NO_NOTIFICATION
        private FlockNotificationProvider _notification;
#endif
#if !FLOCK_NO_MULTIPLAYER
        private FlockMultiplayerProvider _multiplayer;
#endif
        private FlockSession _session;
        private FlockAnalyticsLaunches _analyticsLaunches;
#if !FLOCK_NO_ANALYTICS
        private IAnalyticProvider _analytics;
#endif

        private FlockClient(FlockInitConfig initConfig, IFlockLogger logger)
        {
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig));
            // Errors and warnings always surface — EnableDebugLogs only adds info/debug on top.
            _logger = logger ?? new UnityFlockLogger(initConfig.EnableDebugLogs);
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

                // Order matters and is the whole fix: rescue state out of the version-scoped tree BEFORE
                // pruning it. A queue left where older builds put it would already be deleted by the time its
                // provider went looking, which is how a game-version change used to lose a player's unsent
                // offline writes.
#if !FLOCK_NO_COMMANDS
                // Only the commands queue was ever kept there; a build without commands has nothing to rescue.
                client._snapshotStore?.MigrateLegacyState(FlockCommandProvider.SnapshotCategory);
#endif
                client._snapshotStore?.PruneOtherVersions(client._initConfig.GameVersionId);
                client._snapshotStore?.DeleteLeftOverFiles();
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
            (_instance._analytics as FlockAnalyticsProvider)?.StopForShutdown();
#endif
#if !FLOCK_NO_COMMANDS
            _instance._commands?.UnsubscribeFlushTriggers();
#endif
#if !FLOCK_NO_MULTIPLAYER
            _instance._multiplayer?.StopForShutdown();
#endif
            _instance.ClearTokens();
            // After the session end is spooled: a later Create takes this launch's folder over.
            _instance._analyticsLaunches?.Dispose();
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
#if !FLOCK_NO_LEADERBOARD
            _leaderboard = new FlockLeaderboardProvider(this);
            #endif
#if !FLOCK_NO_NOTIFICATION
            _notification = new FlockNotificationProvider(this);
#endif
#if !FLOCK_NO_MULTIPLAYER
            _multiplayer = new FlockMultiplayerProvider(this);
            _multiplayer.StartFrameUpdates();
#endif
            _authentication = new FlockAuthProvider(this);

#if !FLOCK_NO_ANALYTICS
            if (_initConfig.AnalyticsConfig.Enabled)
            {
                // Each launch keeps its crash marker, live-session record and queues in a folder of its own, locked while it
                // runs, and takes over the files of launches that have ended.
                string testingFolder = FlockAnalyticsLaunches.FolderForTesting;
                _analyticsLaunches = FlockAnalyticsLaunches.Start(
                    testingFolder ?? FlockAnalyticsLaunches.DefaultFolder(),
                    testingFolder == null ? FlockEarlierBuildFiles.OnThisMachine() : null);
                if (!_analyticsLaunches.IsHoldingItsFolder)
                    _logger.LogWarning("Could not lock a folder for this launch's analytics files, so launches that ended before this one are not reported this time.");
                _session = new FlockSession(_initConfig.AnalyticsConfig, _logger, _analyticsLaunches.SessionStatePath);
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
#if !FLOCK_NO_LEADERBOARD
        public FlockLeaderboardProvider Leaderboard => _leaderboard;
        #endif
#if !FLOCK_NO_NOTIFICATION
        public FlockNotificationProvider Notification => _notification;
#endif
#if !FLOCK_NO_MULTIPLAYER
        public FlockMultiplayerProvider Multiplayer => _multiplayer;
#endif
#if !FLOCK_NO_ANALYTICS
        public IAnalyticProvider Analytics => _analytics;
#endif
        internal FlockSession Session => _session;
        internal FlockAnalyticsLaunches AnalyticsLaunches => _analyticsLaunches;
        public bool HasActiveSession => _session?.IsActive ?? false;
        public string CurrentSessionId => _session?.ServerSessionId ?? _session?.SessionId;

        /// <summary>The id the server gave the current analytics session; null until that session has reached the server.</summary>
        public string ServerSessionId => _session != null && _session.IsActive ? _session.ServerSessionId : null;

        public string CurrentPlayerId => _tokenClaims?.PlayerId;

        /// <summary>Which sign-in the tokens belong to: moves on every sign-in and sign-out, never on a refresh.</summary>
        internal int SignInNumber => _signInNumber;
        public string GameId => _initConfig.GameId;
        /// <summary>The Game Version name this client was initialized with; <see cref="GameVersionId"/> is the ID it resolved to.</summary>
        public string GameVersion => _initConfig.GameVersion;
        public string GameVersionId => _initConfig.GameVersionId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
        public bool IsTokenExpired =>
            _tokenClaims?.ExpirationTime.HasValue == true &&
            _tokenClaims.ExpirationTime.Value <= DateTime.UtcNow;
        public JwtTokenClaims TokenClaims => _tokenClaims;

        /// <summary>A new copy of the headers that identify this game to a Qwacks service: X-Flock-API-Key and X-Game-Version-ID. Never carries the player's sign-in.</summary>
        public Dictionary<string, string> GetGameHeaders() => new Dictionary<string, string>(_initConfig.GetBaseHeaders());

        /// <summary>A copy of the retry settings this client was initialized with, for a service that should retry the same way.</summary>
        public RetryPolicy RetryPolicy => (_initConfig.RetryPolicy ?? new RetryPolicy()).Copy();

        internal Dictionary<string, string> GetBaseHeaders()
        {
            Dictionary<string, string> headers = new Dictionary<string, string>(_initConfig.GetBaseHeaders());
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

        internal Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
            => TryRefreshTokenAsync(_signInNumber, cancellationToken);

        /// <summary>Refreshes the tokens of sign-in <paramref name="signInNumber"/>; false, changing nothing, once that sign-in has ended.</summary>
        internal async Task<bool> TryRefreshTokenAsync(int signInNumber, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                return false;

            string refreshSnapshot = _refreshToken;
            string playerIdSnapshot = CurrentPlayerId;
            int generationSnapshot = _refreshGeneration;

            await _refreshSemaphore.WaitAsync(cancellationToken);
            try
            {
                // The sign-in ended before this got its turn: the tokens now held are another sign-in's, not this one's to refresh.
                if (string.IsNullOrEmpty(_refreshToken) || signInNumber != _signInNumber)
                    return false;

                // Someone refreshed while we waited — piggyback on their result instead of POSTing again.
                // Generation-based so it works even when the backend re-issues the same refresh token.
                if (_refreshGeneration != generationSnapshot && !string.IsNullOrEmpty(_accessToken))
                    return true;

                PlayerRefreshTokenRequest refreshRequest = new PlayerRefreshTokenRequest { PlayerId = playerIdSnapshot, RefreshToken = refreshSnapshot };
                // Never log the body — it carries the refresh token, and Debug.Log reaches player logs and crash reporters.
                _logger.LogDebug($"Refresh POST {GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh} for PlayerId: {playerIdSnapshot}");

                PlayerLoginResponse response = await FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh}",
                    refreshRequest,
                    _initConfig.GetBaseHeaders(), cancellationToken);

                // Applying a reply for an ended sign-in would sign that player back in, or sign the next one out.
                if (IsSignInOver(signInNumber))
                    return false;

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    ClearTokens();
                    OnSessionExpired?.Invoke();
                    FlockEvents.InvokeAuthExpired();
                    return false;
                }

                StoreTokens(response.AccessToken, response.RefreshToken);
                _refreshGeneration++;
                _logger.LogInfo("Token refresh successful");
                FlockEvents.InvokeTokenRefreshed();
                return true;
            }
            catch (FlockAuthException e)
            {
                if (IsSignInOver(signInNumber))
                    return false;
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

        // True, and logged, when the sign-in a refresh was sent for has ended.
        private bool IsSignInOver(int signInNumber)
        {
            if (signInNumber == _signInNumber)
                return false;
            _logger.LogDebug("Token refresh reply ignored: the player signed out or changed while it was on its way");
            return true;
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
            _signInNumber++;

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
        /// Starts a new sign-in with the given tokens (a login or a restore, never a refresh) and persists them via
        /// <see cref="FlockInitConfig.TokenStore"/>.
        /// Throws <see cref="FlockAuthException"/> if the access token cannot be parsed
        /// as a JWT — the SDK can't operate without claims, and silent fallback would
        /// leave <see cref="CurrentPlayerId"/> null with no obvious cause.
        /// </summary>
        internal void SetTokens(string accessToken, string refreshToken)
        {
            StoreTokens(accessToken, refreshToken);
            _signInNumber++;
        }

        // Keeps the tokens of the current sign-in; a refresh stores through here so its sign-in number stays.
        private void StoreTokens(string accessToken, string refreshToken)
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
