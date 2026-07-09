using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests
{
    // Locks the coded-error pipeline. Hermetic tests push canned responses through FlockHttpClient via a
    // fake transport to trigger every FlockException type and verify FlockErrorCode parsing; the Explicit
    // live test uses FlockConfig.asset to hit the real backend and capture an actual coded server error.
    public class FlockErrorPipelineTests
    {
        private const string Url = "https://test.invalid/v1/x";
        private bool _createdClient;

        // Returns one preset response so the status -> exception + code-parse path runs without a network.
        private sealed class FakeAdapter : IFlockHttpAdapter
        {
            private readonly FlockHttpResponse _response;
            public FakeAdapter(FlockHttpResponse response) { _response = response; }
            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
                => Task.FromResult(_response);
        }

        private static FlockHttpResponse Coded(int status, string code)
            => new FlockHttpResponse
            {
                Result = FlockHttpResult.Success,
                StatusCode = status,
                Body = "{\"detail\":{\"code\":\"" + code + "\",\"message\":\"test\"}}"
            };

        private static FlockHttpResponse Transport(FlockHttpResult result, string body = null)
            => new FlockHttpResponse { Result = result, Body = body };

        // Sends the canned response off Unity's sync-context so blocking can't deadlock; rethrows the inner exception.
        private static void Send(FlockHttpResponse canned)
        {
            FlockHttpClient.Configure(new FakeAdapter(canned));
            Task.Run(() => FlockHttpClient.GetAsync<Shop>(Url, null, CancellationToken.None)).GetAwaiter().GetResult();
        }

        [TearDown]
        public void TearDown()
        {
            // Restore the real platform transport so a fake can't leak into the next test.
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (_createdClient)
            {
                FlockClient.Shutdown();
                _createdClient = false;
            }
        }

        // Hermetic: every exception path.

        [Test]
        public void Timeout_Throws_Network()
            => Assert.Throws<FlockNetworkException>(() => Send(Transport(FlockHttpResult.Timeout)));

        [Test]
        public void ConnectionError_Throws_Network()
            => Assert.Throws<FlockNetworkException>(() => Send(Transport(FlockHttpResult.ConnectionError, "offline")));

        [Test]
        public void Status401_Coded_Throws_Auth_WithCode()
        {
            FlockAuthException ex = Assert.Throws<FlockAuthException>(() => Send(Coded(401, "player.invalid_refresh_token")));
            Assert.AreEqual(FlockErrorCode.PlayerInvalidRefreshToken, ex.ErrorCode);
            Assert.AreEqual(401, ex.StatusCode);
        }

        [Test]
        public void Status400_Coded_Throws_Validation_WithCode()
        {
            FlockValidationException ex = Assert.Throws<FlockValidationException>(() => Send(Coded(400, "player.email_already_registered")));
            Assert.AreEqual(FlockErrorCode.PlayerEmailAlreadyRegistered, ex.ErrorCode);
        }

        [Test]
        public void Status400_NameAlreadyRegistered_Throws_Validation_WithCode()
        {
            FlockValidationException ex = Assert.Throws<FlockValidationException>(() => Send(Coded(400, "player.name_already_registered")));
            Assert.AreEqual(FlockErrorCode.PlayerNameAlreadyRegistered, ex.ErrorCode);
        }

        [Test]
        public void Status422_Coded_Throws_Validation_WithCode()
        {
            FlockValidationException ex = Assert.Throws<FlockValidationException>(() => Send(Coded(422, "shop.insufficient_funds")));
            Assert.AreEqual(FlockErrorCode.ShopInsufficientFunds, ex.ErrorCode);
        }

        [Test]
        public void Status404_Coded_Throws_Network_WithCode()
        {
            FlockNetworkException ex = Assert.Throws<FlockNetworkException>(() => Send(Coded(404, "shop.shop_not_found")));
            Assert.AreEqual(FlockErrorCode.ShopShopNotFound, ex.ErrorCode);
            Assert.AreEqual(404, ex.StatusCode);
        }

        [Test]
        public void EmptyBody_Throws_Serialization()
            => Assert.Throws<FlockSerializationException>(() =>
                Send(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = "" }));

        [Test]
        public void MalformedBody_Throws_Serialization()
            => Assert.Throws<FlockSerializationException>(() =>
                Send(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 200, Body = "<<<not-json>>>" }));

        [Test]
        public void Parse_MapsKnownCodes_AndFallsBackToUnknown()
        {
            Assert.AreEqual(FlockErrorCode.ShopInsufficientFunds, FlockErrorCodes.Parse("shop.insufficient_funds"));
            Assert.AreEqual(FlockErrorCode.PlayerEmailAlreadyRegistered, FlockErrorCodes.Parse("player.email_already_registered"));
            Assert.AreEqual(FlockErrorCode.ShopItemShopNotFound, FlockErrorCodes.Parse("shop_item.shop_not_found"));
            Assert.AreEqual(FlockErrorCode.GameConfigInvalidTag, FlockErrorCodes.Parse("game_config.invalid_tag"));
            Assert.AreEqual(FlockErrorCode.PlayerNameAlreadyRegistered, FlockErrorCodes.Parse("player.name_already_registered"));
            Assert.AreEqual(FlockErrorCode.PlayerPlayerNotFound, FlockErrorCodes.Parse("player.player_not_found"));
            Assert.AreEqual(FlockErrorCode.PlayerNoEmailAccount, FlockErrorCodes.Parse("player.no_email_account"));
            Assert.AreEqual(FlockErrorCode.Unknown, FlockErrorCodes.Parse("server.brand_new_code"));
            Assert.AreEqual(FlockErrorCode.Unknown, FlockErrorCodes.Parse(null));
            Assert.AreEqual(FlockErrorCode.Unknown, FlockErrorCodes.Parse(""));
        }

        // Builds a FlockException carrying a given wire code, same as the pipeline would.
        private static FlockException Exc(string code) => new FlockException("test") { Code = code };

        [Test]
        public void IsAlreadyRegistered_TrueForEveryIdentityCode()
        {
            Assert.IsTrue(Exc("player.email_already_registered").IsAlreadyRegistered());
            Assert.IsTrue(Exc("player.device_already_registered").IsAlreadyRegistered());
            Assert.IsTrue(Exc("player.google_account_already_registered").IsAlreadyRegistered());
            Assert.IsTrue(Exc("player.apple_account_already_registered").IsAlreadyRegistered());
            Assert.IsTrue(Exc("player.steam_account_already_registered").IsAlreadyRegistered());
        }

        [Test]
        public void IsAlreadyRegistered_FalseForNameTakenAndUnrelated()
        {
            // A taken display name is a different remediation, so it must NOT be grouped as "already registered".
            Assert.IsFalse(Exc("player.name_already_registered").IsAlreadyRegistered());
            Assert.IsFalse(Exc("shop.insufficient_funds").IsAlreadyRegistered());
            Assert.IsFalse(Exc(null).IsAlreadyRegistered());
        }

        // Live: real backend, intentional error. Manual only — needs FlockConfig.asset + network.

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually from the Test Runner.")]
        public IEnumerator Live_BadLogin_ReturnsCodedServerError()
        {
            FlockConfigAsset asset = Resources.Load<FlockConfigAsset>("FlockConfig");
            if (asset == null || !asset.IsValid(out string _) || string.IsNullOrEmpty(asset.gameVersionId))
            {
                Assert.Ignore("No usable FlockConfig.asset (missing fields or unresolved Game Version ID).");
                yield break;
            }

            if (!FlockClient.IsInitialized)
            {
                FlockClient.Create(asset.ToInitConfig());
                _createdClient = true;
            }

            Task<PlayerLoginResponse> login = FlockClient.Instance.Authentication.LoginWithEmailAsync(
                "flock-sdk-test-no-such-user@example.invalid", "definitely-wrong-password");
            while (!login.IsCompleted)
                yield return null;

            Assert.IsTrue(login.IsFaulted, "Expected the bad-credentials login to fail.");
            FlockException ex = login.Exception?.GetBaseException() as FlockException;
            Assert.IsNotNull(ex, "Expected a FlockException from the server.");

            // No HTTP status means a transport/network problem, not a server answer — don't fail the suite on it.
            if (ex.StatusCode == null)
            {
                Assert.Inconclusive($"Live backend unreachable: {ex.Message}");
                yield break;
            }

            Debug.Log($"[Flock] Live server error — status {ex.StatusCode}, code '{ex.Code}', ErrorCode {ex.ErrorCode}, message: {ex.Message}");
            Assert.AreNotEqual(FlockErrorCode.Unknown, ex.ErrorCode, "Server error carried no recognized coded detail.");
        }
    }
}
