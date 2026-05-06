using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using UnityEngine;

namespace Flock
{
    /// <summary>
    /// Drop-in scene component that calls <see cref="FlockClient.CreateAsync"/> for you.
    /// References a <see cref="FlockConfigAsset"/> for its values — the asset stays the
    /// single source of truth for API URL, key, game ID, and version. Use this when you
    /// don't want to write your own bootstrap code; otherwise call
    /// <c>FlockClient.CreateAsync</c> directly.
    /// </summary>
    [AddComponentMenu("Flock/Flock Bootstrap")]
    [DisallowMultipleComponent]
    public class FlockBootstrap : MonoBehaviour
    {
        [SerializeField]
        [Tooltip(
            "FlockConfig asset that holds your API URL, key, game ID, and game version. " +
            "This component never stores those values itself — edit them on the asset " +
            "(open Qwacks > Editor or select the asset in the Project view).")]
        private FlockConfigAsset config;

        [SerializeField]
        [Tooltip(
            "When ON, FlockClient.CreateAsync runs automatically in Awake using the " +
            "asset above. Turn OFF if you want to call InitializeAsync() yourself — " +
            "for example, after a splash screen, after the player accepts a EULA, or " +
            "once you've fetched a remote-config override.")]
        private bool initializeOnAwake = true;

        [SerializeField]
        [Tooltip(
            "When ON and this GameObject is at the scene root, it survives scene loads " +
            "via DontDestroyOnLoad. Recommended pattern: place this in a 'Boot' scene " +
            "loaded once at startup, then load your real game scenes additively. " +
            "Has no effect on child GameObjects (Unity restriction).")]
        private bool dontDestroyOnLoad = true;

        [SerializeField]
        [Tooltip(
            "When ON, after CreateAsync succeeds the bootstrap calls " +
            "Authentication.TryRestoreSessionAsync() to resume a session persisted " +
            "from a previous launch. Listen to OnSessionRestored to branch between " +
            "'go to game' (true) and 'show login UI' (false). Turn OFF if you want " +
            "to drive the restore yourself.")]
        private bool restoreSessionOnInit = true;

        private static FlockBootstrap _activeInstance;

        /// <summary>The asset this bootstrap is currently pointed at. Read-only.</summary>
        public FlockConfigAsset Config => config;

        /// <summary>True while <see cref="InitializeAsync"/> is in flight.</summary>
        public bool IsInitializing { get; private set; }

        /// <summary>Mirrors <see cref="FlockClient.IsInitialized"/> for inspector convenience.</summary>
        public bool IsInitialized => FlockClient.IsInitialized;

        /// <summary>Fired after <see cref="FlockClient.CreateAsync"/> resolves successfully.</summary>
        public event Action OnInitialized;

        /// <summary>Fired if initialization throws. The exception is passed through.</summary>
        public event Action<Exception> OnInitializationFailed;

        /// <summary>
        /// Fired after <see cref="FlockClient.Authentication"/>'s
        /// <c>TryRestoreSessionAsync</c> completes when <c>restoreSessionOnInit</c>
        /// is enabled. <c>true</c> = signed in (proceed to game), <c>false</c> =
        /// no usable persisted session (show login UI).
        /// </summary>
        public event Action<bool> OnSessionRestored;

        private async void Awake()
        {
            if (_activeInstance != null && _activeInstance != this)
            {
                Debug.LogWarning(
                    $"[Flock] Multiple FlockBootstrap components found ('{_activeInstance.name}' " +
                    $"and '{name}'). Destroying the duplicate on '{name}'.", this);
                Destroy(gameObject);
                return;
            }
            _activeInstance = this;

            if (dontDestroyOnLoad && transform.parent == null)
                DontDestroyOnLoad(gameObject);

            if (initializeOnAwake)
            {
                try { await InitializeAsync(); }
                catch { /* surfaced via OnInitializationFailed + Debug.LogError below */ }
            }
        }

        private void OnDestroy()
        {
            if (_activeInstance == this) _activeInstance = null;
        }

        /// <summary>
        /// Initializes the SDK using the referenced asset. Safe to call multiple times —
        /// returns immediately if the SDK is already initialized. Throws if the asset is
        /// missing or invalid (also fires <see cref="OnInitializationFailed"/>).
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (FlockClient.IsInitialized)
            {
                Debug.Log("[Flock] FlockClient already initialized; FlockBootstrap skipping.");
                return;
            }

            if (config == null)
            {
                var ex = new InvalidOperationException(
                    $"FlockBootstrap on '{name}' has no FlockConfig asset assigned. " +
                    "Drag a FlockConfig into the 'Config' field, or create one via Qwacks > Editor.");
                Debug.LogError(ex.Message, this);
                OnInitializationFailed?.Invoke(ex);
                throw ex;
            }

            if (!config.IsValid(out string validationError))
            {
                var ex = new InvalidOperationException(
                    $"FlockConfig asset '{config.name}' is incomplete: {validationError}. " +
                    "Open Qwacks > Editor to fix it.");
                Debug.LogError(ex.Message, this);
                OnInitializationFailed?.Invoke(ex);
                throw ex;
            }

            IsInitializing = true;
            try
            {
                await FlockClient.CreateAsync(config.ToInitConfig(), cancellationToken: cancellationToken);
                OnInitialized?.Invoke();

                if (restoreSessionOnInit)
                {
                    bool resumed = await FlockClient.Instance.Authentication.TryRestoreSessionAsync(cancellationToken);
                    OnSessionRestored?.Invoke(resumed);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock] Bootstrap initialization failed: {ex.Message}", this);
                OnInitializationFailed?.Invoke(ex);
                throw;
            }
            finally
            {
                IsInitializing = false;
            }
        }
    }
}
