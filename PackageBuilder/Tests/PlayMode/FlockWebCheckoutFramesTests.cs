using System;
using System.Collections;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Web checkout on the game's real frames: the client sends a checkout's reads due each frame, with nothing sent by hand.
    public class FlockWebCheckoutFramesTests
    {
        private const string Sword =
            "{\"id\":\"sword\",\"name\":\"Sword\",\"type\":\"standard\",\"price\":799,\"currency\":\"SAR\",\"is_real_money\":true,\"store_skus\":{},\"status\":\"active\"}";

        private static string Purchase(string status)
            => "{\"id\":\"pur-1\",\"shop_item_id\":\"sword\",\"item_type\":\"standard\",\"status\":\"" + status + "\",\"price_amount\":799,\"price_currency\":\"SAR\"," +
               "\"settlement_method\":\"external\",\"payment_provider\":\"stripe\",\"granted\":[],\"player_inventory_id\":null,\"created_at\":\"2026-10-31T12:00:00Z\"," +
               "\"settled_at\":null,\"refunded_at\":null,\"expires_at\":\"2026-10-31T13:00:00Z\"}";

        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [UnityTest]
        public IEnumerator ACheckoutIsReadOnTheGamesFrames_UntilPaid()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("sword"), FlockFakeTransport.Ok(Sword));
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok("{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" + Sword + "]}],\"total\":1,\"page\":1,\"limit\":100}"));
            transport.On(FlockEndpoints.ShopCheckout, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-1\",\"provider\":\"stripe\",\"payment_url\":\"https://checkout.stripe.com/c/pay/cs_test_a1\"}"));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok("{\"items\":[],\"total\":0,\"page\":1,\"limit\":100}"));
            transport.OnSequence(FlockEndpoints.ShopPurchaseById("pur-1"), FlockFakeTransport.Ok(Purchase("pending")), FlockFakeTransport.Ok(Purchase("paid")));
            _h = FlockTestClient.Create(transport);
            _h.LoginAs("player-a");
            Task<FlockStore> connecting = FlockStore.ConnectAsync(new FlockStoreOptions { PaymentProvider = PaymentProviders.Stripe, OpenPaymentPage = _ => { }, CheckEvery = TimeSpan.FromMilliseconds(50) });
            yield return FlockTestWait.Until(() => connecting.IsCompleted, "The web checkout connected");
            Assert.IsFalse(connecting.IsFaulted, connecting.Exception?.GetBaseException().ToString());
            FlockStore checkout = connecting.Result;
            Assert.IsTrue(checkout.IsWebCheckout);

            Task<PurchaseResult> buying = _h.Client.Shop.PurchaseAsync("sword");
            yield return FlockTestWait.Until(() => buying.IsCompleted, "Paid, read on the game's frames alone");

            Assert.IsFalse(buying.IsFaulted, buying.Exception?.GetBaseException().ToString());
            Assert.IsTrue(buying.Result.IsPaid);
            Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")));
            checkout.Dispose();
        }
    }
}
