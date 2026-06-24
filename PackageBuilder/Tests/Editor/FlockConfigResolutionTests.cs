using System;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Http;
using NUnit.Framework;

using UnityEngine;

namespace Flock.Tests
{
    // Locks the config value resolution: GetByConfigIdAsync<T> returns the patch's data when a patch
    // exists for the game version, and falls back to the config's own data when there is no patch
    // (instead of empty defaults). Hermetic — a routing fake adapter answers both fetches.
    public class FlockConfigResolutionTests
    {
        private const string ConfigId = "cfg1";

        private class TestConfig
        {
            public int Coins { get; set; }
        }

        // Routes by URL: a /game_patch/* request gets the patches body, anything else the config body.
        private sealed class RoutingAdapter : IFlockHttpAdapter
        {
            private readonly string _patchesBody;
            private readonly string _configBody;
            public RoutingAdapter(string patchesBody, string configBody) { _patchesBody = patchesBody; _configBody = configBody; }
            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                string body = request.Url.Contains("/game_patch/") ? _patchesBody : _configBody;
                return Task.FromResult(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = body });
            }
        }

        private static string OnePatch(int coins) =>
            "{\"result\":[{\"id\":\"p1\",\"game_config_id\":\"" + ConfigId + "\",\"data\":[{\"field_name\":\"coins\",\"type\":\"int\",\"value\":" + coins + "}]}]}";
        private const string NoPatches = "{\"result\":[]}";
        private static string ConfigBody(int coins) =>
            "{\"result\":{\"id\":\"" + ConfigId + "\",\"name\":\"test\",\"data\":[{\"field_name\":\"coins\",\"type\":\"int\",\"value\":" + coins + "}]}}";

        [SetUp]
        public void SetUp()
        {
            FlockConfigAsset asset = ScriptableObject.CreateInstance<FlockConfigAsset>();
            asset.apiUrl = "https://test.invalid";
            asset.apiKey = "test-key";
            asset.gameId = "test-game";
            asset.gameVersion = "1.0.0";
            asset.gameVersionId = "test-gvid";
            asset.analyticsEnabled = false;
            asset.enableOfflineCache = false;
            asset.autoInitializeOnLoad = false;
            FlockClient.Create(asset.ToInitConfig());
        }

        [TearDown]
        public void TearDown()
        {
            FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
        }

        // Runs off Unity's sync-context so blocking for the result can't deadlock.
        private static T Run<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

        [Test]
        public void PatchPresent_ReturnsPatchData()
        {
            FlockHttpClient.Configure(new RoutingAdapter(OnePatch(200), ConfigBody(100)));
            TestConfig result = Run(() => FlockClient.Instance.Config.GetByConfigIdAsync<TestConfig>(ConfigId, CancellationToken.None));
            Assert.AreEqual(200, result.Coins);
        }

        [Test]
        public void NoPatch_FallsBackToConfigData()
        {
            FlockHttpClient.Configure(new RoutingAdapter(NoPatches, ConfigBody(100)));
            TestConfig result = Run(() => FlockClient.Instance.Config.GetByConfigIdAsync<TestConfig>(ConfigId, CancellationToken.None));
            Assert.AreEqual(100, result.Coins);
        }
    }
}
