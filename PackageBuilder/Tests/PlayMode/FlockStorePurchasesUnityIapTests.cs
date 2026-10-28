#if FLOCK_UNITY_IAP_5
using System;
using System.Collections;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.Purchasing;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // The adapter on Unity IAP 5's own StoreController and its fake store (the test project builds with IAP_FAKE_STORE_DEFAULT):
    // the store's real order reaches Flock, and only then does the store hear the purchase is finished.
    public class FlockStorePurchasesUnityIapTests
    {
        private const string Catalog =
            "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[{\"id\":\"gems\",\"name\":\"gems\",\"type\":\"currency_pack\",\"price\":199," +
            "\"currency\":\"USD\",\"is_real_money\":true,\"store_skus\":{\"google_play\":\"com.flock.gems\"}}]}],\"total\":1,\"page\":1,\"limit\":100}";

        private FlockTestClient _h;
        private FlockFakeTransport _transport;
        private FlockStorePurchases _purchases;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _transport = new FlockFakeTransport();
            _transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
            _transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok("{\"player_id\":\"player-a\",\"account_token\":\"c1601ba6-6805-5c7b-92d0-40ccdbedd6eb\"}"));
            _transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Ok(
                "{\"purchase_id\":\"pur-1\",\"item_type\":\"currency_pack\",\"status\":\"paid\",\"granted\":[{\"type\":\"currency\",\"code\":\"gold\",\"amount\":100}]}"));
            _h = FlockTestClient.Create(_transport);
            _h.SetReachable(true);
            _h.LoginAs("player-a");
        }

        [TearDown]
        public void TearDown()
        {
            _purchases?.Dispose();
            _purchases = null;
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [UnityTest]
        public IEnumerator AFakeStorePurchase_IsDeliveredByFlock_ThenFinishedInTheStore()
        {
            bool finished = false;
            StoreController watcher = UnityIAPServices.StoreController();
            Action<Order> heardFinished = order => finished = true;
            watcher.OnPurchaseConfirmed += heardFinished;
            try
            {
                Task<FlockStorePurchases> connecting = FlockStorePurchases.ConnectAsync(PaymentProviders.GooglePlay);
                yield return FlockTestWait.Until(() => connecting.IsCompleted, "Unity IAP connected and Flock's products registered");
                Assert.IsFalse(connecting.IsFaulted, connecting.Exception?.GetBaseException().Message);
                _purchases = connecting.Result;

                Task<FlockStorePurchase> buying = _purchases.BuyAsync("gems");
                yield return FlockTestWait.Until(() => buying.IsCompleted, "The fake store's purchase was redeemed");
                Assert.IsFalse(buying.IsFaulted, buying.Exception?.GetBaseException().Message);

                Assert.AreEqual(FlockStorePurchaseOutcome.Delivered, buying.Result.Outcome);
                string sent = _transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody;
                StringAssert.Contains("\"provider\":\"google_play\"", sent);
                StringAssert.Contains("\"product_id\":\"com.flock.gems\"", sent, "The store's own order names the item's product.");
                yield return FlockTestWait.Until(() => finished, "The store heard the purchase is finished");
            }
            finally
            {
                watcher.OnPurchaseConfirmed -= heardFinished;
            }
        }
    }
}
#endif
