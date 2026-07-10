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

        // ---- SHOP-03: an ambiguous purchase failure (5xx) is NOT retried (money safety) ----
        [Test]
        public void Purchase_ServerError_NotRetried_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("item-1"),
                FlockFakeTransport.Ok("{\"result\":{\"id\":\"item-1\",\"price\":100,\"currency\":\"USD\"}}"));
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Status(500, "{}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("item-1")));
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopTransaction), "Money mutation must not be retried on an ambiguous 5xx.");
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
    }
}
