using Flock.Logging;
using System;
using System.Collections;
using System.Text.RegularExpressions;
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

        // A real-money item sent through the wallet purchase, body as the backend's shop settlement code builds it (and as a live
        // run measured it on 2026-10-21). The route's documented responses leave the code out, which is how the snapshot missed
        // it until 2026-10-15.
        [Test]
        public void RealMoneyItemRefusal_ParsesToItsCode_WithItsHint()
        {
            const string body = "{\"detail\":{\"code\":\"shop.real_currency_needs_checkout\",\"message\":\"'USD' is a real currency and cannot be paid from a wallet; open a checkout with POST /v1/shop/checkout, or verify a store receipt with POST /v1/shop/validate-receipt\"}}";
            FlockValidationException ex = Assert.Throws<FlockValidationException>(() =>
                Send(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = 422, Body = body }));

            Assert.AreEqual(FlockErrorCode.ShopRealCurrencyNeedsCheckout, ex.ErrorCode);
            Assert.AreEqual(422, ex.StatusCode);
            Assert.IsNotNull(ex.ServerMessage, "The server's own reason should survive parsing.");
            Assert.AreEqual(FlockErrorHints.For(FlockErrorCode.ShopRealCurrencyNeedsCheckout), ex.Hint);
            Assert.IsNotNull(ex.Hint);
        }

        // Every refusal of the payment routes (redeeming a store receipt, opening a web checkout), body as the backend sends it:
        // the first nineteen copied from live responses on 2026-10-21, one row per reason; the next six the backend's own messages,
        // not seen live (a store that cannot be reached, a store answer it cannot read, a Google payment still pending, a receipt
        // for another product, a provider that would not open a checkout, a purchase id the game does not have); the rest the
        // payment hardening's, each marked below as measured or read.
        private static readonly object[] PaymentRefusals =
        {
            new object[] { 503, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.credentials_unreadable\",\"message\":\"Apple credentials could not be used to sign a request\"}}", FlockErrorCode.PaymentProviderCredentialsUnreadable },
            new object[] { 503, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.credentials_unreadable\",\"message\":\"Google service account credentials are unusable\"}}", FlockErrorCode.PaymentProviderCredentialsUnreadable },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.provider_not_found\",\"message\":\"Provider 'apple_app_store' is not configured for this game\"}}", FlockErrorCode.PaymentProviderProviderNotFound },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.provider_not_found\",\"message\":\"Provider 'stripe' is not configured for this game\"}}", FlockErrorCode.PaymentProviderProviderNotFound },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.provider_not_supported_here\",\"message\":\"'APPLE_APP_STORE' is not a store; use the checkout endpoint instead\"}}", FlockErrorCode.PaymentProviderProviderNotSupportedHere },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.provider_not_supported_here\",\"message\":\"'steam' is not a store; use the checkout endpoint instead\"}}", FlockErrorCode.PaymentProviderProviderNotSupportedHere },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.provider_not_supported_here\",\"message\":\"'stripe' is not a store; use the checkout endpoint instead\"}}", FlockErrorCode.PaymentProviderProviderNotSupportedHere },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: Apple returned 401\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: Google rejected the service account (400)\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: no transaction_id supplied\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: product_id and purchase_token are both required\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_product_mismatch\",\"message\":\"This item has no apple_app_store product id configured\"}}", FlockErrorCode.PaymentProviderReceiptProductMismatch },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_product_mismatch\",\"message\":\"This item has no google_play product id configured\"}}", FlockErrorCode.PaymentProviderReceiptProductMismatch },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.not_a_real_currency_item\",\"message\":\"'Gold' is a game currency; buy this through the transaction endpoint instead\"}}", FlockErrorCode.ShopNotARealCurrencyItem },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.real_currency_settlement_unavailable\",\"message\":\"This game has no web payment provider configured\"}}", FlockErrorCode.ShopRealCurrencySettlementUnavailable },
            new object[] { 503, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"shop.real_currency_settlement_unavailable\",\"message\":\"PAYMENT_WEBHOOK_BASE_URL is not configured, so the provider would have nowhere to confirm this payment\"}}", FlockErrorCode.ShopRealCurrencySettlementUnavailable },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.webhook_provider_unknown\",\"message\":\"'apple_app_store' does not open web checkouts\"}}", FlockErrorCode.ShopWebhookProviderUnknown },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.webhook_provider_unknown\",\"message\":\"'paypal' does not open web checkouts\"}}", FlockErrorCode.ShopWebhookProviderUnknown },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.webhook_provider_unknown\",\"message\":\"Several payment providers are active; name one of stripe, tap\"}}", FlockErrorCode.ShopWebhookProviderUnknown },
            new object[] { 502, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Could not reach apple_app_store to verify the receipt\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: the store returned an unreadable response\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: purchase state is 2, not purchased\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_product_mismatch\",\"message\":\"Receipt is for 'gems_10', but this item sells 'gems_100'\"}}", FlockErrorCode.PaymentProviderReceiptProductMismatch },
            new object[] { 502, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.checkout_failed\",\"message\":\"Could not open a tap checkout: gateway timeout\"}}", FlockErrorCode.PaymentProviderCheckoutFailed },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"shop.purchase_not_found\",\"message\":\"Purchase not found\"}}", FlockErrorCode.ShopPurchaseNotFound },
            // The payment hardening's codes (backend fix/sdk-payment-hardening): the server's own messages, read in its code.
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: could not read the transaction Apple returned\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 503, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"payment_provider.store_unavailable\",\"message\":\"Apple could not be asked about this receipt (HTTP 503); try again later\"}}", FlockErrorCode.PaymentProviderStoreUnavailable },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.store_credentials_rejected\",\"message\":\"Apple rejected this game's store credentials (HTTP 401); the studio's payment settings need fixing before receipts can be verified\"}}", FlockErrorCode.PaymentProviderStoreCredentialsRejected },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"payment_provider.receipt_pending\",\"message\":\"Google reports this payment as still in progress; ask again later\"}}", FlockErrorCode.PaymentProviderReceiptPending },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"payment_provider.receipt_belongs_to_another_player\",\"message\":\"Receipt refused: the store purchase belongs to a different player\"}}", FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"payment_provider.receipt_belongs_to_another_player\",\"message\":\"Receipt refused: this store transaction was redeemed by another player\"}}", FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"shop.player_mismatch\",\"message\":\"player_id does not match the signed-in player\"}}", FlockErrorCode.ShopPlayerMismatch },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.player_required\",\"message\":\"Send the player's token, or name player_id in the body\"}}", FlockErrorCode.ShopPlayerRequired },
            new object[] { 422, typeof(FlockValidationException), "{\"detail\":{\"code\":\"shop.purchase_not_found\",\"message\":\"Unknown purchase status 'done'\"}}", FlockErrorCode.ShopPurchaseNotFound },
        };

        [TestCaseSource(nameof(PaymentRefusals))]
        public void PaymentRefusal_ParsesToItsCode_WithItsHint(int status, Type exceptionType, string body, FlockErrorCode expected)
            => AssertRefusalParses(status, exceptionType, body, expected);

        // A coded refusal becomes its exception type with its code, status, the server's reason and its code's hint.
        private static void AssertRefusalParses(int status, Type exceptionType, string body, FlockErrorCode expected)
        {
            FlockException ex = Assert.Catch<FlockException>(() =>
                Send(new FlockHttpResponse { Result = FlockHttpResult.Success, StatusCode = status, Body = body }));

            Assert.AreEqual(expected, ex.ErrorCode);
            Assert.AreEqual(exceptionType, ex.GetType());
            Assert.AreEqual(status, ex.StatusCode);
            Assert.IsNotNull(ex.ServerMessage, "The server's own reason should survive parsing.");
            Assert.AreEqual(FlockErrorHints.For(expected), ex.Hint);
            Assert.IsNotNull(ex.Hint);
        }

        // These codes each have more than one cause, and only the server's reason tells them apart, so the hint covers every
        // cause; a cause that is not final yet gets "send it again later", never the bad receipt's fix. A phrase marked as the
        // server's is quoted in the hint, so it must be one a refusal above really carries.
        private static readonly object[] PaymentCauses =
        {
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "Apple returned 401", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "Google rejected the service account", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "Could not reach", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "unreadable", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "purchase state is 2", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "429 or a 5xx", false },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "purchase token", false },
            new object[] { FlockErrorCode.PaymentProviderReceiptProductMismatch, "product id configured", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptProductMismatch, "but this item sells", true },
            new object[] { FlockErrorCode.PaymentProviderProviderNotFound, "never connected", false },
            new object[] { FlockErrorCode.PaymentProviderProviderNotFound, "paused", false },
            new object[] { FlockErrorCode.PaymentProviderCredentialsUnreadable, "trying again will not help", false },
            new object[] { FlockErrorCode.ShopRealCurrencySettlementUnavailable, "no web payment provider configured", true },
            new object[] { FlockErrorCode.ShopRealCurrencySettlementUnavailable, "PAYMENT_WEBHOOK_BASE_URL is not configured", true },
            new object[] { FlockErrorCode.ShopWebhookProviderUnknown, "does not open web checkouts", true },
            new object[] { FlockErrorCode.ShopWebhookProviderUnknown, "Several payment providers are active", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "could not read", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptInvalid, "An older Flock server", false },
            new object[] { FlockErrorCode.PaymentProviderStoreUnavailable, "send the same receipt again later", false },
            new object[] { FlockErrorCode.PaymentProviderReceiptPending, "send the same receipt again later", false },
            new object[] { FlockErrorCode.PaymentProviderStoreCredentialsRejected, "the same receipt goes through", false },
            new object[] { FlockErrorCode.PaymentProviderStoreCredentialsRejected, "Google Play Console", false },
            new object[] { FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer, "belongs to a different player", true },
            new object[] { FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer, "redeemed by another player", true },
            new object[] { FlockErrorCode.ShopPurchaseNotFound, "another player's purchase", false },
            new object[] { FlockErrorCode.ShopPurchaseNotFound, "Unknown purchase status", true },
            new object[] { FlockErrorCode.ShopPlayerMismatch, "Leave playerId out", false },
            // Consuming while signed in answers another player's entry as not found (the same hardening).
            new object[] { FlockErrorCode.PlayerInventoryInventoryEntryNotFound, "another player's", false },
        };

        [TestCaseSource(nameof(PaymentCauses))]
        public void EachCauseOfAPaymentCode_IsToldApartInItsHint(FlockErrorCode code, string phrase, bool quotesTheServer)
        {
            StringAssert.Contains(phrase, FlockErrorHints.For(code), code + "'s hint does not cover the cause \"" + phrase + "\" names.");
            if (!quotesTheServer)
                return;

            bool sent = false;
            foreach (object[] refusal in PaymentRefusals)
            {
                if ((FlockErrorCode)refusal[3] == code && ((string)refusal[2]).Contains(phrase))
                    sent = true;
            }
            Assert.IsTrue(sent, code + "'s hint quotes \"" + phrase + "\", which no " + code + " refusal carries: quote the server's reason as it sends it.");
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

        // Every matchmaking, party and session refusal, body as the backend sends it: copied from live responses on
        // 2026-10-05 (the two version_mismatch codes on 2026-10-08), except party_too_large, player_not_eligible and
        // mint_rate_limited (the spec's own examples) and join_code_unavailable, already_in_session, the team-size
        // party_too_large and search_input_required (the backend's messages; none seen live).
        private static readonly object[] MultiplayerRefusals =
        {
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"matchmaking.already_queued\",\"message\":\"Player 01M46J2QM0KSQ1X3QNF03RRT6V is already queued for this game\"}}", FlockErrorCode.MatchmakingAlreadyQueued },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"matchmaking.match_not_found\",\"message\":\"Match not found\"}}", FlockErrorCode.MatchmakingMatchNotFound },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"matchmaking.not_party_leader\",\"message\":\"Only the party leader can queue the party\"}}", FlockErrorCode.MatchmakingNotPartyLeader },
            new object[] { 400, typeof(FlockValidationException), "{\"detail\":{\"code\":\"matchmaking.party_too_large\",\"message\":\"The party has more players than the queue allows\"}}", FlockErrorCode.MatchmakingPartyTooLarge },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"matchmaking.player_not_eligible\",\"message\":\"A player is not eligible for this queue\"}}", FlockErrorCode.MatchmakingPlayerNotEligible },
            new object[] { 400, typeof(FlockValidationException), "{\"detail\":{\"code\":\"matchmaking.party_too_large\",\"message\":\"This party has 3 players but a team in this queue holds at most 2\"}}", FlockErrorCode.MatchmakingPartyTooLarge },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"matchmaking.queue_not_found\",\"message\":\"Matchmaking queue not found\"}}", FlockErrorCode.MatchmakingQueueNotFound },
            new object[] { 400, typeof(FlockValidationException), "{\"detail\":{\"code\":\"matchmaking.search_input_required\",\"message\":\"This queue matches by skill and player 01M46J2QM0KSQ1X3QNF03RRT6V sent no skill\"}}", FlockErrorCode.MatchmakingSearchInputRequired },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"matchmaking.ticket_not_cancelable\",\"message\":\"This ticket is no longer queued\"}}", FlockErrorCode.MatchmakingTicketNotCancelable },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"matchmaking.ticket_not_found\",\"message\":\"Ticket not found\"}}", FlockErrorCode.MatchmakingTicketNotFound },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"matchmaking.version_mismatch\",\"message\":\"Player 01M46J4VCTQ54NSPP206GSA069 is on a different game version than the party leader, and this queue matches same-version players only\"}}", FlockErrorCode.MatchmakingVersionMismatch },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.already_in_session\",\"message\":\"Player is already in a live multiplayer session for this game\"}}", FlockErrorCode.MultiplayerAlreadyInSession },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.invalid_join_code\",\"message\":\"Join code is not valid\"}}", FlockErrorCode.MultiplayerInvalidJoinCode },
            new object[] { 400, typeof(FlockValidationException), "{\"detail\":{\"code\":\"multiplayer.invalid_join_token\",\"message\":\"Join token is not valid\"}}", FlockErrorCode.MultiplayerInvalidJoinToken },
            new object[] { 500, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.join_code_unavailable\",\"message\":\"Could not allocate a join code, try again\"}}", FlockErrorCode.MultiplayerJoinCodeUnavailable },
            new object[] { 429, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.mint_rate_limited\",\"message\":\"This player has requested relay credentials too frequently\"}}", FlockErrorCode.MultiplayerMintRateLimited },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.not_a_participant\",\"message\":\"You are not in this session\"}}", FlockErrorCode.MultiplayerNotAParticipant },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"multiplayer.not_host\",\"message\":\"Only the host can perform this action\"}}", FlockErrorCode.MultiplayerNotHost },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.session_full\",\"message\":\"This session is full\"}}", FlockErrorCode.MultiplayerSessionFull },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.session_not_found\",\"message\":\"Session not found\"}}", FlockErrorCode.MultiplayerSessionNotFound },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.target_not_a_participant\",\"message\":\"That player is not in this session\"}}", FlockErrorCode.MultiplayerTargetNotAParticipant },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"multiplayer.version_mismatch\",\"message\":\"This session is on a different game version\"}}", FlockErrorCode.MultiplayerVersionMismatch },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"party.already_in_party\",\"message\":\"You are already in a party for this game\"}}", FlockErrorCode.PartyAlreadyInParty },
            new object[] { 400, typeof(FlockValidationException), "{\"detail\":{\"code\":\"party.cannot_kick_leader\",\"message\":\"The leader cannot be kicked; leave or transfer leadership instead\"}}", FlockErrorCode.PartyCannotKickLeader },
            new object[] { 409, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"party.full\",\"message\":\"This party is full\"}}", FlockErrorCode.PartyFull },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"party.invalid_invite_code\",\"message\":\"Invite code is not valid\"}}", FlockErrorCode.PartyInvalidInviteCode },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"party.not_a_member\",\"message\":\"You are not a member of this party\"}}", FlockErrorCode.PartyNotAMember },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"party.not_found\",\"message\":\"Party not found\"}}", FlockErrorCode.PartyNotFound },
            new object[] { 403, typeof(FlockAuthException), "{\"detail\":{\"code\":\"party.not_party_leader\",\"message\":\"Only the party leader can perform this action\"}}", FlockErrorCode.PartyNotPartyLeader },
            new object[] { 404, typeof(FlockNetworkException), "{\"detail\":{\"code\":\"party.target_not_a_member\",\"message\":\"That player is not a member of this party\"}}", FlockErrorCode.PartyTargetNotAMember },
        };

        [TestCaseSource(nameof(MultiplayerRefusals))]
        public void MultiplayerRefusal_ParsesToItsCode_WithItsHint(int status, Type exceptionType, string body, FlockErrorCode expected)
            => AssertRefusalParses(status, exceptionType, body, expected);

        // A 403 here arrives as FlockAuthException but means "not allowed", not "signed out", so its hint never sends the player to sign in.
        [Test]
        public void PermissionRefusalHints_NeverSendThePlayerToSignIn()
        {
            int checkedCodes = 0;
            foreach (object[] refusal in MultiplayerRefusals)
            {
                if ((int)refusal[0] != 403)
                    continue;
                FlockErrorCode code = (FlockErrorCode)refusal[3];
                string hint = FlockErrorHints.For(code);
                Assert.IsNotNull(hint, code + " has no hint.");
                Assert.IsFalse(
                    Regex.IsMatch(hint, @"\b(sign|log)(g?ed)?[\s-]?(in|out)\b|authenticat", RegexOptions.IgnoreCase),
                    code + "'s hint sends the player to sign in, but this refusal is about permission, not sign-in: " + hint);
                checkedCodes++;
            }

            Assert.Greater(checkedCodes, 0, "The refusal table has no 403 rows, so nothing was checked.");
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
                FlockClient.Create(asset.ToInitConfig(), new NullFlockLogger());
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
                Assert.Ignore($"Live backend unreachable: {ex.Message}");
                yield break;
            }

            Debug.Log($"[Flock] Live server error — status {ex.StatusCode}, code '{ex.Code}', ErrorCode {ex.ErrorCode}, message: {ex.Message}");
            Assert.AreNotEqual(FlockErrorCode.Unknown, ex.ErrorCode, "Server error carried no recognized coded detail.");
        }
    }
}
