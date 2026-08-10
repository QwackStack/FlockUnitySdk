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

        /// <summary>A credential was attached to the signed-in player; payload is the provider that was linked.</summary>
        public static event Action<FlockCredentialProvider> OnAccountLinked;

        /// <summary>A credential was removed from the signed-in player; payload is the provider that was unlinked.</summary>
        public static event Action<FlockCredentialProvider> OnAccountUnlinked;

        //Session

        /// <summary>A gameplay/analytics session began; payload is the local session id.</summary>
        public static event Action<string> OnSessionStarted;

        /// <summary>A session ended (any path); payload carries the final snapshot and the reason.</summary>
        public static event Action<FlockSessionEndedArgs> OnSessionEnded;

        /// <summary>The active session was paused (app backgrounded).</summary>
        public static event Action OnSessionPaused;

        /// <summary>The paused session resumed (app foregrounded).</summary>
        public static event Action OnSessionResumed;

        //Notifications

        /// <summary>The player's unread notification count changed. Raised only when the server reports a count — a fetch of unread count or summary, or a mark-all-read — never from a background poll, because the SDK doesn't run one.</summary>
        public static event Action<int> OnUnreadCountChanged;

        /// <summary>A notification the SDK hasn't surfaced before came back from an inbox or summary fetch — schedule, trigger and campaign deliveries alike.</summary>
        /// <remarks>"Received" means first seen by a read, not the instant the server created it: there is no realtime channel and the SDK never polls. The first fetch for a player is silent so an existing inbox doesn't arrive as a burst; after that, each new notification raises once, oldest first. Inspect <c>CampaignId</c> (campaign) or <c>trigger_id</c> in <c>Data</c> (trigger) to tell the source apart.</remarks>
        public static event Action<Models.Notification> OnNotificationReceived;

        //Consent

        /// <summary>Analytics consent was granted or revoked via <c>Analytics.SetConsent</c>. Payload: the new state.</summary>
        public static event Action<bool> OnConsentChanged;

        //Internal invokes

        internal static void InvokeInitialized()
        {
            Invoke(_onInitialized, nameof(OnInitialized));
        }

        internal static void InvokeInitializationFailed(Exception exception)
        {
            Invoke(_onInitializationFailed, exception, nameof(OnInitializationFailed));
        }

        internal static void InvokeShutdown()
        {
            Invoke(OnShutdown, nameof(OnShutdown));
        }

        internal static void InvokeAuthenticated(FlockAuthInfo info)
        {
            Invoke(OnAuthenticated, info, nameof(OnAuthenticated));
        }

        internal static void InvokeTokenRefreshed()
        {
            Invoke(OnTokenRefreshed, nameof(OnTokenRefreshed));
        }

        internal static void InvokeAuthExpired()
        {
            Invoke(OnAuthExpired, nameof(OnAuthExpired));
        }

        internal static void InvokeLoggedOut()
        {
            Invoke(OnLoggedOut, nameof(OnLoggedOut));
        }

        internal static void InvokeSessionRestored(bool restored)
        {
            Invoke(OnSessionRestored, restored, nameof(OnSessionRestored));
        }

        internal static void InvokeAccountLinked(FlockCredentialProvider provider)
        {
            Invoke(OnAccountLinked, provider, nameof(OnAccountLinked));
        }

        internal static void InvokeAccountUnlinked(FlockCredentialProvider provider)
        {
            Invoke(OnAccountUnlinked, provider, nameof(OnAccountUnlinked));
        }

        internal static void InvokeSessionStarted(string sessionId)
        {
            Invoke(OnSessionStarted, sessionId, nameof(OnSessionStarted));
        }

        internal static void InvokeSessionEnded(FlockSessionEndedArgs args)
        {
            Invoke(OnSessionEnded, args, nameof(OnSessionEnded));
        }

        internal static void InvokeSessionPaused()
        {
            Invoke(OnSessionPaused, nameof(OnSessionPaused));
        }

        internal static void InvokeSessionResumed()
        {
            Invoke(OnSessionResumed, nameof(OnSessionResumed));
        }

        internal static void InvokeUnreadCountChanged(int count)
        {
            Invoke(OnUnreadCountChanged, count, nameof(OnUnreadCountChanged));
        }

        internal static void InvokeNotificationReceived(Models.Notification notification)
        {
            Invoke(OnNotificationReceived, notification, nameof(OnNotificationReceived));
        }

        internal static void InvokeConsentChanged(bool granted)
        {
            Invoke(OnConsentChanged, granted, nameof(OnConsentChanged));
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
            OnAccountLinked = null;
            OnAccountUnlinked = null;
            OnSessionStarted = null;
            OnSessionEnded = null;
            OnSessionPaused = null;
            OnSessionResumed = null;
            OnUnreadCountChanged = null;
            OnConsentChanged = null;
        }

        // Mirrors FlockClient.ResetStaticState for disabled domain reload.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            ClearAll();
            Logger = null;
        }

        //Invoke plumbing

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

        private static void Invoke(Action handlers, string eventName)
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

        private static void Invoke<T>(Action<T> handlers, T payload, string eventName)
        {
            if (handlers == null)
            {
                //For now it is a lot of spam to see non-listened to invokes
                // Logger?.LogDebug($"{eventName} fired -> 0 subscribers");
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
