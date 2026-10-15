using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests
{
    // Every failure a developer sees says what is wrong and how to fix it: the server's own reason when it gave one, the plain
    // meaning of the status or the transport failure otherwise, and a fix that is never empty.
    public class FlockErrorFixTests
    {
        private const string Url = "https://test.invalid/v1/test";

        // The texts every failure used to read as, which named neither the cause nor the fix.
        private static readonly string[] OldGenericTexts =
        {
            "HTTP request failed", "Authentication failed", "Validation failed", "Network request failed", "Request timeout",
            "Empty response from server", "Malformed response body", "Invalid response from server",
        };

        private sealed class FakeAdapter : IFlockHttpAdapter
        {
            private readonly FlockHttpResponse _response;
            public FakeAdapter(FlockHttpResponse response) { _response = response; }
            public Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
                => Task.FromResult(_response);
        }

        private static FlockHttpResponse Answer(int status, string body)
            => new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = status, Body = body };

        // One read through the real client against an answer, so the classification under test is the one games get.
        private static FlockException FailureFor(FlockHttpResponse canned)
        {
            FlockHttpClient.Configure(new FakeAdapter(canned));
            try
            {
                Task.Run(() => FlockHttpClient.GetAsync<Shop>(Url, null, CancellationToken.None)).GetAwaiter().GetResult();
            }
            catch (FlockException failure)
            {
                return failure;
            }
            Assert.Fail("the read was expected to fail");
            return null;
        }

        [TearDown]
        public void TearDown()
        {
            FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        // The report this was made for: a purchase given the item's name answered "HTTP request failed (HTTP 404)".
        [Test]
        public void APurchaseGivenTheWrongIdSaysWhichIdItTakes()
        {
            // The answer as the local backend sent it on 2026-10-15 for GET /v1/shop_item/GoldItem (the item's name, not its id).
            FlockFakeTransport transport = new FlockFakeTransport()
                .On("shop_item/GoldItem", FlockFakeTransport.Status(404,
                    "{\"detail\":{\"code\":\"shop_item.shop_item_not_found\",\"message\":\"Shop item with ID 'GoldItem' not found\"}}"));
            using (FlockTestClient harness = FlockTestClient.Create(transport))
            {
                harness.LoginAs("player-a");
                FlockException failure = Assert.Catch<FlockException>(() => harness.Run(() => harness.Client.Shop.PurchaseAsync("GoldItem")));

                Assert.AreEqual(FlockErrorCode.ShopItemShopItemNotFound, failure.ErrorCode);
                Assert.AreEqual("Shop item with ID 'GoldItem' not found", failure.ServerMessage);
                StringAssert.Contains("ShopItem.Id", failure.Hint, "the fix names the id a purchase takes");
                StringAssert.Contains("not the item's name or the shop's id", failure.Hint);
                StringAssert.Contains("Shop item with ID 'GoldItem' not found [shop_item.shop_item_not_found, HTTP 404]\nFix: " + failure.Hint, failure.Message);
                foreach (string old in OldGenericTexts)
                    StringAssert.DoesNotContain(old, failure.Message);
                Assert.AreEqual(0, transport.CountTo("shop/transaction"), "nothing was bought");
            }

            // Counter-case: the item reads, and the purchase itself is refused on the purchase route's own code.
            FlockFakeTransport bought = new FlockFakeTransport()
                .On("shop_item/item-1", FlockFakeTransport.Ok(
                    "{\"id\":\"item-1\",\"name\":\"GoldItem\",\"status\":\"active\",\"type\":\"standard\",\"shop_id\":\"shop-1\",\"price\":10,\"currency\":\"GOLD\",\"created_at\":\"2026-10-15T00:00:00Z\",\"updated_at\":\"2026-10-15T00:00:00Z\"}"))
                .On("shop/transaction", FlockFakeTransport.Status(404, "{\"detail\":{\"code\":\"shop.item_not_found\",\"message\":\"Shop item not found\"}}"));
            using (FlockTestClient harness = FlockTestClient.Create(bought))
            {
                harness.LoginAs("player-a");
                FlockException refused = Assert.Catch<FlockException>(() => harness.Run(() => harness.Client.Shop.PurchaseAsync("item-1")));
                Assert.AreEqual(FlockErrorCode.ShopItemNotFound, refused.ErrorCode);
                Assert.AreEqual(FlockErrorHints.For(FlockErrorCode.ShopItemNotFound), refused.Hint);
                StringAssert.Contains("ShopItem.Id", refused.Hint);
            }
        }

        // Each answer measured from the local backend on 2026-10-15 reads as its cause and carries its code's fix.
        [TestCase(404, "{\"detail\":{\"code\":\"request.not_found\",\"message\":\"Not Found\"}}", FlockErrorCode.RequestNotFound, "Not Found")]
        [TestCase(405, "{\"detail\":{\"code\":\"request.method_not_allowed\",\"message\":\"Method Not Allowed\"}}", FlockErrorCode.RequestMethodNotAllowed, "Method Not Allowed")]
        [TestCase(422, "{\"detail\":{\"code\":\"request.validation_failed\",\"message\":\"body.shop_item_id: Field required\",\"fields\":[{\"type\":\"missing\",\"loc\":[\"body\",\"shop_item_id\"],\"msg\":\"Field required\",\"input\":{}}]}}", FlockErrorCode.RequestValidationFailed, "body.shop_item_id: Field required")]
        [TestCase(401, "{\"detail\":{\"code\":\"game.missing_api_key\",\"message\":\"Missing X-Flock-API-Key header\"}}", FlockErrorCode.GameMissingApiKey, "Missing X-Flock-API-Key header")]
        [TestCase(401, "{\"detail\":{\"code\":\"game.invalid_api_key\",\"message\":\"Invalid API Key\"}}", FlockErrorCode.GameInvalidApiKey, "Invalid API Key")]
        [TestCase(401, "{\"detail\":{\"code\":\"player.missing_token\",\"message\":\"Missing token.\"}}", FlockErrorCode.PlayerMissingToken, "Missing token.")]
        [TestCase(401, "{\"detail\":{\"code\":\"player.invalid_token\",\"message\":\"Invalid token.\"}}", FlockErrorCode.PlayerInvalidToken, "Invalid token.")]
        [TestCase(404, "{\"detail\":{\"code\":\"shop.item_not_found\",\"message\":\"Shop item not found\"}}", FlockErrorCode.ShopItemNotFound, "Shop item not found")]
        [TestCase(404, "{\"detail\":{\"code\":\"shop.player_not_found\",\"message\":\"Player not found\"}}", FlockErrorCode.ShopPlayerNotFound, "Player not found")]
        [TestCase(404, "{\"detail\":{\"code\":\"shop.shop_not_found\",\"message\":\"Shop with name 'no-such-shop' not found\"}}", FlockErrorCode.ShopShopNotFound, "Shop with name 'no-such-shop' not found")]
        [TestCase(404, "{\"detail\":{\"code\":\"leaderboard.not_found\",\"message\":\"Leaderboard 'Nope' not found\"}}", FlockErrorCode.LeaderboardNotFound, "Leaderboard 'Nope' not found")]
        public void TheMeasuredAnswersSayWhatIsWrongAndHowToFixIt(int status, string body, FlockErrorCode code, string reason)
        {
            FlockException failure = FailureFor(Answer(status, body));

            Assert.AreEqual(code, failure.ErrorCode);
            Assert.AreEqual(reason, failure.ServerMessage);
            Assert.AreEqual(FlockErrorHints.For(code), failure.Hint);
            Assert.IsNotEmpty(failure.Hint);
            StringAssert.StartsWith(reason + " [", failure.Message);
        }

        [Test]
        public void AnAnswerWithoutAFlockCodeIsCalledOneThatDidNotComeFromFlock()
        {
            // An API URL without /v1 reaches the dashboard's routes, which answer with no Flock code: not the player's sign-in.
            FlockException noVersion = FailureFor(Answer(401, "{\"detail\":\"Invalid token.\"}"));
            Assert.AreEqual("Invalid token.", noVersion.ServerMessage);
            StringAssert.Contains("no Flock error code", noVersion.Hint);
            StringAssert.Contains("API URL", noVersion.Hint);
            StringAssert.Contains("API URL in Flock > Settings", FlockErrorHints.For(FlockErrorCode.RequestNotFound));
        }

        // Every status, body shape and transport failure reads as a cause and carries a fix.
        [Test]
        public void EveryFailureSaysWhatIsWrongAndHowToFixIt([Values(400, 401, 403, 404, 405, 408, 409, 413, 418, 422, 429, 500, 502, 503)] int status)
        {
            string[] bodies =
            {
                "",
                "<html><body>Sign in to the Wi-Fi</body></html>",
                "{\"detail\":{\"code\":\"shop.item_not_found\",\"message\":\"Shop item not found\"}}",
                "{\"detail\":{\"code\":\"player.something_new\",\"message\":\"Something new\"}}",
                "{\"detail\":[{\"loc\":[\"body\",\"name\"],\"msg\":\"Field required\",\"type\":\"missing\"}]}",
            };
            foreach (string body in bodies)
            {
                FlockException failure = FailureFor(Answer(status, body));
                Assert.IsNotEmpty(failure.Hint, $"HTTP {status}, '{body}': says how to fix it");
                StringAssert.Contains($"HTTP {status}]", failure.Message);
                StringAssert.Contains("\nFix: " + failure.Hint, failure.Message);
                foreach (string old in OldGenericTexts)
                    StringAssert.DoesNotContain(old, failure.Message);
            }

            FlockException bare = FailureFor(Answer(status, ""));
            if (status >= 500)
                StringAssert.Contains("failed while handling the call", bare.Hint);
            else if (status == 408 || status == 429)
                StringAssert.Contains("slow down", bare.Hint);
            else
            {
                StringAssert.Contains("no Flock error code", bare.Hint);
                StringAssert.Contains("API URL", bare.Hint);
            }

            FlockException newer = FailureFor(Answer(status, "{\"detail\":{\"code\":\"player.something_new\",\"message\":\"Something new\"}}"));
            Assert.AreEqual("Something new", newer.ServerMessage);
            if (status < 500)
                StringAssert.Contains("player.something_new", newer.Hint);
        }

        [Test]
        public void NoAnswerAndAnUnreadableAnswerEachCarryTheirOwnFix()
        {
            StringAssert.DoesNotContain("uthenticat", FailureFor(Answer(403, "")).Message, "a bare 403 does not blame the sign-in");

            FlockException timeout = FailureFor(FlockFakeTransport.Timeout());
            Assert.AreEqual(FlockErrorHints.NoAnswerInTime, timeout.Hint);
            StringAssert.Contains("may still have been made", timeout.Hint);

            FlockException offline = FailureFor(FlockFakeTransport.Offline());
            Assert.AreEqual(FlockErrorHints.CouldNotReachTheServer, offline.Hint);

            FlockException empty = FailureFor(Answer(200, ""));
            StringAssert.Contains("was empty", empty.Message);
            Assert.AreEqual(FlockErrorHints.NotFlocksAnswer, empty.Hint);

            FlockException portal = FailureFor(Answer(200, "<html>Sign in</html>"));
            Assert.IsInstanceOf<FlockSerializationException>(portal);
            Assert.AreEqual(FlockErrorHints.NotFlocksAnswer, portal.Hint);
        }

        // A failure the SDK finds itself carries its own fix, and every kind gets one when it does not give it.
        [Test]
        public void AFailureTheSdkFindsItselfCarriesAFix()
        {
            FlockException given = new FlockValidationException("Queue Name is empty") { Hint = "Pass the queue's name." };
            Assert.AreEqual("Queue Name is empty\nFix: Pass the queue's name.", given.Message);

            FlockException[] kinds =
            {
                new FlockException("x"), new FlockNetworkException("x"), new FlockAuthException("x"), new FlockValidationException("x"),
                new FlockSerializationException("x"),
            };
            foreach (FlockException kind in kinds)
            {
                Assert.IsNotEmpty(kind.Hint, $"{kind.GetType().Name} made with no fix still carries one");
                StringAssert.Contains("\nFix: ", kind.Message);
            }
            Assert.AreEqual(FlockErrorHints.SignInFirst, new FlockAuthException("x").Hint);

            // Through a provider: an empty argument, and a call that acts as the player with nobody signed in.
            using (FlockTestClient harness = FlockTestClient.Create(new FlockFakeTransport()))
            {
                FlockValidationException noId = Assert.Throws<FlockValidationException>(() => harness.Run(() => harness.Client.Shop.PurchaseAsync("")));
                StringAssert.StartsWith("Shop Item ID is empty", noId.Message);
                Assert.AreEqual("Pass a Shop Item ID; this call cannot run without one.", noId.Hint);

                FlockValidationException nobody = Assert.Throws<FlockValidationException>(() => harness.Run(() => harness.Client.Shop.PurchaseAsync("item-1")));
                Assert.AreEqual(FlockErrorHints.SignInFirst, nobody.Hint);
            }
        }

        // A relay failure is made with its reason, and the reason decides the fix: each one the relay gives has a fix of its own, so a
        // reason added without one fails here rather than reaching a developer with the fallback.
        [Test]
        public void EveryRelayReasonCarriesAFixOfItsOwn()
        {
            string fallback = FlockRelayFailure.FixFor("a reason the relay never gives");
            string[] reasons = typeof(FlockRelayFailure).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetValue(null))
                .ToArray();
            Assert.Greater(reasons.Length, 10, "Precondition: the reasons are read off FlockRelayFailure");
            foreach (string reason in reasons)
            {
                FlockRelayException failure = new FlockRelayException(reason, "The relay could not be used");
                Assert.AreEqual(FlockRelayFailure.FixFor(reason), failure.Hint, $"{reason}: the exception carries its reason's fix");
                Assert.AreNotEqual(fallback, failure.Hint, $"{reason} has a fix of its own");
            }
        }
    }
}
