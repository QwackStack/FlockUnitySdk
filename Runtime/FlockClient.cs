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
                    throw new FlockException("FlockClient has not been initialized") { Hint = FlockErrorHints.InitializeFlockFirst };
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
        // Set by Shutdown: an answer this client gets afterwards keeps no sign-in and announces nothing to the next client's listeners.
        private bool _shutDown;

        // A local player's client: the client that added it, and the player it serves; both null on the main client.
        private readonly FlockClient _mainClient;
        private FlockLocalPlayer _localPlayer;
        // The local players this main client added and has not ended.
        private readonly List<FlockLocalPlayer> _localPlayers = new List<FlockLocalPlayer>();
        // Why this local player's client ended, and how to carry on; null while it serves.
        private string _endedCause;
        private string _endedFix;
        // The tokens this sign-in held before another sign-in here as the same player handed it theirs: tried once if the handed-over
        // refresh token is refused, as the server may have taken the two sign-ins in the other order.
        private StoredTokens _tokensBeforeHandover;
        // The refresh token this main client held when it shut down, which its saved sign-in keeps for the next start.
        private string _refreshTokenWhenShutDown;

        internal const string LocalPlayerRemovedCause = "This local player was removed with RemoveLocalPlayer()";
        internal const string LocalPlayerEndedByShutdownCause = "Flock was shut down, which ended this local player";
        internal const string LocalPlayerSignedInAsTheMainPlayerCause = "This local player was ended because the main player signed in as the same player.";
        private const string LocalPlayerSignedInAsTheMainPlayerFix = "Sign each player in once on a machine: use the main player for them, or add a new local player and sign them in as someone else.";
        private const string LocalPlayerEndedFix = "Add a new one with FlockClient.Instance.AddLocalPlayer() and sign them in again (after a shutdown, once FlockClient.Create has run).";
        private const string LocalPlayerNotSignedInFix = "Sign this local player in first, through their own Authentication: LoginWithDeviceAsync(), LoginWithEmailAsync() "
            + "or a register call. A local player has no saved sign-in to restore.";

        // Every refresh token a client in this process sent and has no answer for yet, never logged: the server takes each one once,
        // so a client restarted while the one before it waits for an answer sends the same token only after that answer.
        private static readonly Dictionary<string, Task> RefreshTokensOnTheirWay = new Dictionary<string, Task>(StringComparer.Ordinal);

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
            _instance?.StopRepeatingCalls();
            _instance?.EndLocalPlayers(LocalPlayerEndedByShutdownCause);
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
        private FlockRepeatingCalls _repeatingCalls;
        // The game's frames the repeating calls run on, held while they do.
        private FlockBehaviour _repeatingCallsFrames;
        private FlockSession _session;
        private FlockAnalyticsLaunches _analyticsLaunches;
#if !FLOCK_NO_ANALYTICS
        private IAnalyticProvider _analytics;
