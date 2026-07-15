using System;
using System.Threading.Tasks;
using Flock.Config;
using UnityEngine;

namespace Flock
{
    /// <summary>Drop-in component that calls <see cref="FlockClient.Create"/> from a <see cref="FlockConfigAsset"/>. Optional — you can call <c>Create</c> yourself.</summary>
    [AddComponentMenu("Flock/Flock Bootstrap")]
    [DisallowMultipleComponent]
    public class FlockBootstrap : MonoBehaviour
    {
        [SerializeField]
        [Tooltip(
            "FlockConfig asset that holds your API URL, key, game name, and game version. " +
            "This component never stores those values itself — edit them on the asset " +
            "(open Flock > Settings or select the asset in the Project view).")]
        private FlockConfigAsset config;

        [SerializeField]
        [Tooltip(
            "When ON, FlockClient.Create runs automatically in Awake using the " +
            "asset above. Turn OFF if you want to call Initialize() yourself — " +
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
            "When ON, after init the bootstrap restores a persisted session in the background. " +
            "Subscribe to FlockEvents.OnSessionRestored (true = signed in, false = show login) " +
            "or bind to FlockClient.IsRestoringSession. Turn OFF to drive the restore yourself.")]
        private bool restoreSessionOnInit = true;

        private static FlockBootstrap _activeInstance;

        /// <summary>The asset this bootstrap is currently pointed at. Read-only.</summary>
        public FlockConfigAsset Config => config;

        /// <summary>Mirrors <see cref="FlockClient.IsInitialized"/> for inspector convenience.</summary>
        public bool IsInitialized => FlockClient.IsInitialized;

        /// <summary>Fired after <see cref="FlockClient.Create"/> succeeds.</summary>
        public event Action OnInitialized;

        /// <summary>Fired if initialization throws. The exception is passed through.</summary>
        public event Action<Exception> OnInitializationFailed;

        private void Awake()
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
                try { Initialize(); }
                catch { /* surfaced via OnInitializationFailed + Debug.LogError in Initialize */ }
            }
        }

        private void OnDestroy()
        {
            if (_activeInstance == this) _activeInstance = null;
        }

        /// <summary>Synchronously inits the SDK from the asset (no network), then restores a session in the background if enabled. No-op if already initialized; throws on missing/invalid config.</summary>
        public void Initialize()
        {
            if (FlockClient.IsInitialized)
            {
                Debug.Log("[Flock] FlockClient already initialized; FlockBootstrap skipping.");
                return;
            }

            if (config == null)
            {
                InvalidOperationException ex = new InvalidOperationException(
                    $"FlockBootstrap on '{name}' has no FlockConfig asset assigned. " +
                    "Drag a FlockConfig into the 'Config' field, or create one via Flock > Settings.");
                Debug.LogError(ex.Message, this);
                OnInitializationFailed?.Invoke(ex);
                throw ex;
            }

            if (!config.IsValid(out string validationError))
            {
                InvalidOperationException ex = new InvalidOperationException(
                    $"FlockConfig asset '{config.name}' is incomplete: {validationError}. " +
                    "Open Flock > Settings to fix it.");
                Debug.LogError(ex.Message, this);
                OnInitializationFailed?.Invoke(ex);
                throw ex;
            }

            try
            {
                FlockClient.Create(config.ToInitConfig());
                OnInitialized?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock] Bootstrap initialization failed: {ex.Message}", this);
                OnInitializationFailed?.Invoke(ex);
                throw;
            }

            if (restoreSessionOnInit)
                _ = RestoreSessionAsync();
        }

        // Result + in-flight state surface via FlockEvents.OnSessionRestored and FlockClient.IsRestoringSession.
        private async Task RestoreSessionAsync()
        {
            try
            {
                await FlockClient.Instance.Authentication.TryRestoreSessionAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Flock] Session restore failed: {ex.Message}", this);
            }
        }
    }
}
