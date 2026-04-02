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

                    var go = new GameObject("FlockBehaviour");
                    go.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
                    _instance = go.AddComponent<FlockBehaviour>();
                    DontDestroyOnLoad(go);
                }

                return _instance;
            }
        }

        internal static bool IsAvailable => _instance != null && !_isQuitting;

        internal event Action OnTick;
        internal event Action<bool> OnPause;
        internal event Action<bool> OnFocus;
        internal event Action OnQuit;

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

        private void OnApplicationPause(bool paused)
        {
            OnPause?.Invoke(paused);
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
                OnPause = null;
                OnFocus = null;
                OnQuit = null;
                _instance = null;
            }
        }
    }
}