#endif

        private FlockClient(FlockInitConfig initConfig, IFlockLogger logger)
        {
            _initConfig = initConfig ?? throw new ArgumentNullException(nameof(initConfig), "Pass the config to start Flock with, such as the one Flock > Settings saves (FlockConfigAsset).");
            // Errors and warnings always surface — EnableDebugLogs only adds info/debug on top.
            _logger = logger ?? new UnityFlockLogger(initConfig.EnableDebugLogs);
            FlockEvents.Logger = _logger;
            _logger.LogInfo("Initializing Flock SDK");
            ITokenStore tokenStore = initConfig.TokenStore;
            _logger.LogInfo($"Token persistence provider: {(tokenStore is FlockGameTokenStore game ? game.PlatformStoreName + " (one sign-in for each game)" : tokenStore?.GetType().Name ?? "<disabled>")}");
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
                throw new FlockException("FlockClient is already initialized")
                {
                    Hint = "Use FlockClient.Instance, or call FlockClient.Shutdown() first to start Flock again with a different config."
                };

            try
            {
                FlockClient client = new FlockClient(initConfig, logger);

                if (string.IsNullOrEmpty(client._initConfig.GameVersionId))
                    throw new FlockValidationException("The Game Version is not resolved, so Flock cannot start")
                {
                    Hint = "Open Flock > Settings while online so it resolves the Game Version, then build again: its id is saved into the build "
                        + "at edit time, and starting Flock never asks the server for it."
                };

                // Order matters and is the whole fix: rescue state out of the version-scoped tree BEFORE
                // pruning it. A queue left where older builds put it would already be deleted by the time its
                // provider went looking, which is how a game-version change used to lose a player's unsent
                // offline writes.
#if !FLOCK_NO_COMMANDS
                // Only the commands queue was ever kept there; a build without commands has nothing to rescue.
                client._snapshotStore?.MigrateLegacyState(FlockCommandProvider.SnapshotCategory);
#endif
#if !FLOCK_NO_NOTIFICATION
                // The seen-watermark and the reminders a player can cancel were kept beside the inbox cache until 1.86.0.
                client._snapshotStore?.MigrateLegacyStateFiles(FlockNotificationProvider.SnapshotCategory,
                    FlockNotificationProvider.PendingSchedulesKey, FlockNotificationProvider.WatermarkKey);
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
        /// called again. Ends the analytics session as a quit and lets go of this client's tokens; the saved
        /// sign-in stays, so the next <see cref="Create"/> restores the player (call
        /// <c>Authentication.Logout()</c> first to sign the player out). A login this client gets an answer to afterwards fails and
        /// keeps nothing, and a restore, link or unlink it finishes afterwards raises no event.
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
            _instance.StopRepeatingCalls();
            _instance.EndLocalPlayers(LocalPlayerEndedByShutdownCause);
            _instance._shutDown = true;
            _instance._refreshTokenWhenShutDown = _instance._refreshToken;
            _instance.EndSignIn(FlockSessionEndReason.Quit, deleteSavedSignIn: false);
            // A restore under way was this client's; the next client's own restore sets it again.
            IsRestoringSession = false;
            // After the session end is spooled: a later Create takes this launch's folder over.
            _instance._analyticsLaunches?.Dispose();
            _instance = null;
            FlockEvents.InvokeShutdown();
            FlockEvents.ClearAll();
            FlockEvents.Logger = null;
        }

        private void InitializeServices()
        {
            _repeatingCalls = new FlockRepeatingCalls(this);
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
            // After multiplayer's: each frame its sessions check their sign-in before the calls due are sent.
            StartRepeatingCalls();
            _authentication = new FlockAuthProvider(this);

#if !FLOCK_NO_ANALYTICS
            if (_initConfig.AnalyticsConfig.Enabled)
            {
                // Each launch keeps its crash marker, live-session record and queues in a folder of its own among its game's, locked
                // while it runs, and takes over the files of the game's launches that have ended.
                string testingFolder = FlockAnalyticsLaunches.FolderForTesting;
                _analyticsLaunches = FlockAnalyticsLaunches.Start(
                    testingFolder ?? FlockAnalyticsLaunches.DefaultFolder(), _initConfig.GameKey,
                    testingFolder == null ? FlockEarlierBuildFiles.OnThisMachine() : null);
                if (!_analyticsLaunches.IsHoldingItsFolder)
                    _logger.LogWarning("Could not lock a folder for this launch's analytics files, so this launch keeps none: what it cannot send now is lost, and launches that ended before it are not reported this time.");
                _session = new FlockSession(_initConfig.AnalyticsConfig, _logger, _analyticsLaunches.SessionStatePath);
                _analytics = new FlockAnalyticsProvider(this);
            }
            else
            {
                _analytics = new NullAnalyticsProvider(this);
            }
#endif
        }

        // A local player's client: the main client's config, retries and saved answers, but tokens held in memory only, its own
        // events, and only the player's own surfaces. Never the instance, so it reconfigures nothing shared.
        private FlockClient(FlockClient mainClient)
        {
            _mainClient = mainClient;
            _initConfig = mainClient._initConfig;
            _logger = mainClient._logger;
            _retryHandler = mainClient._retryHandler;
            _snapshotStore = mainClient._snapshotStore;
#if !FLOCK_NO_PLAYER
            _playerData = new PlayerProvider(this);
#endif
#if !FLOCK_NO_COMMANDS
            _commands = new FlockCommandProvider(this);
#endif
#if !FLOCK_NO_LEADERBOARD
            _leaderboard = new FlockLeaderboardProvider(this);
#endif
            _authentication = new FlockAuthProvider(this);
        }

        /// <summary>
        /// Adds a player who signs in beside the others on this machine, for couch and booth games. Returned signed out: sign them
        /// in through its <see cref="FlockLocalPlayer.Authentication"/>, then use its Player, Commands and Leaderboard as you use
        /// this client's. Kept in memory only; its sign-ins are raised on its own events, not on <see cref="FlockEvents"/>.
        /// Throws a <see cref="FlockException"/> on a client that was shut down.
        /// </summary>
        public FlockLocalPlayer AddLocalPlayer()
        {
            if (_shutDown)
                throw new FlockException("This FlockClient was shut down, so it cannot add a local player")
                {
                    Hint = "Add local players through FlockClient.Instance once FlockClient.Create has run."
                };
            FlockClient client = new FlockClient(this);
            FlockLocalPlayer player = new FlockLocalPlayer(client);
            client._localPlayer = player;
#if !FLOCK_NO_COMMANDS
            client._commands.SubscribeFlushTriggers();
#endif
            _localPlayers.Add(player);
            return player;
        }

        /// <summary>
        /// Signs <paramref name="localPlayer"/> out on this machine and ends them: every later call through them fails with a
        /// <see cref="FlockAuthException"/> saying so, with a fix. Raises their <see cref="FlockLocalPlayer.OnLoggedOut"/> if they
        /// were signed in. A player already removed, added before a shutdown, or null is refused with a <see cref="FlockValidationException"/>.
        /// </summary>
        public void RemoveLocalPlayer(FlockLocalPlayer localPlayer)
        {
            if (localPlayer == null || !_localPlayers.Remove(localPlayer))
                throw new FlockValidationException("That local player is not one of this client's: it was already removed, or Flock was shut down since it was added")
                {
                    Hint = "Remove each local player once. After FlockClient.Create, add and sign in the players again."
                };
            bool wasSignedIn = localPlayer.Client.IsAuthenticated;
            localPlayer.Client.EndLocalPlayer(LocalPlayerRemovedCause);
            if (wasSignedIn)
                localPlayer.RaiseLoggedOut();
        }

        /// <summary>The local players this client added and has not ended yet.</summary>
        internal IReadOnlyList<FlockLocalPlayer> LocalPlayers => _localPlayers;

        /// <summary>The local player this client serves; null on the main client.</summary>
        internal FlockLocalPlayer LocalPlayer => _localPlayer;

        /// <summary>True on a local player's client.</summary>
        internal bool IsLocalPlayer => _localPlayer != null;

        // The main player signed in as a player a local player holds: the server's one sign-in for that player is now the main player's,
        // so the local player ends here, with nothing sent (a revoke would end the main sign-in).
        internal void EndLocalPlayersSignedInAsTheMainPlayer()
        {
            string playerId = CurrentPlayerId;
            foreach (FlockLocalPlayer player in _localPlayers.FindAll(p => p.Client.IsAuthenticated && p.Client.CurrentPlayerId == playerId))
            {
                _localPlayers.Remove(player);
                player.Client.EndLocalPlayer(LocalPlayerSignedInAsTheMainPlayerCause, LocalPlayerSignedInAsTheMainPlayerFix);
                player.RaiseLoggedOut();
            }
        }

        private void EndLocalPlayers(string cause)
        {
            foreach (FlockLocalPlayer player in _localPlayers)
                player.Client.EndLocalPlayer(cause);
            _localPlayers.Clear();
        }

        // Ends this local player's client: the sign-in goes, a reply still on its way keeps nothing, and every later call refuses.
        private void EndLocalPlayer(string cause, string fix = LocalPlayerEndedFix)
        {
            _endedCause = cause;
            _endedFix = fix;
            _shutDown = true;
#if !FLOCK_NO_COMMANDS
            // The game's frames hold the flush triggers, which would keep this player alive for good.
            _commands?.UnsubscribeFlushTriggers();
#endif
            EndSignIn(FlockSessionEndReason.Logout, deleteSavedSignIn: false);
        }

        // The failure every call through an ended local player gives.
        private FlockAuthException EndedFailure() => new FlockAuthException(_endedCause) { Hint = _endedFix };

        /// <summary>The failure a call needing a signed-in player gives when there is none: an ended local player says why, and one not signed in yet is told to sign in through its own Authentication.</summary>
        internal FlockAuthException NoPlayerSignedIn()
        {
            if (_endedCause != null)
                return EndedFailure();
            return new FlockAuthException("No player is signed in") { Hint = IsLocalPlayer ? LocalPlayerNotSignedInFix : FlockErrorHints.SignInFirst };
        }

        /// <summary>Throws the ended failure once this local player has ended; a write queued offline would otherwise be kept for no one.</summary>
        internal void RefuseIfEnded()
        {
            if (_endedCause != null)
                throw EndedFailure();
        }

        // The client on this machine signed in as playerId, other than asking: the main client or a local player's; null when none.
        private FlockClient ClientSignedInAs(string playerId, FlockClient asking)
        {
            if (this != asking && IsAuthenticated && CurrentPlayerId == playerId)
                return this;
            foreach (FlockLocalPlayer player in _localPlayers)
                if (player.Client != asking && player.Client.IsAuthenticated && player.Client.CurrentPlayerId == playerId)
                    return player.Client;
            return null;
        }

        // A local player's sign-in as a player already signed in here: the server keeps one refresh token per player and this answer
        // has just replaced it, so the sign-in already here takes the new tokens and this one is refused.
        private void RefuseAPlayerAlreadySignedInHere(string accessToken, string refreshToken)
        {
            string playerId;
            try
            {
                playerId = JwtTokenParser.Parse(accessToken).PlayerId;
            }
            catch (Exception)
            {
                return; // StoreTokens refuses it, with its own cause.
            }
            FlockClient holder = _mainClient.ClientSignedInAs(playerId, this);
            if (holder == null)
            {
                // Answered after Flock was shut down holding this player: the sign-in saved for the next start had its token replaced.
                _mainClient.SaveWhereTheSentSignInIsStillSaved(playerId, _mainClient._refreshTokenWhenShutDown, accessToken, refreshToken);
                return;
            }
            holder._tokensBeforeHandover = new StoredTokens { AccessToken = holder._accessToken, RefreshToken = holder._refreshToken };
            holder.StoreTokens(accessToken, refreshToken);
            holder._refreshGeneration++;
            _logger.LogInfo($"Player {playerId} is already signed in on this machine; that sign-in carries on with the new tokens");
            RefuseIfEnded();
            throw new FlockAuthException($"Player {playerId} is already signed in on this machine, so this local player was not signed in")
            {
                Code = "flock.player_already_signed_in",
                Hint = FlockErrorHints.For(FlockErrorCode.FlockPlayerAlreadySignedIn)
            };
        }

        // Every sign-in, sign-out and refresh event goes to the local player's own events on a local player's client.
        internal void AnnounceAuthenticated(FlockAuthInfo info)
        {
            if (IsLocalPlayer) _localPlayer.RaiseAuthenticated(info);
            else FlockEvents.InvokeAuthenticated(info);
        }

        internal void AnnounceLoggedOut()
        {
            if (IsLocalPlayer) _localPlayer.RaiseLoggedOut();
            else FlockEvents.InvokeLoggedOut();
        }

        internal void AnnounceAccountLinked(FlockCredentialProvider provider)
        {
            if (IsLocalPlayer) _localPlayer.RaiseAccountLinked(provider);
            else FlockEvents.InvokeAccountLinked(provider);
        }

        internal void AnnounceAccountUnlinked(FlockCredentialProvider provider)
        {
            if (IsLocalPlayer) _localPlayer.RaiseAccountUnlinked(provider);
            else FlockEvents.InvokeAccountUnlinked(provider);
        }

        private void AnnounceTokenRefreshed()
        {
            if (IsLocalPlayer) _localPlayer.RaiseTokenRefreshed();
            else FlockEvents.InvokeTokenRefreshed();
        }

        private void AnnounceAuthExpired()
        {
            OnSessionExpired?.Invoke();
            if (IsLocalPlayer) _localPlayer.RaiseAuthExpired();
            else FlockEvents.InvokeAuthExpired();
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

        /// <summary>The sign-in the analytics session named by <see cref="ServerSessionId"/> started under, which can differ from the current one.</summary>
        internal int ServerSessionSignInNumber => _session != null && _session.IsActive ? _session.StartedUnderSignIn : 0;

        /// <summary>True once <see cref="Shutdown"/> let go of this client.</summary>
        internal bool HasShutDown => _shutDown;

        /// <summary>The one owner of every call the SDK repeats; run on the game's frames while Flock runs.</summary>
        internal FlockRepeatingCalls RepeatingCalls => _repeatingCalls;

        // The repeating calls run on the game's frames, so a web player needs no timers; quitting stops them.
        private void StartRepeatingCalls()
        {
            if (_repeatingCallsFrames != null || !Application.isPlaying)
                return;
            FlockBehaviour behaviour = FlockBehaviour.Instance;
            if (behaviour == null)
                return;
            _repeatingCallsFrames = behaviour;
            behaviour.OnTick += _repeatingCalls.SendThoseDue;
            behaviour.OnQuit += _repeatingCalls.StopAll;
        }

        // Flock shutting down: every repeating call stops, and the game's frames are let go.
        private void StopRepeatingCalls()
        {
            if (_repeatingCallsFrames != null)
            {
                _repeatingCallsFrames.OnTick -= _repeatingCalls.SendThoseDue;
                _repeatingCallsFrames.OnQuit -= _repeatingCalls.StopAll;
                _repeatingCallsFrames = null;
            }
            _repeatingCalls?.StopAll();
        }

        public string GameId => _initConfig.GameId;
        /// <summary>The Game Version name this client was initialized with; <see cref="GameVersionId"/> is the ID it resolved to.</summary>
        public string GameVersion => _initConfig.GameVersion;
        public string GameVersionId => _initConfig.GameVersionId;
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
        public bool IsTokenExpired =>
            _tokenClaims?.ExpirationTime.HasValue == true &&
            _tokenClaims.ExpirationTime.Value <= DateTime.UtcNow;
        public JwtTokenClaims TokenClaims => _tokenClaims;

        /// <summary>The signed-in player's access token, raw (no "Bearer "), for a Qwacks service that checks who is playing; null when no one is signed in. Never log it.</summary>
        internal string PlayerAccessToken => string.IsNullOrEmpty(_accessToken) ? null : _accessToken;

        /// <summary>A new copy of the headers that identify this game to a Qwacks service: X-Flock-API-Key and X-Game-Version-ID. Never carries the player's sign-in.</summary>
        public Dictionary<string, string> GetGameHeaders() => new Dictionary<string, string>(_initConfig.GetBaseHeaders());

        /// <summary>A copy of the retry settings this client was initialized with, for a service that should retry the same way.</summary>
        public RetryPolicy RetryPolicy => (_initConfig.RetryPolicy ?? new RetryPolicy()).Copy();

        internal Dictionary<string, string> GetBaseHeaders()
        {
            // Every request and every retry takes its headers here, so an ended local player sends nothing more.
            RefuseIfEnded();
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
                throw new FlockAuthException("There is no saved sign-in to renew") { Hint = FlockErrorHints.SignInFirst };

            bool success = await TryRefreshTokenAsync(cancellationToken);
            if (!success)
                _logger.LogException(new FlockAuthException("The player's sign-in could not be renewed")
                {
                    Hint = "Sign the player in again. If the device was offline, wait for the connection and renew again first."
                });
            
            return success;
        }

        internal Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
            => TryRefreshTokenAsync(_signInNumber, cancellationToken);

        /// <summary>Refreshes the tokens of sign-in <paramref name="signInNumber"/>; false, changing nothing in this client, once that sign-in has ended.</summary>
        internal async Task<bool> TryRefreshTokenAsync(int signInNumber, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_refreshToken))
                return false;

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

                // The tokens held now, not those held before the wait: a saved sign-in taken meanwhile replaced them.
                return await SendRefreshAsync(signInNumber, CurrentPlayerId, _refreshToken, mayTakeTheSavedSignIn: true, cancellationToken);
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        // Sends one refresh while the caller holds the refresh turn, so the one sent again after taking a saved sign-in never waits for it.
        private async Task<bool> SendRefreshAsync(int signInNumber, string playerId, string sentRefreshToken, bool mayTakeTheSavedSignIn,
            CancellationToken cancellationToken)
        {
            // A client shut down while its refresh was on its way sent this same token: its answer is waited for, and the pair it
            // saved is taken rather than the token sent a second time.
            Task earlierRefresh = RefreshOnItsWayWith(sentRefreshToken);
            if (earlierRefresh != null)
            {
                _logger.LogDebug("Waiting for the answer to a refresh an earlier start of Flock sent with the same refresh token");
                // The earlier refresh's answer always comes (its request has a timeout), so a caller that cannot give up waits for it.
                if (cancellationToken.CanBeCanceled)
                    await FlockWaiting.FinishedBeforeGivenUpAsync(earlierRefresh, cancellationToken);
                else
                    await earlierRefresh;
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSignInOver(signInNumber))
                    return false;
                if (mayTakeTheSavedSignIn && TakeANewerSavedSignIn(playerId, sentRefreshToken))
                    return await CarryOnWithTheSavedSignInAsync(signInNumber, playerId, cancellationToken);
            }

            TaskCompletionSource<bool> answered = new TaskCompletionSource<bool>();
            bool triedTheTokensBeforeAHandover = false;
            lock (RefreshTokensOnTheirWay)
                RefreshTokensOnTheirWay[sentRefreshToken] = answered.Task;
            try
            {
                PlayerRefreshTokenRequest refreshRequest = new PlayerRefreshTokenRequest { PlayerId = playerId, RefreshToken = sentRefreshToken };
                // Never log the body — it carries the refresh token, and Debug.Log reaches player logs and crash reporters.
                _logger.LogDebug($"Refresh POST {GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh} for PlayerId: {playerId}");

                PlayerLoginResponse response = await FlockHttpClient.PostAsync<PlayerLoginResponse>(
                    $"{GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh}",
                    refreshRequest,
                    _initConfig.GetBaseHeaders(), cancellationToken);

                // Applying a reply for an ended sign-in would sign that player back in, or sign the next one out. The server no longer
                // takes the refresh token it replaced, though, so a shutdown's saved sign-in gets the new pair.
                if (IsSignInOver(signInNumber))
                {
                    if (response != null && !string.IsNullOrEmpty(response.AccessToken))
                        SaveWhereTheSentSignInIsStillSaved(playerId, sentRefreshToken, response.AccessToken, response.RefreshToken);
                    return false;
                }

                if (WasHandedNewerTokens(sentRefreshToken))
                {
                    _refreshGeneration++;
                    AnnounceTokenRefreshed();
                    return true;
                }

                if (response == null || string.IsNullOrEmpty(response.AccessToken))
                {
                    ClearTokens();
                    AnnounceAuthExpired();
                    return false;
                }

                StoreTokens(response.AccessToken, response.RefreshToken);
                _tokensBeforeHandover = null;
                _refreshGeneration++;
                _logger.LogInfo("Token refresh successful");
                AnnounceTokenRefreshed();
                return true;
            }
            catch (FlockAuthException e)
            {
                if (IsSignInOver(signInNumber))
                    return false;
                if (!WasHandedNewerTokens(sentRefreshToken) && TakeTheTokensHeldBeforeAHandover(sentRefreshToken))
                    triedTheTokensBeforeAHandover = true;
                else if (!WasHandedNewerTokens(sentRefreshToken) && (!mayTakeTheSavedSignIn || !TakeANewerSavedSignIn(playerId, sentRefreshToken)))
                {
                    _logger.LogWarning("Token refresh failed: session expired");
                    _logger.LogException(e);
                    ClearTokens();
                    AnnounceAuthExpired();
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Token refresh failed", ex);
                return false;
            }
            finally
            {
                // After a late answer's save, so a client waiting on it finds the new pair. A token has one refresh on its way at a time,
                // as any other waits for it before sending.
                lock (RefreshTokensOnTheirWay)
                    RefreshTokensOnTheirWay.Remove(sentRefreshToken);
                answered.TrySetResult(true);
            }

            if (triedTheTokensBeforeAHandover)
                return await SendRefreshAsync(signInNumber, playerId, _refreshToken, mayTakeTheSavedSignIn: false, cancellationToken);
            return await CarryOnWithTheSavedSignInAsync(signInNumber, playerId, cancellationToken);
        }

        // The tokens held before a handover, taken back once the handed-over refresh token was refused; false when there are none.
        private bool TakeTheTokensHeldBeforeAHandover(string sentRefreshToken)
        {
            StoredTokens before = _tokensBeforeHandover;
            _tokensBeforeHandover = null;
            if (before == null || string.Equals(before.RefreshToken, sentRefreshToken, StringComparison.Ordinal))
                return false;
            StoreTokens(before.AccessToken, before.RefreshToken);
            _logger.LogInfo("The tokens handed over were refused; trying the ones this sign-in held before");
            return true;
        }

        // The same sign-in carries on with the saved pair taken; one whose access token has run out is renewed once more, never taken again.
        private async Task<bool> CarryOnWithTheSavedSignInAsync(int signInNumber, string playerId, CancellationToken cancellationToken)
        {
            if (IsTokenExpired)
                return await SendRefreshAsync(signInNumber, playerId, _refreshToken, mayTakeTheSavedSignIn: false, cancellationToken);
            _refreshGeneration++;
            AnnounceTokenRefreshed();
            return true;
        }

        /// <summary>True while any client in this process waits for the answer to a refresh it sent.</summary>
        internal static bool AnyRefreshOnItsWay
        {
            get
            {
                lock (RefreshTokensOnTheirWay)
                    return RefreshTokensOnTheirWay.Count > 0;
            }
        }

        // The answer still awaited for a refresh sent with this token, by any client in this process; null when there is none.
        private static Task RefreshOnItsWayWith(string refreshToken)
        {
            lock (RefreshTokensOnTheirWay)
                return RefreshTokensOnTheirWay.TryGetValue(refreshToken, out Task answered) ? answered : null;
        }

        // Another start of Flock, or another run of the game sharing the save, renewed this player's sign-in first, so the server
        // refused the refresh token sent here as replaced: the saved pair is taken as this sign-in's, its number kept.
        private bool TakeANewerSavedSignIn(string playerId, string sentRefreshToken)
        {
            StoredTokens saved = LoadPersistedTokens();
            if (saved == null || string.IsNullOrEmpty(saved.RefreshToken) || string.Equals(saved.RefreshToken, sentRefreshToken, StringComparison.Ordinal)
                || !IsSavedFor(saved, playerId))
                return false;
            StoreTokens(saved.AccessToken, saved.RefreshToken);
            _logger.LogInfo("The player's sign-in was renewed by another start of Flock or another run of the game; carrying on with it");
            return true;
        }

        // A refresh answered after its sign-in ended: saved only while the save is still that player's with the refresh token sent,
        // as after a shutdown. A sign-out cleared it and another sign-in replaced it, so those are left alone.
        private void SaveWhereTheSentSignInIsStillSaved(string playerId, string sentRefreshToken, string accessToken, string refreshToken)
        {
            StoredTokens saved = LoadPersistedTokens();
            if (saved == null || string.IsNullOrEmpty(sentRefreshToken) || !string.Equals(saved.RefreshToken, sentRefreshToken, StringComparison.Ordinal)
                || !IsSavedFor(saved, playerId))
                return;
            ITokenStore store = TokenStore;
            try
            {
                store.Save(accessToken, refreshToken);
                _logger.LogDebug($"[{store.GetType().Name}] Saved the renewed tokens of a sign-in that had ended, as the server no longer takes the ones they replaced");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{store.GetType().Name}] Persist failed: {ex.Message}");
            }
        }

        // Whether the saved access token names this player; one that cannot be read names no one.
        private static bool IsSavedFor(StoredTokens saved, string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(saved.AccessToken))
                return false;
            try
            {
                return string.Equals(JwtTokenParser.Parse(saved.AccessToken).PlayerId, playerId, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A local player's sign-in as this player handed over newer tokens while the refresh was on its way; the server holds only those.
        private bool WasHandedNewerTokens(string sentRefreshToken) => !string.Equals(_refreshToken, sentRefreshToken, StringComparison.Ordinal);

        // True, and logged, when the sign-in a refresh was sent for has ended.
        private bool IsSignInOver(int signInNumber)
        {
            if (signInNumber == _signInNumber)
                return false;
            _logger.LogDebug("Token refresh reply ignored: the player signed out or changed while it was on its way");
            return true;
        }

        internal void ClearTokens() => EndSignIn(FlockSessionEndReason.Logout, deleteSavedSignIn: true);

        // Ends the sign-in in this client: its analytics session ends with the reason given, and every call bound to it stops.
        // A shutdown keeps the saved sign-in for the next Create; a sign-out or an expired sign-in deletes it.
        private void EndSignIn(FlockSessionEndReason reason, bool deleteSavedSignIn)
        {
            _logger.LogInfo(deleteSavedSignIn ? "Clearing authentication tokens"
                : IsLocalPlayer ? "Ending this local player's sign-in" : "Letting go of this client's tokens; the saved sign-in stays");

            if (_session != null && _session.IsActive)
            {
                _session.Reset(reason);
            }

#if !FLOCK_NO_ANALYTICS
            (_analytics as FlockAnalyticsProvider)?.HandleAuthCleared();
#endif

            _accessToken = null;
            _refreshToken = null;
            _tokenClaims = null;
            _tokensBeforeHandover = null;
            _signInNumber++;

            if (deleteSavedSignIn)
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
            // Before the ended check: the answer replaced the server's refresh token even when it came after this player ended.
            if (IsLocalPlayer)
                RefuseAPlayerAlreadySignedInHere(accessToken, refreshToken);
            RefuseIfEnded();
            // A sign-in answered after Shutdown would overwrite the saved sign-in the next Create restores, and be announced to its listeners.
            if (_shutDown)
                throw new FlockException("Flock was shut down before the server answered, so the player is not signed in")
                {
                    Hint = "Sign the player in again once Flock is initialized again (FlockClient.Create), through FlockClient.Instance."
                };
            StoreTokens(accessToken, refreshToken);
            _tokensBeforeHandover = null;
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
                    throw new FlockAuthException("The server's sign-in answer holds an access token that cannot be read, so no player session was made", ex)
                    {
                        Hint = FlockErrorHints.NotFlocksAnswer
                    };
                }
            }

            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _tokenClaims = claims;

            if (claims != null)
                _logger.LogDebug($"Token set for PlayerId: {claims.PlayerId}");

            PersistTokens();
        }

        // A local player's sign-in is kept in memory only.
        private ITokenStore TokenStore => IsLocalPlayer ? null : _initConfig.TokenStore;

        internal StoredTokens LoadPersistedTokens()
        {
            ITokenStore store = TokenStore;
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
            ITokenStore store = TokenStore;
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
            ITokenStore store = TokenStore;
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
