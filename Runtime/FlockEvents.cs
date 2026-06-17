using System;
using Flock.Logging;
using UnityEngine;

namespace Flock
{
    /// <summary>
    /// Static hub for SDK lifecycle events. Subscribe anytime, even before
    /// <see cref="FlockClient.Create"/>; raised on the Unity main thread.
    /// <see cref="OnInitialized"/> and <see cref="OnInitializationFailed"/> are replayed to
    /// handlers that subscribe after init already happened, so they fire under auto-init too.
    /// All subscriptions are cleared on <see cref="FlockClient.Shutdown"/> — subscribe in
    /// OnEnable, unsubscribe in OnDisable. A throwing subscriber is logged and never
    /// breaks the SDK or other subscribers.
    /// </summary>
    public static class FlockEvents
    {
        // Set by FlockClient; debug-logs every raise. Null before init.
        internal static IFlockLogger Logger;

        //Lifecycle

        private static Action _onInitialized;
        /// <summary>SDK initialized — <see cref="FlockClient.Instance"/> is usable. Replayed immediately to handlers that subscribe after init (so it works under auto-init).</summary>
        public static event Action OnInitialized
        {
            add
            {
                _onInitialized += value;
                if (FlockClient.IsInitialized) InvokeOne(value, nameof(OnInitialized));
            }
            remove { _onInitialized -= value; }
        }

        private static Action<Exception> _onInitializationFailed;
        /// <summary><see cref="FlockClient.Create"/> failed (still thrown to direct callers; the auto-init path logs instead). Replayed to late subscribers from <see cref="FlockClient.InitializationError"/>. The "already initialized" misuse guard does not raise it.</summary>
        public static event Action<Exception> OnInitializationFailed
        {
            add
            {
                _onInitializationFailed += value;
                Exception error = FlockClient.InitializationError;
                if (error != null) InvokeOne(value, error, nameof(OnInitializationFailed));
            }
            remove { _onInitializationFailed -= value; }
        }

        /// <summary><see cref="FlockClient.Shutdown"/> completed. Last event raised; all subscriptions are cleared right after.</summary>
        public static event Action OnShutdown;

        //Auth

        /// <summary>A player signed in (login, register, or restored session) — see <see cref="FlockAuthInfo.Method"/>.</summary>
        public static event Action<FlockAuthInfo> OnAuthenticated;

        /// <summary>The access token was refreshed successfully.</summary>
        public static event Action OnTokenRefreshed;

        /// <summary>Token refresh failed — the player must log in again.</summary>
        public static event Action OnAuthExpired;

        /// <summary><c>Logout()</c> completed (local-only: nothing revoked server-side).</summary>
        public static event Action OnLoggedOut;

        /// <summary>Session restore finished; payload is whether a session was restored (also fires false when there was none).</summary>
        public static event Action<bool> OnSessionRestored;

        //Session

        /// <summary>A gameplay/analytics session began; payload is the local session id.</summary>
        public static event Action<string> OnSessionStarted;

        /// <summary>A session ended (any path); payload carries the final snapshot and the reason.</summary>
        public static event Action<FlockSessionEndedArgs> OnSessionEnded;

        /// <summary>The active session was paused (app backgrounded).</summary>
        public static event Action OnSessionPaused;

        /// <summary>The paused session resumed (app foregrounded).</summary>
        public static event Action OnSessionResumed;

        //Internal raises

        internal static void RaiseInitialized()
        {
            Raise(_onInitialized, nameof(OnInitialized));
        }

        internal static void RaiseInitializationFailed(Exception exception)
        {
            Raise(_onInitializationFailed, exception, nameof(OnInitializationFailed));
        }

        internal static void RaiseShutdown()
        {
            Raise(OnShutdown, nameof(OnShutdown));
        }

        internal static void RaiseAuthenticated(FlockAuthInfo info)
        {
            Raise(OnAuthenticated, info, nameof(OnAuthenticated));
        }

        internal static void RaiseTokenRefreshed()
        {
            Raise(OnTokenRefreshed, nameof(OnTokenRefreshed));
        }

        internal static void RaiseAuthExpired()
        {
            Raise(OnAuthExpired, nameof(OnAuthExpired));
        }

        internal static void RaiseLoggedOut()
        {
            Raise(OnLoggedOut, nameof(OnLoggedOut));
        }

        internal static void RaiseSessionRestored(bool restored)
        {
            Raise(OnSessionRestored, restored, nameof(OnSessionRestored));
        }

        internal static void RaiseSessionStarted(string sessionId)
        {
            Raise(OnSessionStarted, sessionId, nameof(OnSessionStarted));
        }

        internal static void RaiseSessionEnded(FlockSessionEndedArgs args)
        {
            Raise(OnSessionEnded, args, nameof(OnSessionEnded));
        }

        internal static void RaiseSessionPaused()
        {
            Raise(OnSessionPaused, nameof(OnSessionPaused));
        }

        internal static void RaiseSessionResumed()
        {
            Raise(OnSessionResumed, nameof(OnSessionResumed));
        }

        //Cleanup

        /// <summary>Drops every subscription. Called by Shutdown and on play-session start with domain reload disabled.</summary>
        internal static void ClearAll()
        {
            _onInitialized = null;
            _onInitializationFailed = null;
            OnShutdown = null;
            OnAuthenticated = null;
            OnTokenRefreshed = null;
            OnAuthExpired = null;
            OnLoggedOut = null;
            OnSessionRestored = null;
            OnSessionStarted = null;
            OnSessionEnded = null;
            OnSessionPaused = null;
            OnSessionResumed = null;
        }

        // Mirrors FlockClient.ResetStaticState for disabled domain reload.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            ClearAll();
            Logger = null;
        }

        //Raise plumbing

        private static void InvokeOne(Action handler, string eventName)
        {
            try { handler(); }
            catch (Exception ex) { LogSubscriberException(eventName, ex); }
        }

        private static void InvokeOne<T>(Action<T> handler, T payload, string eventName)
        {
            try { handler(payload); }
            catch (Exception ex) { LogSubscriberException(eventName, ex); }
        }

        private static void Raise(Action handlers, string eventName)
        {
            if (handlers == null)
            {
                Logger?.LogDebug($"{eventName} fired -> 0 subscribers");
                return;
            }

            Delegate[] invocationList = handlers.GetInvocationList();
            Logger?.LogDebug($"{eventName} fired -> {invocationList.Length} subscriber(s)");
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action)invocationList[i])();
                }
                catch (Exception ex)
                {
                    LogSubscriberException(eventName, ex);
                }
            }
        }

        private static void Raise<T>(Action<T> handlers, T payload, string eventName)
        {
            if (handlers == null)
            {
                Logger?.LogDebug($"{eventName} fired -> 0 subscribers");
                return;
            }

            Delegate[] invocationList = handlers.GetInvocationList();
            Logger?.LogDebug($"{eventName} fired -> {invocationList.Length} subscriber(s)");
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T>)invocationList[i])(payload);
                }
                catch (Exception ex)
                {
                    LogSubscriberException(eventName, ex);
                }
            }
        }

        // Always Debug, not IFlockLogger — subscriber bugs must surface even with SDK logging off.
        private static void LogSubscriberException(string eventName, Exception exception)
        {
            Debug.LogError($"[Flock] FlockEvents.{eventName} subscriber threw: {exception}");
        }
    }
}
