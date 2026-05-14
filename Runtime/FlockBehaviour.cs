using System;
using UnityEngine;

namespace Flock
{
    internal class FlockBehaviour : MonoBehaviour
    {
        private static FlockBehaviour _instance;
        private static bool _isQuitting;
        private static readonly object _lock = new object();

        internal static FlockBehaviour Instance
        {
            get
            {
                if (_isQuitting)
                    return null;

                if (_instance != null)
                    return _instance;

                lock (_lock)
                {
                    if (_instance != null)
                        return _instance;

                    GameObject go = new GameObject("FlockBehaviour");
                    go.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
                    _instance = go.AddComponent<FlockBehaviour>();
                    DontDestroyOnLoad(go);
                }

                return _instance;
            }
        }

        internal static bool IsAvailable => _instance != null && !_isQuitting;

        internal event Action OnTick;

        /// <summary>
        /// Fires from Unity's <c>OnApplicationPause</c> — i.e. the app moved to the
        /// background (mobile) or lost foreground status with Run In Background off
        /// (desktop). Not related to gameplay pause / <c>Time.timeScale</c>.
        /// Argument is <c>true</c> when backgrounded, <c>false</c> when foregrounded again.
        /// </summary>
        internal event Action<bool> OnAppBackgrounded;
        internal event Action<bool> OnFocus;
        internal event Action OnQuit;
        internal event Action<string,string> OnException;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            OnTick?.Invoke();
        }

        void OnEnable() {
            Application.logMessageReceived += HandleLog;
        }
        void OnDisable() {
            Application.logMessageReceived -= HandleLog;
        }
        private void HandleLog(string logMessage, string stackTrace, LogType type)
        {
            if (type == LogType.Exception)
            {
                if (string.IsNullOrEmpty(stackTrace))
                {
                    stackTrace = StackTraceUtility.ExtractStackTrace();
                }
                OnException?.Invoke(logMessage, stackTrace);
            }
        }

        private void OnApplicationPause(bool paused)
        {
            OnAppBackgrounded?.Invoke(paused);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            OnFocus?.Invoke(hasFocus);
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
            OnQuit?.Invoke();
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                OnTick = null;
                OnAppBackgrounded = null;
                OnFocus = null;
                OnQuit = null;
                OnException = null;
                _instance = null;
            }
        }
    }
}
