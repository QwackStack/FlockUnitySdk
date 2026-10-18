namespace Flock.Auth
{
    /// <summary>
    /// One game's saved sign-in, kept apart from every other game's, so a sign-in made with one game is never restored by another
    /// (a player of one game is no player of the next). Builds before 1.88.1 kept one sign-in for every game: the first game to read
    /// one takes that over, so nobody is signed out by updating, and it is never taken twice; a new sign-in or a sign-out discards it.
    /// </summary>
    internal sealed class FlockGameTokenStore : ITokenStore
    {
        private readonly ITokenStore _game;
        private readonly ITokenStore _everyGame;

        /// <param name="game">The platform's store for this game's own sign-in.</param>
        /// <param name="everyGame">The platform's store for the sign-in every game shared before 1.88.1.</param>
        internal FlockGameTokenStore(ITokenStore game, ITokenStore everyGame)
        {
            _game = game;
            _everyGame = everyGame;
        }

        /// <summary>The platform's store this game's sign-in is kept in, as the log names it.</summary>
        internal string PlatformStoreName => _game.GetType().Name;

        public override void Save(string accessToken, string refreshToken)
        {
            _game.Save(accessToken, refreshToken);
            // A new sign-in replaces whatever an earlier build kept, so no later game takes that over.
            _everyGame.Clear();
        }

        public override StoredTokens Load()
        {
            StoredTokens own = _game.Load();
            if (own != null)
                return own;
            StoredTokens earlier = _everyGame.Load();
            if (earlier == null)
                return null;
            _game.Save(earlier.AccessToken, earlier.RefreshToken);
            // Cleared only once the game's own slot holds it: a save that failed without a word (a full disk, a Keychain error)
            // leaves the earlier sign-in for the next launch rather than losing it.
            StoredTokens kept = _game.Load();
            if (kept != null && kept.AccessToken == earlier.AccessToken && kept.RefreshToken == earlier.RefreshToken)
                _everyGame.Clear();
            return earlier;
        }

        public override void Clear()
        {
            _game.Clear();
            _everyGame.Clear();
        }
    }
}
