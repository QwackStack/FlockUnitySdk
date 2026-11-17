using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flock.Auth;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    /// <summary>Two local players signed in at once against a local backend, judged on its tables afterwards. Run by hand:
    /// FLOCK_LOCAL_PLAYERS_LIVE names the file Qwacks/Libraries/Unity/local-players/run_local_players_live.py writes.</summary>
    public class FlockLocalPlayersLiveTests
    {
        // The editor's own saved sign-in for this game, put back afterwards so the run does not sign the developer's Play Mode player out.
        private ITokenStore _store;
        private StoredTokens _developersSignIn;

        [TearDown]
        public void TearDown()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (_store == null)
                return;
            if (_developersSignIn == null)
                _store.Clear();
            else
                _store.Save(_developersSignIn.AccessToken, _developersSignIn.RefreshToken);
        }

        [UnityTest, Explicit("Hits the local Flock backend; run by hand through run_local_players_live.py.")]
        public IEnumerator Live_TwoLocalPlayersAtOnce_EachWritesTheirOwnRow()
        {
            string livePath = Environment.GetEnvironmentVariable("FLOCK_LOCAL_PLAYERS_LIVE");
            if (string.IsNullOrEmpty(livePath) || !File.Exists(livePath))
                Assert.Ignore("FLOCK_LOCAL_PLAYERS_LIVE names no file.");
            JObject live = JObject.Parse(File.ReadAllText(livePath));
            string run = (string)live["run"];
            string templateId = (string)live["template_id"];
            FlockConfigAsset asset = Resources.Load<FlockConfigAsset>("FlockConfig");
            FlockInitConfig config = asset.ToInitConfig();
            StringAssert.StartsWith("http://127.0.0.1:8001", config.ApiUrl, "The local stack only, never a shared backend.");
            config.EnableOfflineCache = false;
            _store = config.TokenStore;
            _developersSignIn = _store.Load();
            FlockClient.Create(config, new RecordingFlockLogger());
            FlockClient main = FlockClient.Instance;
            main.ReachabilityProbe = () => true;
            JObject results = new JObject { ["run"] = run };

            Task signedIn = main.Authentication.RegisterWithDeviceAsync($"local-players-{run}-main");
            yield return Await(signedIn);
            results["main_player"] = main.CurrentPlayerId;

            FlockLocalPlayer sara = main.AddLocalPlayer();
            FlockLocalPlayer omar = main.AddLocalPlayer();
            sara.Client.ReachabilityProbe = () => true;
            omar.Client.ReachabilityProbe = () => true;

            // Both sign in at once, then read and write at once: no call of one may act for the other.
            Task both = Task.WhenAll(sara.Authentication.RegisterWithDeviceAsync($"local-players-{run}-sara"),
                omar.Authentication.RegisterWithDeviceAsync($"local-players-{run}-omar"));
            yield return Await(both);
            Assert.AreNotEqual(sara.PlayerId, omar.PlayerId);
            results["sara_player"] = sara.PlayerId;
            results["omar_player"] = omar.PlayerId;

            Task<PlayerData> saraRow = sara.Player.GetMyDataByTemplateAsync(templateId);
            Task<PlayerData> omarRow = omar.Player.GetMyDataByTemplateAsync(templateId);
            yield return Await(Task.WhenAll(saraRow, omarRow));
            Assert.AreEqual(sara.PlayerId, saraRow.Result.PlayerId);
            Assert.AreEqual(omar.PlayerId, omarRow.Result.PlayerId);
            Task writes = Task.WhenAll(sara.Commands.UpdatePlayerDataFieldAsync(saraRow.Result.Id, "Score", 1200),
                omar.Commands.UpdatePlayerDataFieldAsync(omarRow.Result.Id, "Score", 800));
            yield return Await(writes);
            results["sara_row"] = saraRow.Result.Id;
            results["omar_row"] = omarRow.Result.Id;

            // A local sign-in as the main player is refused, and the main sign-in carries on with the new tokens.
            string replacedRefreshToken = config.TokenStore.Load().RefreshToken;
            FlockLocalPlayer twin = main.AddLocalPlayer();
            Task twinSignIn = twin.Authentication.LoginWithDeviceAsync($"local-players-{run}-main");
            yield return FlockTestWait.UntilDone(twinSignIn, 30f);
            Assert.AreEqual(FlockErrorCode.FlockPlayerAlreadySignedIn, (twinSignIn.Exception?.GetBaseException() as FlockException)?.ErrorCode);
            Assert.IsFalse(twin.IsSignedIn);
            // Control: the server replaced the main sign-in's refresh token, so without the handover its renewal would be refused.
            Task<PlayerLoginResponse> withTheOldToken = FlockHttpClient.PostAsync<PlayerLoginResponse>(
                $"{main.GetVersionedApiUrl()}/{FlockEndpoints.PlayerTokenRefresh}",
                new PlayerRefreshTokenRequest { PlayerId = main.CurrentPlayerId, RefreshToken = replacedRefreshToken }, main.GetGameHeaders());
            yield return FlockTestWait.UntilDone(withTheOldToken, 30f);
            Assert.AreEqual(FlockErrorCode.PlayerInvalidRefreshToken, (withTheOldToken.Exception?.GetBaseException() as FlockException)?.ErrorCode);
            results["old_token_refused_with"] = (withTheOldToken.Exception?.GetBaseException() as FlockException)?.Code;
            Task<bool> renewed = main.RefreshTokenAsync();
            yield return Await(renewed);
            Assert.IsTrue(renewed.Result, "The main sign-in renews with the tokens handed over.");
            results["main_renewed"] = renewed.Result;
            // Judged inside the backend's container, then removed from the file by the runner; never printed.
            results["main_refresh_token"] = config.TokenStore.Load().RefreshToken;

            // Removing one leaves the other working.
            main.RemoveLocalPlayer(sara);
            Task<PlayerData> afterRemoval = sara.Player.GetMyDataByTemplateAsync(templateId);
            yield return FlockTestWait.UntilDone(afterRemoval, 30f);
            StringAssert.Contains("AddLocalPlayer", (afterRemoval.Exception?.GetBaseException() as FlockException)?.Hint);
            omar.Player.ClearCache();
            Task<PlayerData> omarAgain = omar.Player.GetMyDataByTemplateAsync(templateId);
            yield return Await(omarAgain);
            Assert.AreEqual(800, Convert.ToInt32(omarAgain.Result.Data.First(f => f.FieldName == "Score").Value));

            File.WriteAllText((string)live["results"], results.ToString());
        }

        private static IEnumerator Await(Task task)
        {
            yield return FlockTestWait.UntilDone(task, 30f);
            if (task.IsFaulted)
                throw task.Exception.GetBaseException();
        }
    }
}
