using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Logging;
using Flock.Models;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // ShopProvider: catalog reads (by-id / by-name), money-safe purchase (non-idempotent, ambiguous failures
    // surface without retry), and never-cached inventory. Purchase re-throws on failure after recording the
    // Failed transaction.
    public class FlockShopProviderTests
    {
        // ---- SHOP-01: shop/{id} answers its shop at the root, no {"result":...} envelope ----
        [Test]
        public void GetById_Success_ReturnsShop()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopById("shop-1"), FlockFakeTransport.Ok("{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Shop r = h.Run(() => h.Client.Shop.GetByIdAsync("shop-1"));

                Assert.AreEqual("shop-1", r.Id);
                Assert.AreEqual("main", r.Name);
            }
        }

        // ---- SHOP (validation) ----
        [Test]
        public void GetById_EmptyId_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.GetByIdAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- SHOP-01 (by-name) ----
        [Test]
        public void GetByName_IncludesNameInUrl()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Ok("{\"id\":\"shop-x\",\"name\":\"myshop\",\"shop_items\":[]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                h.Run(() => h.Client.Shop.GetByNameAsync("myshop"));

                Assert.IsTrue(transport.Sent("myshop"), "By-name lookup carries the name in the URL.");
            }
        }

        // Retries must be raised above the harness default (FlockTestClient pins MaxRetries = 0) or a
        // "sent exactly once" assertion holds no matter what the idempotent flag says.
        private static RetryPolicy RetriesEnabled()
        {
            return new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero };
        }

        // The key each wallet purchase attempt carried, in the order sent.
        private static List<string> PurchaseKeys(FlockFakeTransport transport)
            => transport.AllTo(FlockEndpoints.ShopTransaction).ConvertAll(request => (string)JObject.Parse(request.JsonBody)["idempotency_key"]);

        // ---- SHOP-03: an ambiguous purchase failure (5xx) is sent again with the same key, which the server answers with the first purchase ----
        [Test]
        public void Purchase_ServerError_IsSentAgain_WithTheSameKey()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"),
                FlockFakeTransport.Ok("{\"id\":\"item-1\",\"price\":100,\"currency\":\"Gold\"}"));
            transport.OnSequence(FlockEndpoints.ShopTransaction,
                FlockFakeTransport.Status(500, "{}"),
                FlockFakeTransport.Ok("{\"purchase_id\":\"pur-1\",\"item_type\":\"standard\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PurchaseResult result = h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));

                Assert.AreEqual("pur-1", result.PurchaseId);
                List<string> keys = PurchaseKeys(transport);
                Assert.AreEqual(2, keys.Count, "An answer that may have been lost is asked again.");
                Assert.IsNotEmpty(keys[0], "Every attempt carries a key.");
                Assert.AreEqual(keys[0], keys[1], "The resend carries the first attempt's key, so the server answers it with the first purchase instead of charging again.");
            }
        }

        [Test]
        public void TwoPurchaseCalls_EachCarryAKeyOfTheirOwn()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-1\",\"item_type\":\"standard\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));
                h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));

                List<string> keys = PurchaseKeys(transport);
                Assert.AreEqual(2, keys.Count);
                Assert.AreNotEqual(keys[0], keys[1], "A second call is a second purchase, so it must not share the first one's key.");
            }
        }

        // The body the backend's purchase route sends when a key comes back with another item's id.
        [Test]
        public void Purchase_KeyAlreadyUsedForAnotherItem_ThrowsItsCode_AndIsNotSentAgain()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Status(409,
                "{\"detail\":{\"code\":\"shop.idempotency_key_reused\",\"message\":\"idempotency_key was already used for a different item\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockException ex = Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));

                Assert.AreEqual(FlockErrorCode.ShopIdempotencyKeyReused, ex.ErrorCode);
                StringAssert.Contains("Nothing was bought", ex.Hint);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopTransaction), "The server's answer will not change, so it is not sent again.");
            }
        }

        [Test]
        public void Purchase_ServerErrorOnEveryAttempt_SaysItMayHaveGoneThrough()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Status(502, "<html>Bad Gateway</html>"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockNetworkException ex = Assert.Throws<FlockNetworkException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));

                StringAssert.Contains("may have been made", ex.Hint, "A server error may come after the purchase was made.");
                StringAssert.Contains("a new PurchaseAsync call is a new purchase", ex.Hint);
            }
        }

        // ---- SHOP-03b: a 408 is retried too ----
        [Test]
        public void Purchase_RequestTimeout_IsRetried()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"),
                FlockFakeTransport.Ok("{\"id\":\"item-1\",\"price\":100,\"currency\":\"Gold\"}"));
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Status(408, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));
                Assert.Greater(transport.CountTo(FlockEndpoints.ShopTransaction), 1,
                    "408 means the server never processed it, so re-sending can't double-charge — retries are live under this policy.");
            }
        }

        // ---- SHOP (validation): empty item id ----
        [Test]
        public void Purchase_EmptyItemId_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- SHOP (validation): not signed in ----
        [Test]
        public void Purchase_NotSignedIn_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                // shop item id is present, but no player is signed in -> validation before any network.
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- SHOP-06: inventory is never cached -> offline throws ----
        [Test]
        public void GetInventory_Offline_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                transport.GoOffline();

                Assert.Catch<FlockNetworkException>(() => h.Run(() => h.Client.Shop.GetPlayerInventoryAsync()));
            }
        }

        // ---- SHOP (validation): inventory not signed in ----
        [Test]
        public void GetInventory_NotSignedIn_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.GetPlayerInventoryAsync()));
            }
        }

        // shop_item/{id} and shop/transaction both return their model at the ROOT — no {"result":...} envelope.
        // Fixtures here mirror that exactly; an enveloped stub would pass while the real backend returns nulls.
        private const string ItemBody =
            "{\"id\":\"item-1\",\"name\":\"Gold Pack\",\"type\":\"currency_pack\",\"price\":100,\"currency\":\"USD\"," +
            "\"rewards\":[{\"type\":\"currency\",\"code\":\"GOLD\",\"amount\":500}]}";

        private static FlockFakeTransport WithItem()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"), FlockFakeTransport.Ok(ItemBody));
            return transport;
        }

        // ---- SHOP-07: a purchase surfaces what it granted, plus the updated wallet ----
        [Test]
        public void Purchase_ParsesGrantedRewardsAndWallet()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok(
                "{\"purchase_id\":\"pur-1\",\"item_type\":\"currency_pack\"," +
                "\"inventory\":{\"id\":\"inv-1\",\"player_id\":\"player-a\",\"shop_item_id\":\"item-1\",\"status\":\"granted\",\"created_at\":\"2026-08-26T00:00:00Z\"}," +
                "\"granted\":[{\"type\":\"currency\",\"code\":\"GOLD\",\"amount\":500},{\"type\":\"currency\",\"code\":\"GEMS\",\"amount\":10}]," +
                "\"wallet\":{\"id\":\"pd-1\",\"player_id\":\"player-a\"}}"));

            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PurchaseResult result = h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));

                Assert.AreEqual("pur-1", result.PurchaseId);
                Assert.AreEqual("currency_pack", result.ItemType);
                Assert.AreEqual("inv-1", result.Inventory.Id, "Inventory is nested under 'inventory', not at the root.");
                Assert.AreEqual(2, result.Granted.Count);
                Assert.AreEqual(ShopItemRewardTypes.Currency, result.Granted[0].Type);
                Assert.AreEqual("GOLD", result.Granted[0].Code);
                Assert.AreEqual(500, result.Granted[0].Amount);
                Assert.AreEqual("GEMS", result.Granted[1].Code);
                Assert.AreEqual(10, result.Granted[1].Amount);
                Assert.AreEqual("pd-1", result.Wallet.Id, "Wallet rides along when the purchase moved currency.");
            }
        }

        // ---- SHOP-07b: a non-reward item omits 'granted' entirely — callers must still be able to iterate ----
        [Test]
        public void Purchase_NoGrantedField_YieldsEmptyListNotNull()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok(
                "{\"purchase_id\":\"pur-2\",\"item_type\":\"cosmetic\"," +
                "\"inventory\":{\"id\":\"inv-2\",\"player_id\":\"player-a\",\"shop_item_id\":\"item-1\",\"status\":\"owned\",\"created_at\":\"2026-08-26T00:00:00Z\"}}"));

            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PurchaseResult result = h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));

                Assert.IsNotNull(result.Granted, "Omitted 'granted' must deserialize to an empty list, never null.");
                Assert.AreEqual(0, result.Granted.Count);
                Assert.IsNull(result.Wallet, "No currency moved, so no wallet is returned.");
            }
        }

        // ---- SHOP-08: consuming an inventory row grants its rewards ----
        [Test]
        public void Consume_ParsesGrantedAndHitsConsumeRoute()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerInventoryConsume("inv-1"), FlockFakeTransport.Ok(
                "{\"inventory\":{\"id\":\"inv-1\",\"player_id\":\"player-a\",\"shop_item_id\":\"item-1\",\"status\":\"used\"," +
                "\"created_at\":\"2026-08-26T00:00:00Z\",\"used_at\":\"2026-08-26T01:00:00Z\"}," +
                "\"granted\":[{\"type\":\"currency\",\"code\":\"GOLD\",\"amount\":250}]," +
                "\"wallet\":{\"id\":\"pd-1\",\"player_id\":\"player-a\"}}"));

            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                ConsumeResult result = h.Run(() => h.Client.Shop.ConsumeAsync("inv-1"));

                Assert.IsTrue(transport.Sent("player_inventory/inv-1/consume"), "Consume addresses the inventory row by id.");
                Assert.AreEqual("used", result.Inventory.Status);
                Assert.AreEqual("2026-08-26T01:00:00Z", result.Inventory.UsedAt);
                Assert.AreEqual(1, result.Granted.Count);
                Assert.AreEqual("GOLD", result.Granted[0].Code);
                Assert.AreEqual(250, result.Granted[0].Amount);
                Assert.AreEqual("pd-1", result.Wallet.Id);
            }
        }

        // ---- SHOP-08b: consuming grants currency, so an ambiguous failure must NOT be re-sent (double-grant) ----
        [Test]
        public void Consume_ServerError_NotRetried_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerInventoryConsume("inv-1"), FlockFakeTransport.Status(500, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.ConsumeAsync("inv-1")));
                Assert.AreEqual(1, transport.CountTo("consume"), "Consuming grants currency — an ambiguous 5xx must not be retried.");
            }
        }

        // ---- SHOP (validation): empty inventory id ----
        [Test]
        public void Consume_EmptyInventoryId_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.ConsumeAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- SHOP-09: catalog reads expose an item's advertised rewards ----
        [Test]
        public void GetItem_ParsesAdvertisedRewards()
        {
            FlockFakeTransport transport = WithItem();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync("item-1"));

                Assert.AreEqual("currency_pack", item.Type);
                Assert.AreEqual(1, item.Rewards.Count);
                Assert.AreEqual("GOLD", item.Rewards[0].Code);
                Assert.AreEqual(500, item.Rewards[0].Amount);
            }
        }

        // The wallet row as the server sends it, string-typed so the test does not lean on the number converter.
        private static string WalletJson(string gold)
            => "{\"id\":\"pd-1\",\"player_template_id\":\"curr-tpl\",\"player_id\":\"player-a\"," +
               "\"data\":[{\"field_name\":\"gold\",\"type\":\"string\",\"value\":\"" + gold + "\"}]}";

        // Primes the cached wallet the way a read would, then returns the transport so a test can route the purchase.
        private static void PrimeCachedWallet(FlockTestClient h, string gold)
        {
            h.Transport.On("player_data?page=", FlockFakeTransport.Ok("{\"items\":[" + WalletJson(gold) + "],\"total\":1,\"page\":1,\"limit\":100}"));
            PlayerData primed = h.Run(() => h.Client.Player.GetMyDataByTemplateAsync("curr-tpl"));
            Assert.AreEqual(gold, primed.Data[0].Value.ToString(), "Precondition: the wallet is cached with the old balance.");
        }

        private static string CachedGold(FlockTestClient h)
            => h.Run(() => h.Client.Player.GetMyDataByTemplateAsync("curr-tpl")).Data[0].Value.ToString();

        // ---- A purchase writes the wallet it returns into the cached player data ----
        // Without it the next read serves the balance from before the purchase, and a read-modify-write puts it back.
        [Test]
        public void Purchase_WritesItsWalletToThePlayerCache()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok(
                "{\"purchase_id\":\"pur-1\",\"item_type\":\"currency_pack\",\"wallet\":" + WalletJson("500") + "}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                PrimeCachedWallet(h, "100");

                h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));

                Assert.AreEqual("500", CachedGold(h), "The cached wallet reads the balance the purchase reported.");
                Assert.AreEqual(1, transport.CountTo("player_data?page="), "Read from the cache, not fetched again.");
            }
        }

        // ---- A consume writes its wallet too ----
        [Test]
        public void Consume_WritesItsWalletToThePlayerCache()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerInventoryConsume("inv-1"), FlockFakeTransport.Ok(
                "{\"inventory\":{\"id\":\"inv-1\",\"player_id\":\"player-a\",\"shop_item_id\":\"item-1\",\"status\":\"used\"}," +
                "\"granted\":[{\"type\":\"currency\",\"code\":\"GOLD\",\"amount\":250}],\"wallet\":" + WalletJson("350") + "}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                PrimeCachedWallet(h, "100");

                h.Run(() => h.Client.Shop.ConsumeAsync("inv-1"));

                Assert.AreEqual("350", CachedGold(h), "The cached wallet reads the balance the consume reported.");
            }
        }

        // ---- No wallet means no currency moved: the cached row is left as it is, never blanked ----
        [Test]
        public void Purchase_WithoutAWallet_LeavesTheCacheAlone()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-2\",\"item_type\":\"cosmetic\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                PrimeCachedWallet(h, "100");

                h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));

                Assert.AreEqual("100", CachedGold(h));
            }
        }

        // ---- A purchase never waits on its analytics records ----
        // They are sent as it goes; a record held up (or retrying) must neither delay the purchase's answer nor fail it.
        [Test]
        public void Purchase_DoesNotWaitForItsRecords()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-1\",\"item_type\":\"currency_pack\"}"));
            transport.On(FlockEndpoints.AnalyticsTransactions, FlockFakeTransport.Ok("{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                transport.GateNext(FlockEndpoints.AnalyticsTransactions);

                Task<PurchaseResult> purchase = h.Client.Shop.PurchaseAsync("item-1");
                bool answered = purchase.Wait(TimeSpan.FromSeconds(5));
                transport.ReleaseGate();

                Assert.IsTrue(answered, "The purchase answered while its records were still on their way.");
                Assert.AreEqual("pur-1", purchase.Result.PurchaseId);
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.AnalyticsTransactions), "Both records were still sent: started and purchased.");
            }
        }

        // ---- The purchase reports where it has got to ----
        [Test]
        public void Purchase_ReadsItsStatus()
        {
            FlockFakeTransport transport = WithItem();
            transport.OnSequence(FlockEndpoints.ShopTransaction,
                FlockFakeTransport.Ok("{\"purchase_id\":\"pur-2\",\"item_type\":\"standard\",\"status\":\"pending\"}"),
                FlockFakeTransport.Ok("{\"purchase_id\":\"pur-3\",\"item_type\":\"standard\"}"),
                FlockFakeTransport.Ok("{\"purchase_id\":\"pur-4\",\"item_type\":\"standard\",\"status\":null}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                PurchaseResult pending = h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));
                Assert.AreEqual(PurchaseResultStatuses.Pending, pending.Status, "The status is read as the server spelled it.");
                Assert.IsFalse(pending.IsPaid, "A pending purchase is not paid.");

                PurchaseResult unsaid = h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));
                Assert.AreEqual(PurchaseResultStatuses.Paid, unsaid.Status, "A status left out reads as paid, the schema's default.");
                Assert.IsTrue(unsaid.IsPaid);

                PurchaseResult empty = h.Run(() => h.Client.Shop.PurchaseAsync("item-1"));
                Assert.IsTrue(empty.IsPaid, "A null status reads as left out.");
                Assert.AreEqual("paid", PurchaseResultStatuses.Paid, "The constant is the schema's spelling.");
            }
        }

        // ---- An item priced in real money is refused by the wallet purchase, on its own code, and never re-sent ----
        // The body as the backend's shop settlement code builds it: the route's documented responses leave this code out.
        [Test]
        public void Purchase_RealMoneyItem_IsRefusedWithItsOwnCode_AndNotRetried()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Status(422,
                "{\"detail\":{\"code\":\"shop.real_currency_needs_checkout\",\"message\":\"'USD' is a real currency and cannot be paid from a wallet; open a checkout with POST /v1/shop/checkout, or verify a store receipt with POST /v1/shop/validate-receipt\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockValidationException ex = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));
                Assert.AreEqual(FlockErrorCode.ShopRealCurrencyNeedsCheckout, ex.ErrorCode);
                Assert.IsNotNull(ex.Hint, "The refusal says what to do about it.");
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopTransaction), "A refusal is final: the purchase is not sent again.");
            }
        }

        // ---- Offline, a purchase is sent again with its key while it waits, and never queued ----
        // Purchase checks no reachability: it sends, and the dead network answers. Every attempt carries the one key, and the
        // purchase never enters the offline write queue, so coming back online sends nothing. The item is read while online
        // first, so the purchase reaches its own request rather than failing on the catalog read.
        [Test]
        public void Purchase_Offline_IsSentAgainWithItsKey_AndNeverQueued()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-1\",\"item_type\":\"standard\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                h.Run(() => h.Client.Shop.GetItemAsync("item-1"));

                transport.GoOffline();
                h.SetReachable(false);
                FlockNetworkException noAnswer = Assert.Throws<FlockNetworkException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));
                StringAssert.Contains("may have been made", noAnswer.Hint, "Not \"make the call again\": a new call is a new purchase.");
                List<string> keys = PurchaseKeys(transport);
                Assert.AreEqual(4, keys.Count, "A dropped connection is sent again under this policy.");
                Assert.AreEqual(1, new HashSet<string>(keys).Count, "Every attempt carries the same key.");

                transport.GoOnline();
                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(4, transport.CountTo(FlockEndpoints.ShopTransaction), "Coming back online sends no purchase: it was never queued.");
            }
        }

        // ---- Contrast for the two offline tests ----
        // Under the same policy a dropped connection IS retried for a call that may be re-sent (a catalog read), so the single
        // attempt the offline consume makes is its money rule at work, not retries being off.
        [Test]
        public void CatalogRead_DroppedConnection_IsRetried_UnderTheSamePolicy()
        {
            FlockFakeTransport transport = WithItem();
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.SetReachable(true);
                transport.GoOffline();

                try { h.Run(() => h.Client.Shop.GetItemAsync("item-1")); }
                catch (FlockException) { }
                Assert.Greater(transport.CountTo(FlockEndpoints.ShopItemById("item-1")), 1,
                    "A read may be re-sent, so a dropped connection is retried under this policy.");
            }
        }

        // ---- Offline, a consume is tried once and never queued ----
        [Test]
        public void Consume_Offline_IsTriedOnce_AndNeverQueued()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerInventoryConsume("inv-1"), FlockFakeTransport.Ok("{\"inventory\":{\"id\":\"inv-1\",\"status\":\"used\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                transport.GoOffline();
                h.SetReachable(false);

                Assert.Throws<FlockNetworkException>(() => h.Run(() => h.Client.Shop.ConsumeAsync("inv-1")));
                Assert.AreEqual(1, transport.CountTo("consume"), "Offline, the consume is tried once and not re-sent.");

                transport.GoOnline();
                h.SetReachable(true);
                h.Run(() => h.Client.Commands.FlushPendingWritesAsync());
                Assert.AreEqual(1, transport.CountTo("consume"), "Coming back online sends no consume: it was never queued.");
            }
        }

        // ---- Real-money items: the server's flag and each store's product id, on every catalog read ----
        // Bodies as the backend answered them on 2026-10-26 (main 644bf45) for the payment probe's shop: two items priced in
        // USD, a real currency there, and two in Gold. shop/{id} and shop/by-name answer the shop at the root, the shop list
        // pages it, and shop_item/shop/{id} wraps the items in {"result":...}.
        private const string ProbeShopId = "01M5EM6PVCVP955VDV3FJ8N3BH";
        private const string GemsPackId = "01M5EM6PVZN764Q4NWT3PRGM3T";

        private const string SwordItem =
            "{\"id\":\"01M5EM6PVZ7ZMR42VRKY3HPHYD\",\"name\":\"pv00 sword\",\"status\":\"active\",\"type\":\"standard\",\"shop_id\":\"01M5EM6PVCVP955VDV3FJ8N3BH\"," +
            "\"patch_id\":null,\"price\":10,\"currency\":\"Gold\",\"is_real_money\":false,\"rewards\":[],\"store_skus\":{\"apple_app_store\":\"com.flock.pv00.sword\"}," +
            "\"data\":{},\"created_at\":\"2026-10-21T07:00:57.855275Z\",\"updated_at\":\"2026-10-21T07:00:57.855276Z\"}";
        private const string ShieldItem =
            "{\"id\":\"01M5EM6PVZPG7EWSZ49TXARBYV\",\"name\":\"pv00 shield\",\"status\":\"active\",\"type\":\"standard\",\"shop_id\":\"01M5EM6PVCVP955VDV3FJ8N3BH\"," +
            "\"patch_id\":null,\"price\":10,\"currency\":\"Gold\",\"is_real_money\":false,\"rewards\":[],\"store_skus\":{},\"data\":{}," +
            "\"created_at\":\"2026-10-21T07:00:57.855309Z\",\"updated_at\":\"2026-10-21T07:00:57.855311Z\"}";
        private const string GemsPackItem =
            "{\"id\":\"01M5EM6PVZN764Q4NWT3PRGM3T\",\"name\":\"pv00 gems pack\",\"status\":\"active\",\"type\":\"currency_pack\",\"shop_id\":\"01M5EM6PVCVP955VDV3FJ8N3BH\"," +
            "\"patch_id\":null,\"price\":199,\"currency\":\"USD\",\"is_real_money\":true,\"rewards\":[{\"type\":\"currency\",\"code\":\"Gold\",\"amount\":100}]," +
            "\"store_skus\":{\"google_play\":\"com.flock.pv00.gems\",\"apple_app_store\":\"com.flock.pv00.gems\"},\"data\":{}," +
            "\"created_at\":\"2026-10-21T07:00:57.855168Z\",\"updated_at\":\"2026-10-21T07:00:57.855173Z\"}";
        private const string GemsPackWithoutProductIdsItem =
            "{\"id\":\"01M5EM6PVZJT2C0K06M61RZFGB\",\"name\":\"pv00 gems pack without product ids\",\"status\":\"active\",\"type\":\"currency_pack\"," +
            "\"shop_id\":\"01M5EM6PVCVP955VDV3FJ8N3BH\",\"patch_id\":null,\"price\":199,\"currency\":\"USD\",\"is_real_money\":true," +
            "\"rewards\":[{\"type\":\"currency\",\"code\":\"Gold\",\"amount\":100}],\"store_skus\":{},\"data\":{}," +
            "\"created_at\":\"2026-10-21T07:00:57.855245Z\",\"updated_at\":\"2026-10-21T07:00:57.855247Z\"}";
        private const string ProbeShopItems = "[" + SwordItem + "," + ShieldItem + "," + GemsPackItem + "," + GemsPackWithoutProductIdsItem + "]";
        private const string ProbeShop =
            "{\"id\":\"01M5EM6PVCVP955VDV3FJ8N3BH\",\"name\":\"pv00-shop\",\"status\":\"active\",\"game_id\":\"01M5EM6PTHKSYB8ZFPSSRAJ7C2\"," +
            "\"game_version_id\":\"01M5EM6PTY4M5PA1A93GQXQYDP\",\"data\":{\"stats\":null,\"web_shop_url\":null,\"pwa_shop_url\":null},\"shop_items\":" + ProbeShopItems + "," +
            "\"created_at\":\"2026-10-21T07:00:57.836159Z\",\"updated_at\":\"2026-10-21T07:00:57.836163Z\"}";

        // The same item from a server without the real-money flag (main aa402fc, the payment probe of 2026-10-21).
        private const string GemsPackFromAServerWithoutTheFlag =
            "{\"id\":\"01M5EM6PVZN764Q4NWT3PRGM3T\",\"name\":\"pv00 gems pack\",\"status\":\"active\",\"type\":\"currency_pack\",\"shop_id\":\"01M5EM6PVCVP955VDV3FJ8N3BH\"," +
            "\"patch_id\":null,\"price\":199,\"currency\":\"USD\",\"rewards\":[{\"type\":\"currency\",\"code\":\"Gold\",\"amount\":100}]," +
            "\"store_skus\":{\"google_play\":\"com.flock.pv00.gems\",\"apple_app_store\":\"com.flock.pv00.gems\"},\"data\":{}," +
            "\"created_at\":\"2026-10-21T07:00:57.855168Z\",\"updated_at\":\"2026-10-21T07:00:57.855173Z\"}";

        private static ShopItem Named(List<ShopItem> items, string name)
        {
            ShopItem found = items.Find(item => item.Name == name);
            Assert.IsNotNull(found, $"No item named '{name}'.");
            return found;
        }

        private static void AssertProbeShopItems(List<ShopItem> items)
        {
            Assert.AreEqual(4, items.Count);

            ShopItem pack = Named(items, "pv00 gems pack");
            Assert.IsTrue(pack.IsRealMoney, "Priced in USD, which the game's currency config lists as real money.");
            Assert.AreEqual(199, pack.Price, "A real-money price is in the currency's smallest unit.");
            Assert.AreEqual("com.flock.pv00.gems", pack.StoreProductIds[PaymentProviders.GooglePlay]);
            Assert.AreEqual("com.flock.pv00.gems", pack.StoreProductIds[PaymentProviders.AppleAppStore]);

            ShopItem packWithoutIds = Named(items, "pv00 gems pack without product ids");
            Assert.IsTrue(packWithoutIds.IsRealMoney);
            Assert.IsNotNull(packWithoutIds.StoreProductIds, "No product id reads as an empty map, never null.");
            Assert.AreEqual(0, packWithoutIds.StoreProductIds.Count);

            ShopItem sword = Named(items, "pv00 sword");
            Assert.IsFalse(sword.IsRealMoney, "Priced in Gold, a game currency.");
            CollectionAssert.AreEquivalent(new[] { PaymentProviders.AppleAppStore }, sword.StoreProductIds.Keys);

            ShopItem shield = Named(items, "pv00 shield");
            Assert.IsFalse(shield.IsRealMoney);
            Assert.AreEqual(0, shield.StoreProductIds.Count);
        }

        [Test]
        public void GetByName_ReadsEachItemsRealMoneyFlagAndStoreProductIds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopByName("pv00-shop"), FlockFakeTransport.Ok(ProbeShop));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Shop shop = h.Run(() => h.Client.Shop.GetByNameAsync("pv00-shop"));

                AssertProbeShopItems(shop.ShopItems);
            }
        }

        [Test]
        public void GetById_ReadsEachItemsRealMoneyFlagAndStoreProductIds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopById(ProbeShopId), FlockFakeTransport.Ok(ProbeShop));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Shop shop = h.Run(() => h.Client.Shop.GetByIdAsync(ProbeShopId));

                AssertProbeShopItems(shop.ShopItems);
            }
        }

        [Test]
        public void GetAll_ReadsEachItemsRealMoneyFlagAndStoreProductIds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Shop, FlockFakeTransport.Ok("{\"items\":[" + ProbeShop + "],\"total\":1,\"page\":1,\"limit\":100}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                PaginatedResponse<Shop> shops = h.Run(() => h.Client.Shop.GetAllAsync());

                AssertProbeShopItems(shops.Items[0].ShopItems);
            }
        }

        [Test]
        public void GetItemsByShop_ReadsEachItemsRealMoneyFlagAndStoreProductIds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemsByShop(ProbeShopId), FlockFakeTransport.Ok(
                "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":" + ProbeShopItems + "}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                List<ShopItem> items = h.Run(() => h.Client.Shop.GetItemsByShopAsync(ProbeShopId));

                AssertProbeShopItems(items);
            }
        }

        [Test]
        public void GetItem_RealMoneyItem_ReadsItsFlagAndStoreProductIds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById(GemsPackId), FlockFakeTransport.Ok(GemsPackItem));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync(GemsPackId));

                Assert.IsTrue(item.IsRealMoney);
                Assert.AreEqual(ShopItemTypes.CurrencyPack, item.Type);
                Assert.AreEqual(2, item.StoreProductIds.Count);
                Assert.AreEqual("com.flock.pv00.gems", item.StoreProductIds[PaymentProviders.GooglePlay]);
            }
        }

        // The flag is the server's word: an older server leaves it out, and the SDK never guesses it from the currency.
        [Test]
        public void GetItem_FromAServerThatDoesNotSayRealMoney_ReadsFalse_AndKeepsItsProductIds()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById(GemsPackId), FlockFakeTransport.Ok(GemsPackFromAServerWithoutTheFlag));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync(GemsPackId));

                Assert.IsFalse(item.IsRealMoney, "No flag reads false, even for an item priced in USD.");
                Assert.AreEqual("com.flock.pv00.gems", item.StoreProductIds[PaymentProviders.AppleAppStore]);
            }
        }

        [Test]
        public void GetItem_NullStoreProductIds_ReadsAnEmptyMap()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"), FlockFakeTransport.Ok(
                "{\"id\":\"item-1\",\"price\":10,\"currency\":\"Gold\",\"is_real_money\":false,\"store_skus\":null}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync("item-1"));

                Assert.IsNotNull(item.StoreProductIds, "A null map reads as empty, so callers never null-check it.");
                Assert.AreEqual(0, item.StoreProductIds.Count);
            }
        }

        [Test]
        public void GetItem_NullRealMoneyFlag_ReadsFalse_WithoutFailingTheRead()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"), FlockFakeTransport.Ok(
                "{\"id\":\"item-1\",\"price\":10,\"currency\":\"Gold\",\"is_real_money\":null,\"store_skus\":{}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync("item-1"));

                Assert.AreEqual("item-1", item.Id);
                Assert.IsFalse(item.IsRealMoney);
            }
        }

        // The server accepts only the stores it checks receipts against today; one it adds later must still read.
        [Test]
        public void GetItem_StoreProductIds_KeepAStoreThisSdkDoesNotKnow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"), FlockFakeTransport.Ok(
                "{\"id\":\"item-1\",\"price\":199,\"currency\":\"USD\",\"is_real_money\":true,\"store_skus\":{\"google_play\":\"com.flock.gems\",\"steam\":\"480\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync("item-1"));

                Assert.AreEqual("480", item.StoreProductIds["steam"]);
                Assert.AreEqual("com.flock.gems", item.StoreProductIds[PaymentProviders.GooglePlay]);
            }
        }

        // A store compares product ids exactly, so the SDK keeps each as the dashboard saved it.
        [Test]
        public void GetItem_StoreProductIds_KeepEachIdVerbatim()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"), FlockFakeTransport.Ok(
                "{\"id\":\"item-1\",\"price\":199,\"currency\":\"USD\",\"is_real_money\":true,\"store_skus\":{\"apple_app_store\":\"com.Flock.Gems_Pack.V2\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync("item-1"));

                Assert.AreEqual("com.Flock.Gems_Pack.V2", item.StoreProductIds[PaymentProviders.AppleAppStore]);
            }
        }

        // A relaunch with no network serves the catalog from the snapshot the last online read wrote, flag and ids included.
        [Test]
        public void RealMoneyItem_ReadOfflineFromItsSnapshot_KeepsItsFlagAndStoreProductIds()
        {
            string relaunchFolder = Path.Combine(Path.GetTempPath(), "flock_test_" + Guid.NewGuid().ToString("N"));
            FlockFakeTransport online = new FlockFakeTransport();
            online.On(FlockEndpoints.ShopItemById(GemsPackId), FlockFakeTransport.Ok(GemsPackItem));
            using (FlockTestClient h = FlockTestClient.Create(online))
            {
                h.SetReachable(true);
                h.Run(() => h.Client.Shop.GetItemAsync(GemsPackId));
                CopyFolder(h.SnapshotDirectory, relaunchFolder);
            }

            FlockFakeTransport offline = new FlockFakeTransport();
            offline.GoOffline();
            using (FlockTestClient h = FlockTestClient.Create(offline, config => config.OfflineCacheDirectory = relaunchFolder))
            {
                h.SetReachable(false);

                ShopItem item = h.Run(() => h.Client.Shop.GetItemAsync(GemsPackId));

                Assert.AreEqual(0, offline.CountTo(FlockEndpoints.ShopItemById(GemsPackId)), "Served from the snapshot, not the network.");
                Assert.IsTrue(item.IsRealMoney);
                Assert.AreEqual("com.flock.pv00.gems", item.StoreProductIds[PaymentProviders.GooglePlay]);
                Assert.AreEqual("com.flock.pv00.gems", item.StoreProductIds[PaymentProviders.AppleAppStore]);
            }
        }

        private static void CopyFolder(string from, string to)
        {
            foreach (string folder in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(folder.Replace(from, to));
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to), true);
        }

        // The names the backend's ShopItemType and PaymentProvider use (read 2026-10-26); the body-read tests above prove
        // the two stores and two kinds a live answer carried.
        [Test]
        public void ShopItemTypes_AndPaymentProviders_AreTheServersNames()
        {
            Assert.AreEqual("standard", ShopItemTypes.Standard);
            Assert.AreEqual("currency_pack", ShopItemTypes.CurrencyPack);
            Assert.AreEqual("consumable", ShopItemTypes.Consumable);
            Assert.AreEqual("apple_app_store", PaymentProviders.AppleAppStore);
            Assert.AreEqual("google_play", PaymentProviders.GooglePlay);
            Assert.AreEqual("stripe", PaymentProviders.Stripe);
            Assert.AreEqual("tap", PaymentProviders.Tap);
            Assert.AreEqual("moyasar", PaymentProviders.Moyasar);
        }

        // ---- Redeeming a store's purchase (shop/validate-receipt) ----
        // The answer is the purchase at the root, as the wallet purchase's is; a delivered store purchase reads paid.
        private const string RedeemedPack =
            "{\"purchase_id\":\"pur-9\",\"item_type\":\"currency_pack\",\"status\":\"paid\",\"inventory\":null," +
            "\"granted\":[{\"type\":\"currency\",\"code\":\"gold\",\"amount\":100}],\"wallet\":";

        // Refusals as the backend sent them to the payment probe on 2026-10-26 (main 644bf45); the last three are the
        // backend's own messages for causes no probe can reach without a real store purchase.
        private static readonly object[] ReceiptRefusals =
        {
            new object[] { 422, "{\"detail\":{\"code\":\"payment_provider.store_credentials_rejected\",\"message\":\"Apple rejected this game's store credentials (HTTP 401); the studio's payment settings need fixing before receipts can be verified\"}}", FlockErrorCode.PaymentProviderStoreCredentialsRejected },
            new object[] { 422, "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: no transaction_id supplied\"}}", FlockErrorCode.PaymentProviderReceiptInvalid },
            new object[] { 422, "{\"detail\":{\"code\":\"payment_provider.receipt_product_mismatch\",\"message\":\"This item has no apple_app_store product id configured\"}}", FlockErrorCode.PaymentProviderReceiptProductMismatch },
            new object[] { 404, "{\"detail\":{\"code\":\"payment_provider.provider_not_found\",\"message\":\"Provider 'apple_app_store' is not configured for this game\"}}", FlockErrorCode.PaymentProviderProviderNotFound },
            new object[] { 404, "{\"detail\":{\"code\":\"shop.item_not_found\",\"message\":\"Shop item not found\"}}", FlockErrorCode.ShopItemNotFound },
            new object[] { 422, "{\"detail\":{\"code\":\"payment_provider.receipt_pending\",\"message\":\"Google reports this payment as still in progress; ask again later\"}}", FlockErrorCode.PaymentProviderReceiptPending },
            new object[] { 403, "{\"detail\":{\"code\":\"payment_provider.receipt_belongs_to_another_player\",\"message\":\"Receipt refused: this store transaction was redeemed by another player\"}}", FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer },
            new object[] { 422, "{\"detail\":{\"code\":\"payment_provider.receipt_product_mismatch\",\"message\":\"Receipt is for 'com.flock.gems.small', but this item sells 'com.flock.gems'\"}}", FlockErrorCode.PaymentProviderReceiptProductMismatch },
        };

        private const string StoreUnavailable =
            "{\"detail\":{\"code\":\"payment_provider.store_unavailable\",\"message\":\"apple_app_store could not be asked about this receipt ([Errno 111] Connection refused); try again later\"}}";
        private const string UnreadableKeys =
            "{\"detail\":{\"code\":\"payment_provider.credentials_unreadable\",\"message\":\"Apple credentials could not be used to sign a request\"}}";

        [Test]
        public void Redeem_AppStore_SendsItsTransactionId_AsTheSignedInPlayer()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Ok(RedeemedPack + "null}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999")));

                FlockHttpRequest sent = transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0];
                Assert.AreEqual("POST", sent.Method);
                Assert.AreEqual("{\"shop_item_id\":\"item-1\",\"provider\":\"apple_app_store\",\"receipt\":{\"transaction_id\":\"2000000999999999\"}}", sent.JsonBody,
                    "No player_id: the server redeems for the player the sign-in names.");
                Assert.IsTrue(sent.Headers.ContainsKey("Authorization"), "Sent with the player's sign-in.");
            }
        }

        [Test]
        public void Redeem_GooglePlay_SendsItsProductIdAndPurchaseToken()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Ok(RedeemedPack + "null}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromGooglePlay("com.flock.gems", "token-abc.AO-J1Oz")));

                Assert.AreEqual("{\"shop_item_id\":\"item-1\",\"provider\":\"google_play\",\"receipt\":{\"product_id\":\"com.flock.gems\",\"purchase_token\":\"token-abc.AO-J1Oz\"}}",
                    transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody);
            }
        }

        [Test]
        public void Redeem_Delivered_ReturnsWhatItGranted_AndWritesTheWallet()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Ok(RedeemedPack + WalletJson("600") + "}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                PrimeCachedWallet(h, "500");

                PurchaseResult result = h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999")));

                Assert.AreEqual("pur-9", result.PurchaseId);
                Assert.IsTrue(result.IsPaid);
                Assert.AreEqual(100, result.Granted[0].Amount);
                Assert.AreEqual("600", CachedGold(h), "The cached wallet reads the balance the redemption reported.");
            }
        }

        // The server answers a repeat with what the first delivered, so the SDK keeps no answer of its own to hand back.
        [Test]
        public void Redeem_SentAgain_AsksTheServerAgain()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.OnSequence(FlockEndpoints.ShopValidateReceipt,
                FlockFakeTransport.Ok(RedeemedPack + "null}"),
                FlockFakeTransport.Ok("{\"purchase_id\":\"pur-9\",\"item_type\":\"currency_pack\",\"status\":\"refunded\",\"granted\":[{\"type\":\"currency\",\"code\":\"gold\",\"amount\":100}]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                StoreReceipt receipt = StoreReceipt.FromAppleAppStore("2000000999999999");

                h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", receipt));
                PurchaseResult again = h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", receipt));

                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                Assert.AreEqual(PurchaseResultStatuses.Refunded, again.Status, "The repeat reads the purchase as it is now.");
                Assert.AreEqual(100, again.Granted[0].Amount, "A repeat carries what the first redemption granted.");
            }
        }

        [Test]
        public void Redeem_NotSignedIn_ThrowsBeforeSending()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                FlockAuthException ex = Assert.Throws<FlockAuthException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                Assert.AreEqual(FlockErrorHints.SignInFirst, ex.Hint);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        private static readonly object[] ReceiptsMissingAnId =
        {
            new object[] { null },
            new object[] { StoreReceipt.FromAppleAppStore("") },
            new object[] { StoreReceipt.FromAppleAppStore(null) },
            new object[] { StoreReceipt.FromGooglePlay("", "token-abc") },
            new object[] { StoreReceipt.FromGooglePlay("com.flock.gems", null) },
        };

        [TestCaseSource(nameof(ReceiptsMissingAnId))]
        public void Redeem_WithoutAnId_ThrowsBeforeSending(StoreReceipt receipt)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockValidationException ex = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", receipt)));

                Assert.IsNotEmpty(ex.Hint);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        // A store's id with spaces around it was mangled where it was copied; trimming it would hide that, so it is refused.
        [TestCase(" 2000000999999999")]
        [TestCase("2000000999999999\n")]
        public void Redeem_AnIdWithSpacesAroundIt_IsRefused_NotTrimmed(string transactionId)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Throws<FlockValidationException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore(transactionId))));
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        [TestCaseSource(nameof(ReceiptRefusals))]
        public void Redeem_Refusals_ParseToTheirCodes_AndAreNotSentAgain(int status, string body, FlockErrorCode expected)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Status(status, body));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockException ex = Assert.Catch<FlockException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                Assert.AreEqual(expected, ex.ErrorCode);
                Assert.IsNotEmpty(ex.Hint);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "A refusal is the server's final word on this receipt for now.");
            }
        }

        [Test]
        public void Redeem_StoreUnavailable_IsSentAgain_ThenThrowsWithItsCode()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Status(503, StoreUnavailable));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockException ex = Assert.Catch<FlockException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                Assert.AreEqual(FlockErrorCode.PaymentProviderStoreUnavailable, ex.ErrorCode);
                Assert.AreEqual(4, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "Sent again under the retry policy: the first try and three retries.");
            }
        }

        // Keys the server cannot read stay unreadable until the studio enters them again, so the same receipt is not sent again.
        [Test]
        public void Redeem_UnreadableKeys_AreNotSentAgain()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Status(503, UnreadableKeys));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockException ex = Assert.Catch<FlockException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                Assert.AreEqual(FlockErrorCode.PaymentProviderCredentialsUnreadable, ex.ErrorCode);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        // Unlike a wallet purchase, a redemption may be sent again after an answer that never arrived (payment plan D-P3).
        [Test]
        public void Redeem_DroppedConnection_IsSentAgain()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                transport.GoOffline();

                Assert.Catch<FlockException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                Assert.AreEqual(4, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        // A retry goes out as the player who redeemed or not at all: another player's sign-in never redeems the first player's receipt.
        [Test]
        public void Redeem_PlayerChangesWhileItIsSentAgain_IsNotSentAsTheNextPlayer()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                transport.Default(request =>
                {
                    h.LoginAs("player-b");
                    return FlockFakeTransport.Status(503, StoreUnavailable);
                });

                Assert.Catch<OperationCanceledException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "The retry would have gone out as player-b.");
            }
        }

        // A 2xx that names no purchase says nothing about one; PurchaseResult's default status would read it as paid.
        [TestCase("{}")]
        [TestCase("{\"item_type\":\"currency_pack\",\"granted\":[]}")]
        public void Redeem_AnswerNamingNoPurchase_Fails(string body)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Ok(body));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockSerializationException ex = Assert.Throws<FlockSerializationException>(() =>
                    h.Run(() => h.Client.Shop.RedeemStoreReceiptAsync("item-1", StoreReceipt.FromAppleAppStore("2000000999999999"))));

                StringAssert.Contains("send the same receipt again", ex.Hint);
            }
        }

        // ---- The store account token (shop/account-token): read once a sign-in, never handed to the next player ----
        private const string TokenAnswer = "{\"player_id\":\"player-a\",\"account_token\":\"c1601ba6-6805-5c7b-92d0-40ccdbedd6eb\"}";

        [Test]
        public void StoreAccountToken_IsReadOnceASignIn_AndAgainForTheNextPlayer()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok(TokenAnswer));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.AreEqual("c1601ba6-6805-5c7b-92d0-40ccdbedd6eb", h.Run(() => h.Client.Shop.GetStoreAccountTokenAsync()));
                h.Run(() => h.Client.Shop.GetStoreAccountTokenAsync());
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopAccountToken), "Stable per player, so read once a sign-in.");
                Assert.IsTrue(transport.AllTo(FlockEndpoints.ShopAccountToken)[0].Headers.ContainsKey("Authorization"));

                h.LoginAs("player-b");
                h.Run(() => h.Client.Shop.GetStoreAccountTokenAsync());
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopAccountToken), "The next player's is read for them.");
            }
        }

        [Test]
        public void StoreAccountToken_NotSignedIn_ThrowsBeforeSending()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                FlockAuthException ex = Assert.Throws<FlockAuthException>(() => h.Run(() => h.Client.Shop.GetStoreAccountTokenAsync()));

                Assert.AreEqual(FlockErrorHints.SignInFirst, ex.Hint);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopAccountToken));
            }
        }

        [Test]
        public void StoreAccountToken_AnswerWithoutOne_Fails()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok("{\"player_id\":\"player-a\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Throws<FlockSerializationException>(() => h.Run(() => h.Client.Shop.GetStoreAccountTokenAsync()));
            }
        }

        [Test]
        public void Purchase_AnswerNamingNoPurchase_Fails()
        {
            FlockFakeTransport transport = WithItem();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                FlockSerializationException ex = Assert.Throws<FlockSerializationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));

                StringAssert.Contains("inventory and wallet", ex.Hint);
            }
        }

        // An inventory entry as the inventory route answers with it, on a page at the root.
        private static string InventoryEntry(string id, string shopItemId, string status)
            => "{\"id\":\"" + id + "\",\"player_id\":\"player-a\",\"shop_item_id\":\"" + shopItemId + "\",\"status\":\"" + status + "\",\"created_at\":\"2026-10-27T00:00:00Z\"}";

        private static string InventoryPage(int page, int total, List<string> entries)
            => "{\"items\":[" + string.Join(",", entries) + "],\"total\":" + total + ",\"page\":" + page + ",\"limit\":100}";

        // A full first page, then a second: 99 potions and a used sword, then an unused sword and a refunded shield.
        private static FlockFakeTransport WithTwoInventoryPages()
        {
            List<string> first = new List<string>();
            for (int i = 0; i < 99; i++)
                first.Add(InventoryEntry("inv-p" + i, "potion", "unused"));
            first.Add(InventoryEntry("inv-s1", "sword", "used"));
            List<string> second = new List<string> { InventoryEntry("inv-s2", "sword", "unused"), InventoryEntry("inv-sh", "shield", "revoked") };
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerInventoryByPlayer("player-a") + "?page=1&limit=100", FlockFakeTransport.Ok(InventoryPage(1, 102, first)));
            transport.On(FlockEndpoints.PlayerInventoryByPlayer("player-a") + "?page=2&limit=100", FlockFakeTransport.Ok(InventoryPage(2, 102, second)));
            return transport;
        }

        [Test]
        public void OwnedItemCounts_ReadEveryPage_AndCountOnlyUnusedEntries()
        {
            FlockFakeTransport transport = WithTwoInventoryPages();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                IReadOnlyDictionary<string, int> owned = h.Run(() => h.Client.Shop.GetOwnedItemCountsAsync());

                Assert.AreEqual(99, owned["potion"]);
                Assert.AreEqual(1, owned["sword"], "The used sword does not count; the one on the second page does.");
                Assert.IsFalse(owned.ContainsKey("shield"), "A refunded entry is not owned.");
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.PlayerInventoryByPlayer("player-a")), "Both pages were read, and no more.");
            }
        }

        [Test]
        public void Owns_IsTrueOnlyForAnItemWithAnUnusedEntry()
        {
            using (FlockTestClient h = FlockTestClient.Create(WithTwoInventoryPages()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.IsTrue(h.Run(() => h.Client.Shop.OwnsAsync("sword")));
                Assert.IsFalse(h.Run(() => h.Client.Shop.OwnsAsync("shield")), "Refunded.");
                Assert.IsFalse(h.Run(() => h.Client.Shop.OwnsAsync("never-bought")));
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.OwnsAsync("")));
            }
        }

        [Test]
        public void OwnedItemCounts_EmptyInventory_IsEmpty_AfterOneRead()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerInventoryByPlayer("player-a"), FlockFakeTransport.Ok(InventoryPage(1, 0, new List<string>())));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.AreEqual(0, h.Run(() => h.Client.Shop.GetOwnedItemCountsAsync()).Count);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.PlayerInventoryByPlayer("player-a")));
            }
        }

        [Test]
        public void OwnedItemCounts_NotSignedIn_ThrowsBeforeSending()
        {
            FlockFakeTransport transport = WithTwoInventoryPages();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.GetOwnedItemCountsAsync()));
                Assert.AreEqual(0, transport.Requests.Count);
            }
        }
    }

    // Live: the catalog as the SDK reads it from the real backend, against the route's own answer. Manual only: needs FlockConfig.asset and the backend.
    public class FlockShopLiveTests
    {
        private const string UnknownItemId = "01ZZZZZZZZZZZZZZZZZZZZZZZZ";
        private const string UnknownPlayerId = "01ZZZZZZZZZZZZZZZZZZZZZZZZ";
        private bool _createdClient;

        [TearDown]
        public void TearDown()
        {
            if (_createdClient)
            {
                FlockClient.Shutdown();
                _createdClient = false;
            }
        }

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually from the Test Runner.")]
        public IEnumerator Live_ShopItems_ReadTheServersRealMoneyFlagAndStoreProductIds()
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

            string url = $"{FlockClient.Instance.GetVersionedApiUrl()}/{FlockEndpoints.Shop}?page=1&limit=100";
            Task<JObject> raw = FlockHttpClient.GetAsync<JObject>(url, FlockClient.Instance.GetGameHeaders(), CancellationToken.None);
            Task<PaginatedResponse<Shop>> read = FlockClient.Instance.Shop.GetAllAsync();
            while (!raw.IsCompleted || !read.IsCompleted)
                yield return null;
            if (raw.IsFaulted && (raw.Exception?.GetBaseException() as FlockException)?.StatusCode == null)
            {
                Assert.Ignore($"Live backend unreachable: {raw.Exception?.GetBaseException().Message}");
                yield break;
            }
            Assert.IsFalse(read.IsFaulted, $"The SDK's shop read failed: {read.Exception?.GetBaseException().Message}");

            Dictionary<string, ShopItem> readById = new Dictionary<string, ShopItem>();
            foreach (Shop shop in read.Result.Items)
                foreach (ShopItem item in shop.ShopItems)
                    readById[item.Id] = item;

            int compared = 0;
            foreach (JToken shop in raw.Result["items"])
            {
                foreach (JToken rawItem in shop["shop_items"])
                {
                    ShopItem item = readById[(string)rawItem["id"]];
                    Assert.AreEqual((bool)rawItem["is_real_money"], item.IsRealMoney, $"{item.Name}: the flag as the server sent it.");
                    Dictionary<string, string> sent = rawItem["store_skus"].ToObject<Dictionary<string, string>>();
                    CollectionAssert.AreEquivalent(sent, item.StoreProductIds, $"{item.Name}: each store's product id as the server sent it.");
                    compared++;
                }
            }
            if (compared == 0)
            {
                Assert.Ignore("This game's shops hold no items to compare.");
                yield break;
            }
            Debug.Log($"[Flock] Live shop read: {compared} items match the route's answer.");

            // Counter-case: an item that does not exist is refused on its own code.
            Task<ShopItem> unknown = FlockClient.Instance.Shop.GetItemAsync(UnknownItemId);
            while (!unknown.IsCompleted)
                yield return null;
            FlockException refusal = unknown.Exception?.GetBaseException() as FlockException;
            Assert.IsNotNull(refusal, "An unknown item is refused.");
            Assert.AreEqual(FlockErrorCode.ShopItemShopItemNotFound, refusal.ErrorCode);
        }

        // Redeeming through the real route as a signed-in player: with no store purchase to hand, each answer is a refusal on its own code.
        // A refusal past the sign-in and the body's checks proves the request went out signed in and in the route's shape.
        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually from the Test Runner.")]
        public IEnumerator Live_RedeemStoreReceipt_IsRefusedOnItsOwnCodes()
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

            Task<string> signIn = SignInTheLiveDeviceAsync();
            while (!signIn.IsCompleted)
                yield return null;
            if (signIn.GetAwaiter().GetResult() != null)
            {
                Assert.Ignore(signIn.Result);
                yield break;
            }

            Task<PaginatedResponse<Shop>> shops = FlockClient.Instance.Shop.GetAllAsync();
            while (!shops.IsCompleted)
                yield return null;
            Assert.IsFalse(shops.IsFaulted, $"The shop read failed: {shops.Exception?.GetBaseException().Message}");
            ShopItem item = null;
            foreach (Shop shop in shops.Result.Items)
                foreach (ShopItem candidate in shop.ShopItems)
                    if (item == null && candidate.StoreProductIds.Count == 0)
                        item = candidate;
            if (item == null)
            {
                Assert.Ignore("This game has no shop item without store product ids to redeem against.");
                yield break;
            }

            Task<PurchaseResult> apple = FlockClient.Instance.Shop.RedeemStoreReceiptAsync(item.Id, StoreReceipt.FromAppleAppStore("2000000999999999"));
            while (!apple.IsCompleted)
                yield return null;
            AssertRefusedWith(apple, FlockErrorCode.PaymentProviderReceiptProductMismatch);

            Task<PurchaseResult> google = FlockClient.Instance.Shop.RedeemStoreReceiptAsync(item.Id, StoreReceipt.FromGooglePlay("com.flock.live.none", "live-test-token"));
            while (!google.IsCompleted)
                yield return null;
            AssertRefusedWith(google, FlockErrorCode.PaymentProviderReceiptProductMismatch);

            // Counter-case: an item that does not exist.
            Task<PurchaseResult> unknown = FlockClient.Instance.Shop.RedeemStoreReceiptAsync(UnknownItemId, StoreReceipt.FromAppleAppStore("2000000999999999"));
            while (!unknown.IsCompleted)
                yield return null;
            AssertRefusedWith(unknown, FlockErrorCode.ShopItemNotFound);
            Debug.Log($"[Flock] Live redemption: refused on its own codes for '{item.Name}' and for an unknown item.");
        }

        // The token a store purchase is started with, as the route sends it for the signed-in player.
        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually from the Test Runner.")]
        public IEnumerator Live_StoreAccountToken_IsTheRoutesAnswerForTheSignedInPlayer()
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

            Task<string> signIn = SignInTheLiveDeviceAsync();
            while (!signIn.IsCompleted)
                yield return null;
            if (signIn.GetAwaiter().GetResult() != null)
            {
                Assert.Ignore(signIn.Result);
                yield break;
            }

            string url = $"{FlockClient.Instance.GetVersionedApiUrl()}/{FlockEndpoints.ShopAccountToken}";
            Task<JObject> raw = FlockHttpClient.GetAsync<JObject>(url, FlockClient.Instance.GetBaseHeaders(), CancellationToken.None);
            Task<string> token = FlockClient.Instance.Shop.GetStoreAccountTokenAsync();
            while (!raw.IsCompleted || !token.IsCompleted)
                yield return null;
            Assert.IsFalse(raw.IsFaulted, $"The route's own answer failed: {raw.Exception?.GetBaseException().Message}");
            Assert.IsFalse(token.IsFaulted, $"The SDK's read failed: {token.Exception?.GetBaseException().Message}");
            Assert.AreEqual((string)raw.Result["account_token"], token.Result);
            Assert.IsTrue(Guid.TryParse(token.Result, out Guid _), "A UUID, as Apple's appAccountToken needs.");

            // Counter-case: the route refuses a request that names no player.
            Task<JObject> unsigned = FlockHttpClient.GetAsync<JObject>(url, FlockClient.Instance.GetGameHeaders(), CancellationToken.None);
            while (!unsigned.IsCompleted)
                yield return null;
            AssertRefusedWith(unsigned, FlockErrorCode.PlayerMissingToken);
            Debug.Log("[Flock] Live store account token: the route's answer, a UUID; refused with no player signed in.");
        }

        // Drops the answer to the first wallet purchase, after the real backend has made it, as a lost connection would.
        private sealed class LosesTheFirstPurchaseAnswer : IFlockHttpAdapter
        {
            private readonly IFlockHttpAdapter _real = new SystemNetHttpAdapter(TimeSpan.FromSeconds(30));
            public readonly List<string> PurchaseBodies = new List<string>();

            public async Task<FlockHttpResponse> SendAsync(FlockHttpRequest request, CancellationToken cancellationToken)
            {
                FlockHttpResponse answer = await _real.SendAsync(request, cancellationToken);
                if (!request.Url.Contains(FlockEndpoints.ShopTransaction))
                    return answer;
                PurchaseBodies.Add(request.JsonBody);
                return PurchaseBodies.Count == 1 ? new FlockHttpResponse { Result = FlockHttpResult.ConnectionError, StatusCode = 0 } : answer;
            }
        }

        private static IEnumerator Wait(Task task)
        {
            while (!task.IsCompleted)
                yield return null;
        }

        // The game's wallet items (paid from the wallet, landing in the inventory), cheapest first.
        private static List<ShopItem> WalletItems(PaginatedResponse<Shop> shops)
        {
            List<ShopItem> items = new List<ShopItem>();
            foreach (Shop shop in shops.Items)
                foreach (ShopItem item in shop.ShopItems)
                    if (!item.IsRealMoney && item.Price > 0 && item.Type == ShopItemTypes.Standard)
                        items.Add(item);
            items.Sort((a, b) => a.Price.CompareTo(b.Price));
            return items;
        }

        // A purchase the server made but whose answer never arrived is sent again with its key: one charge, one entry.
        // A device of its own: a wallet made before a currency was added to the template cannot be funded (BACKLOG BE-P30).
        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; adds funds to the live wallet test player and buys an item; run manually.")]
        public IEnumerator Live_WalletPurchase_WhoseAnswerWasLost_IsSentAgain_AndChargedOnce()
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
            Task<string> signIn = SignInTheLiveDeviceAsync(LiveWalletDeviceId);
            yield return Wait(signIn);
            if (signIn.GetAwaiter().GetResult() != null)
            {
                Assert.Ignore(signIn.Result);
                yield break;
            }

            Task<PaginatedResponse<Shop>> shops = FlockClient.Instance.Shop.GetAllAsync();
            yield return Wait(shops);
            List<ShopItem> walletItems = WalletItems(shops.Result);
            if (walletItems.Count < 2)
            {
                Assert.Ignore("This game needs two standard items paid from the wallet.");
                yield break;
            }
            ShopItem item = walletItems[0];
            Task<PlayerData> funded = FlockClient.Instance.Commands.AddGameFundsAsync(item.Currency, item.Price);
            yield return Wait(funded);
            Assert.IsFalse(funded.IsFaulted, $"Adding {item.Price} {item.Currency} failed: {funded.Exception?.GetBaseException().Message}");

            FlockClient.Instance.Player.ClearCache();
            Task<FlockWallet> before = FlockClient.Instance.Player.GetWalletAsync();
            Task<IReadOnlyDictionary<string, int>> ownedBefore = FlockClient.Instance.Shop.GetOwnedItemCountsAsync();
            yield return Wait(Task.WhenAll(before, ownedBefore));
            ownedBefore.Result.TryGetValue(item.Id, out int heldBefore);

            LosesTheFirstPurchaseAnswer lossy = new LosesTheFirstPurchaseAnswer();
            FlockHttpClient.Configure(lossy);
            Task<PurchaseResult> purchase;
            try
            {
                purchase = FlockClient.Instance.Shop.PurchaseAsync(item.Id);
                yield return Wait(purchase);
            }
            finally
            {
                FlockHttpClient.Configure(TimeSpan.FromSeconds(30));
            }
            Assert.IsFalse(purchase.IsFaulted, $"The purchase sent again failed: {purchase.Exception?.GetBaseException().Message}");
            Assert.AreEqual(2, lossy.PurchaseBodies.Count, "The answer was lost once, so the purchase went twice.");
            string key = (string)JObject.Parse(lossy.PurchaseBodies[0])["idempotency_key"];
            Assert.IsNotEmpty(key);
            Assert.AreEqual(key, (string)JObject.Parse(lossy.PurchaseBodies[1])["idempotency_key"]);

            FlockClient.Instance.Player.ClearCache();
            Task<FlockWallet> after = FlockClient.Instance.Player.GetWalletAsync();
            Task<IReadOnlyDictionary<string, int>> ownedAfter = FlockClient.Instance.Shop.GetOwnedItemCountsAsync();
            yield return Wait(Task.WhenAll(after, ownedAfter));
            ownedAfter.Result.TryGetValue(item.Id, out int heldAfter);
            Assert.AreEqual(before.Result.BalanceOf(item.Currency) - item.Price, after.Result.BalanceOf(item.Currency), "Charged once for two sends.");
            Assert.AreEqual(heldBefore + 1, heldAfter, "One inventory entry for two sends.");

            // Counter-case: the key sent again for another item is refused on its own code, and nothing more is bought.
            ShopItem other = walletItems[1];
            string url = $"{FlockClient.Instance.GetVersionedApiUrl()}/{FlockEndpoints.ShopTransaction}";
            Task<JObject> reused = FlockHttpClient.PostAsync<JObject>(url, new ShopTransactionRequest { ShopItemId = other.Id, IdempotencyKey = key },
                FlockClient.Instance.GetBaseHeaders(), CancellationToken.None);
            yield return Wait(reused);
            AssertRefusedWith(reused, FlockErrorCode.ShopIdempotencyKeyReused);
            Debug.Log($"[Flock] Live keyed purchase: '{item.Name}' sent twice under one key, charged {item.Price} {item.Currency} once; the key for '{other.Name}' refused.");
        }

        [UnityTest, Explicit("Hits the live Flock backend using FlockConfig.asset; run manually from the Test Runner.")]
        public IEnumerator Live_WalletAndOwnedCounts_AreTheRoutesAnswers()
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
            Task<string> signIn = SignInTheLiveDeviceAsync();
            yield return Wait(signIn);
            if (signIn.GetAwaiter().GetResult() != null)
            {
                Assert.Ignore(signIn.Result);
                yield break;
            }
            string me = FlockClient.Instance.CurrentPlayerId;

            FlockClient.Instance.Player.ClearCache();
            Task<FlockWallet> wallet = FlockClient.Instance.Player.GetWalletAsync();
            Task<IReadOnlyDictionary<string, int>> owned = FlockClient.Instance.Shop.GetOwnedItemCountsAsync();
            string api = FlockClient.Instance.GetVersionedApiUrl();
            Task<JObject> rows = FlockHttpClient.GetAsync<JObject>($"{api}/{FlockEndpoints.PlayerData}?page=1&limit=100&player_id={me}", FlockClient.Instance.GetBaseHeaders(), CancellationToken.None);
            Task<JObject> templates = FlockHttpClient.GetAsync<JObject>($"{api}/{FlockEndpoints.PlayerTemplate}", FlockClient.Instance.GetGameHeaders(), CancellationToken.None);
            Task<JObject> inventory = FlockHttpClient.GetAsync<JObject>($"{api}/{FlockEndpoints.PlayerInventoryByPlayer(me)}?page=1&limit=100", FlockClient.Instance.GetBaseHeaders(), CancellationToken.None);
            yield return Wait(Task.WhenAll(wallet, owned, rows, templates, inventory));
            Assert.IsFalse(wallet.IsFaulted, $"The SDK's wallet read failed: {wallet.Exception?.GetBaseException().Message}");

            string walletTemplate = null;
            foreach (JToken template in templates.Result["result"])
                if (string.Equals((string)template["tag"], "currency", StringComparison.OrdinalIgnoreCase))
                    walletTemplate = (string)template["id"];
            Dictionary<string, long> sent = new Dictionary<string, long>();
            foreach (JToken row in rows.Result["items"])
                if ((string)row["player_template_id"] == walletTemplate)
                    foreach (JToken category in row["data"])
                        if ((string)category["field_name"] == "game_currencies")
                            foreach (JToken currency in category["value"])
                                sent[(string)currency["field_name"]] = (long)currency["value"];
            Assert.Greater(sent.Count, 0, "Control: the route's wallet row names game currencies.");
            CollectionAssert.AreEquivalent(sent, wallet.Result.GameCurrencies, "Each balance as the route sent it.");

            Assert.LessOrEqual((int)inventory.Result["total"], 100, "Control: one page of the route holds the whole inventory compared here.");
            Dictionary<string, int> unused = new Dictionary<string, int>();
            foreach (JToken entry in inventory.Result["items"])
                if ((string)entry["status"] == PlayerInventoryStatuses.Unused)
                    unused[(string)entry["shop_item_id"]] = unused.TryGetValue((string)entry["shop_item_id"], out int n) ? n + 1 : 1;
            CollectionAssert.AreEquivalent(unused, owned.Result, "Unused entries by item, as the route listed them.");

            // Counter-case: the inventory of a player that does not exist is refused on its own code.
            Task<PaginatedResponse<PlayerInventory>> nobody = FlockClient.Instance.Shop.GetPlayerInventoryAsync(UnknownPlayerId);
            yield return Wait(nobody);
            AssertRefusedWith(nobody, FlockErrorCode.PlayerInventoryPlayerNotFound);
            Debug.Log($"[Flock] Live wallet: {sent.Count} game currencies match the route; {unused.Count} owned items match the inventory.");
        }

        private const string LiveDeviceId = "flock-sdk-live-shop";
        private const string LiveWalletDeviceId = "flock-sdk-live-wallet";

        // Signs a live test device in, registering it on its first run; answers why the test is skipped, or null once signed in.
        private static async Task<string> SignInTheLiveDeviceAsync(string deviceId = LiveDeviceId)
        {
            try
            {
                await FlockClient.Instance.Authentication.LoginWithDeviceAsync(deviceId);
                return null;
            }
            catch (FlockException ex) when (ex.StatusCode == null)
            {
                return $"Live backend unreachable: {ex.Message}";
            }
            catch (FlockException)
            {
                await FlockClient.Instance.Authentication.RegisterWithDeviceAsync(deviceId);
                return null;
            }
        }

        private static void AssertRefusedWith(Task task, FlockErrorCode expected)
        {
            FlockException ex = task.Exception?.GetBaseException() as FlockException;
            Assert.IsNotNull(ex, $"Expected a refusal on {expected}.");
            Assert.AreEqual(expected, ex.ErrorCode, ex.Message);
        }
    }
}
