using System;
using Flock.Providers;

namespace Flock
{
    /// <summary>
    /// One of several players signed in at the same time on this machine, for couch and booth games. Made with
    /// <see cref="FlockClient.AddLocalPlayer"/>, ended with <see cref="FlockClient.RemoveLocalPlayer"/>.
    /// </summary>
    /// <remarks>
    /// Each local player has a sign-in of their own, so one signing in never changes who another's calls act for, and the player
    /// signed in through <see cref="FlockClient.Authentication"/> is not affected either. Kept in memory only: sign them in again
    /// on the next launch. Their sign-ins are raised on this player's own events, not on <see cref="FlockEvents"/>, and analytics
    /// stays with the main player.
    /// </remarks>
    public sealed class FlockLocalPlayer
    {
        internal readonly FlockClient Client;

        internal FlockLocalPlayer(FlockClient client)
        {
            Client = client;
        }

        /// <summary>Signing in, registering, linking and signing out, for this player only.</summary>
        public FlockAuthProvider Authentication => Client.Authentication;
#if !FLOCK_NO_PLAYER
        /// <summary>This player's saved data.</summary>
        public PlayerProvider Player => Client.Player;
#endif
#if !FLOCK_NO_COMMANDS
        /// <summary>Writing to this player's saved data.</summary>
        public FlockCommandProvider Commands => Client.Commands;
#endif
#if !FLOCK_NO_LEADERBOARD
        /// <summary>Where this player stands on a board.</summary>
        public FlockLeaderboardProvider Leaderboard => Client.Leaderboard;
#endif

        /// <summary>The signed-in player's id; null before they sign in and once they are removed.</summary>
        public string PlayerId => Client.CurrentPlayerId;

        /// <summary>Whether this local player is signed in.</summary>
        public bool IsSignedIn => Client.IsAuthenticated;

        /// <summary>This player signed in.</summary>
        public event Action<FlockAuthInfo> OnAuthenticated;

        /// <summary>This player's access token was renewed.</summary>
        public event Action OnTokenRefreshed;

        /// <summary>This player's sign-in could not be renewed and has ended; sign them in again.</summary>
        public event Action OnAuthExpired;

        /// <summary>This player signed out, or was removed while signed in.</summary>
        public event Action OnLoggedOut;

        /// <summary>A credential was linked to this player.</summary>
        public event Action<FlockCredentialProvider> OnAccountLinked;

        /// <summary>A credential was unlinked from this player.</summary>
        public event Action<FlockCredentialProvider> OnAccountUnlinked;

        internal void RaiseAuthenticated(FlockAuthInfo info) => FlockEvents.InvokeEach(OnAuthenticated, info, $"{nameof(FlockLocalPlayer)}.{nameof(OnAuthenticated)}");
        internal void RaiseTokenRefreshed() => FlockEvents.InvokeEach(OnTokenRefreshed, $"{nameof(FlockLocalPlayer)}.{nameof(OnTokenRefreshed)}");
        internal void RaiseAuthExpired() => FlockEvents.InvokeEach(OnAuthExpired, $"{nameof(FlockLocalPlayer)}.{nameof(OnAuthExpired)}");
        internal void RaiseLoggedOut() => FlockEvents.InvokeEach(OnLoggedOut, $"{nameof(FlockLocalPlayer)}.{nameof(OnLoggedOut)}");
        internal void RaiseAccountLinked(FlockCredentialProvider provider) => FlockEvents.InvokeEach(OnAccountLinked, provider, $"{nameof(FlockLocalPlayer)}.{nameof(OnAccountLinked)}");
        internal void RaiseAccountUnlinked(FlockCredentialProvider provider) => FlockEvents.InvokeEach(OnAccountUnlinked, provider, $"{nameof(FlockLocalPlayer)}.{nameof(OnAccountUnlinked)}");
    }
}
