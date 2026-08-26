using System;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // ShopProvider: catalog reads (by-id / by-name), money-safe purchase (non-idempotent, ambiguous failures
    // surface without retry), and never-cached inventory. Purchase re-throws on failure after recording the
    // Failed transaction.
    public class FlockShopProviderTests
    {
        // ---- SHOP-01 ----
        [Test]
        public void GetById_Success_ReturnsShop()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopById("shop-1"), FlockFakeTransport.Ok("{\"result\":{\"id\":\"shop-1\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Shop r = h.Run(() => h.Client.Shop.GetByIdAsync("shop-1"));

                Assert.IsNotNull(r);
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
            transport.Default(request => FlockFakeTransport.Ok("{\"result\":{\"id\":\"shop-x\"}}"));
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

        // ---- SHOP-03: an ambiguous purchase failure (5xx) is NOT retried (money safety) ----
        [Test]
        public void Purchase_ServerError_NotRetried_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"),
                FlockFakeTransport.Ok("{\"result\":{\"id\":\"item-1\",\"price\":100,\"currency\":\"USD\"}}"));
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Status(500, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = RetriesEnabled()))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopTransaction), "Money mutation must not be retried on an ambiguous 5xx.");
            }
        }

        // ---- SHOP-03b: contrast — a 408 is provably NOT processed, so even a purchase retries it ----
        // Without this, SHOP-03 could pass purely because retries were off; this proves the flag is what's measured.
        [Test]
        public void Purchase_RequestTimeout_IsRetried()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"),
                FlockFakeTransport.Ok("{\"result\":{\"id\":\"item-1\",\"price\":100,\"currency\":\"USD\"}}"));
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
    }
}
