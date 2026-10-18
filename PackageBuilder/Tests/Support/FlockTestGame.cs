using Flock.Analytics;
using Flock.Auth;
using Flock.Config;
using UnityEngine;

namespace Flock.Tests.Support
{
    /// <summary>The game every test's SDK is made for (https://test.invalid with test-key): its game key, the PlayerPrefs keys its saved sign-in is kept under, and where its analytics launches go.</summary>
    public static class FlockTestGame
    {
        public const string ApiUrl = "https://test.invalid";
        public const string ApiKey = "test-key";

        public static readonly string Key = FlockGameKey.Of(ApiUrl, ApiKey);

        /// <summary>Where the test game's saved access token is kept in the editor.</summary>
        public static readonly string AccessTokenPref = OtherTokenStore.AccessKeyOf(Key);
        public static readonly string RefreshTokenPref = OtherTokenStore.RefreshKeyOf(Key);

        /// <summary>Where the sign-in every game shared before 1.88.1 is kept in the editor.</summary>
        public static readonly string EveryGamesAccessTokenPref = OtherTokenStore.AccessKeyOf(null);
        public static readonly string EveryGamesRefreshTokenPref = OtherTokenStore.RefreshKeyOf(null);

        /// <summary>The test game's launches folder under an analytics folder.</summary>
        public static string LaunchesFolderIn(string analyticsFolder) => FlockAnalyticsLaunches.LaunchesFolderOf(analyticsFolder, Key);

        /// <summary>Forgets the test game's saved sign-in and the one every game shared, so no test restores a sign-in it did not make.</summary>
        public static void ForgetSavedSignIns()
        {
            PlayerPrefs.DeleteKey(AccessTokenPref);
            PlayerPrefs.DeleteKey(RefreshTokenPref);
            PlayerPrefs.DeleteKey(EveryGamesAccessTokenPref);
            PlayerPrefs.DeleteKey(EveryGamesRefreshTokenPref);
        }
    }
}
