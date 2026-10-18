using System;
using System.IO;
using System.Linq;
using Flock.Auth;
using Flock.Config;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;

namespace Flock.Tests.Editor
{
    // Each game's saved sign-in kept apart (a player of one game is no player of another), the one an earlier build kept for every
    // game taken over once by the first game, and the game key both are kept under.
    public class FlockGameSignInTests
    {
        private const string GameA = "aaaaaaaaaaaaaaaa";
        private const string GameB = "bbbbbbbbbbbbbbbb";
        private string _folder;
        // What the editor kept under the keys every game shared, put back after: an open editor's own sign-in is not a test's.
        private string _editorsAccess;
        private string _editorsRefresh;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "flock_sign_in_" + Guid.NewGuid().ToString("N"));
            _editorsAccess = PlayerPrefs.HasKey(FlockTestGame.EveryGamesAccessTokenPref) ? PlayerPrefs.GetString(FlockTestGame.EveryGamesAccessTokenPref) : null;
            _editorsRefresh = PlayerPrefs.HasKey(FlockTestGame.EveryGamesRefreshTokenPref) ? PlayerPrefs.GetString(FlockTestGame.EveryGamesRefreshTokenPref) : null;
            Forget();
        }

        [TearDown]
        public void TearDown()
        {
            Forget();
            if (_editorsAccess != null)
                PlayerPrefs.SetString(FlockTestGame.EveryGamesAccessTokenPref, _editorsAccess);
            if (_editorsRefresh != null)
                PlayerPrefs.SetString(FlockTestGame.EveryGamesRefreshTokenPref, _editorsRefresh);
            PlayerPrefs.Save();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        private static void Forget()
        {
            foreach (string game in new[] { GameA, GameB, null })
                new OtherTokenStore(game).Clear();
            FlockTestGame.ForgetSavedSignIns();
        }

        private static FlockGameTokenStore Game(string gameKey) => new FlockGameTokenStore(new OtherTokenStore(gameKey), new OtherTokenStore(null));

        // A store whose saves fail without a word, as a full disk or a Keychain error can.
        private sealed class SavesNothingStore : ITokenStore
        {
            public override void Save(string accessToken, string refreshToken) { }
            public override StoredTokens Load() => null;
            public override void Clear() { }
        }

        [Test]
        public void TwoGames_KeepTheirSignInsApart()
        {
            Game(GameA).Save("a-access", "a-refresh");
            Assert.IsNull(Game(GameB).Load(), "Another game restores nobody");
            Assert.AreEqual("a-access", Game(GameA).Load()?.AccessToken, "Its own game restores its player");
        }

        [Test]
        public void TheSignInAnEarlierBuildKeptForEveryGame_IsTakenOverByTheFirstGame_Once()
        {
            new OtherTokenStore(null).Save("earlier-access", "earlier-refresh");

            StoredTokens taken = Game(GameA).Load();
            Assert.AreEqual("earlier-access", taken?.AccessToken, "Nobody is signed out by updating");
            Assert.AreEqual("earlier-refresh", taken?.RefreshToken);
            Assert.IsNull(new OtherTokenStore(null).Load(), "No longer kept for every game");
            Assert.IsNull(Game(GameB).Load(), "A second game takes nothing over");
            Assert.AreEqual("earlier-access", Game(GameA).Load()?.AccessToken, "The first game keeps it as its own");
        }

        [Test]
        public void TheEarlierBuildsSignIn_IsKept_WhenTheGamesOwnSlotCouldNotSaveIt()
        {
            new OtherTokenStore(null).Save("earlier-access", "earlier-refresh");
            FlockGameTokenStore failing = new FlockGameTokenStore(new SavesNothingStore(), new OtherTokenStore(null));
            Assert.AreEqual("earlier-access", failing.Load()?.AccessToken, "This launch is still signed in");
            Assert.AreEqual("earlier-access", new OtherTokenStore(null).Load()?.AccessToken, "And the earlier sign-in waits for the next launch, not lost");
        }

        [Test]
        public void TheConfig_KeepsTheSignInWithTheGameItsAddressNamesWhenTheStoreIsNeeded()
        {
            FlockInitConfig config = new FlockInitConfig("https://dev.invalid", "key-a", "a game", "1.0.0");
            config.ApiUrl = "https://prod.invalid";
            FlockInitConfig prod = new FlockInitConfig("https://prod.invalid", "key-a", "a game", "1.0.0");
            FlockInitConfig dev = new FlockInitConfig("https://dev.invalid", "key-a", "a game", "1.0.0");
            try
            {
                config.TokenStore.Save("prod-access", "prod-refresh");
                Assert.AreEqual("prod-access", prod.TokenStore.Load()?.AccessToken, "Kept for the game the address names now, as its analytics are");
                Assert.IsNull(dev.TokenStore.Load(), "Not for the address the config was made with");
            }
            finally
            {
                prod.TokenStore.Clear();
                dev.TokenStore.Clear();
            }
        }

        [Test]
        public void ANewSignIn_ReplacesTheOneAnEarlierBuildKept()
        {
            new OtherTokenStore(null).Save("earlier-access", "earlier-refresh");
            Game(GameA).Save("a-access", "a-refresh");
            Assert.IsNull(new OtherTokenStore(null).Load(), "No later game takes the earlier sign-in over");
            Assert.IsNull(Game(GameB).Load());
        }

        [Test]
        public void SigningOut_ForgetsTheOneAnEarlierBuildKeptToo()
        {
            new OtherTokenStore(null).Save("earlier-access", "earlier-refresh");
            new OtherTokenStore(GameA).Save("a-access", "a-refresh");
            Game(GameA).Clear();
            Assert.IsNull(Game(GameA).Load(), "Signed out stays signed out at the next launch");
        }

        [Test]
        public void TheConfig_KeepsEachGamesSignInApart_ByTheBackendAndTheApiKey()
        {
            FlockInitConfig a = new FlockInitConfig("https://test.invalid", "key-a", "same name", "1.0.0");
            FlockInitConfig sameGame = new FlockInitConfig(" HTTPS://Test.Invalid// ", " key-a ", "another name", "2.0.0");
            FlockInitConfig otherKey = new FlockInitConfig("https://test.invalid", "key-b", "same name", "1.0.0");
            FlockInitConfig otherBackend = new FlockInitConfig("https://other.invalid", "key-a", "same name", "1.0.0");
            try
            {
                a.TokenStore.Save("a-access", "a-refresh");
                Assert.AreEqual("a-access", sameGame.TokenStore.Load()?.AccessToken, "The same backend and key are the same game, whatever its name");
                Assert.IsNull(otherKey.TokenStore.Load(), "Another API key is another game");
                Assert.IsNull(otherBackend.TokenStore.Load(), "Another backend is another game");
            }
            finally
            {
                a.TokenStore.Clear();
                otherKey.TokenStore.Clear();
                otherBackend.TokenStore.Clear();
            }
        }

        [Test]
        public void TheGameKey_IsSixteenHexCharacters_AndReadsTheAddressAsTheServerDoes()
        {
            string key = FlockGameKey.Of("https://api.example.com", "the-key");
            Assert.AreEqual(16, key.Length);
            Assert.IsTrue(key.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), key);
            Assert.AreEqual(key, FlockGameKey.Of(" HTTPS://API.Example.com/ ", " the-key "), "Spaces, a trailing slash and letter case in the address change nothing");
            Assert.AreNotEqual(key, FlockGameKey.Of("https://api.example.com", "The-key"), "An API key's letter case is its own");
            Assert.AreNotEqual(key, FlockGameKey.Of("https://dev.example.com", "the-key"));
            StringAssert.DoesNotContain("the-key", key, "The API key is never written as it is");
        }

        [Test]
        public void TheTokenFile_OfAGame_IsNamedForIt_AndReadsNoEarlierTwoFiles()
        {
            Func<byte[], byte[]> seal = plain => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
            FlockTokenFile every = new FlockTokenFile(_folder, null, seal, seal);
            FlockTokenFile game = new FlockTokenFile(_folder, GameA, seal, seal);
            game.Save("a-access", "a-refresh");
            Assert.AreEqual(Path.Combine(_folder, FlockTokenFile.FileName + "-" + GameA), game.FilePath);
            Assert.IsTrue(File.Exists(game.FilePath));
            Assert.IsNull(every.Load(), "The file every game shares is another file");

            // The two files of versions before the one file are read only as the file every game shares.
            File.WriteAllBytes(Path.Combine(_folder, "AccessData"), seal(System.Text.Encoding.UTF8.GetBytes("old-access")));
            File.WriteAllBytes(Path.Combine(_folder, "RefreshData"), seal(System.Text.Encoding.UTF8.GetBytes("old-refresh")));
            FlockTokenFile gameB = new FlockTokenFile(_folder, GameB, seal, seal);
            Assert.IsNull(gameB.Load(), "A game's own file is never read from the earlier two files");
            Assert.AreEqual("old-access", every.Load()?.AccessToken);
        }
    }
}
