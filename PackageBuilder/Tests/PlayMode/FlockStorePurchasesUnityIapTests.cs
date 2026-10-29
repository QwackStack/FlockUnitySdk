#if FLOCK_UNITY_IAP_5
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Exceptions;
using Flock.Http;
using Flock.Logging;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
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

        private IEnumerator ConnectToTheFakeStore()
        {
            Task<FlockStorePurchases> connecting = FlockStorePurchases.ConnectAsync(PaymentProviders.GooglePlay);
            yield return FlockTestWait.Until(() => connecting.IsCompleted, "Unity IAP connected and Flock's products registered");
            Assert.IsFalse(connecting.IsFaulted, connecting.Exception?.GetBaseException().Message);
            _purchases = connecting.Result;
        }

        // Unity IAP warns about every event of its own nobody listens to; a game using Flock's connection should see none of those.
        [UnityTest]
        public IEnumerator Connecting_AndAskingForPurchases_LeaveNoUnityIapEventUnheard()
        {
            List<string> unheard = new List<string>();
            Application.LogCallback listen = (message, stack, type) =>
            {
                if (message.Contains("called without a callback defined"))
                    unheard.Add(message);
            };
            Application.logMessageReceived += listen;
            try
            {
                yield return ConnectToTheFakeStore();
                Task<FlockStoreRestore> restoring = _purchases.RestoreAsync();
                yield return FlockTestWait.Until(() => restoring.IsCompleted, "The store listed its purchases");
            }
            finally
            {
                Application.logMessageReceived -= listen;
            }

            CollectionAssert.IsEmpty(unheard);
        }

        // The price comes from the store's own product, which Unity IAP's fake store fills in.
        [UnityTest]
        public IEnumerator StorePrice_IsReadFromTheStoresProduct()
        {
            yield return ConnectToTheFakeStore();

            FlockStorePrice price = _purchases.GetStorePrice("gems");

            Assert.IsNotNull(price, "The fake store found the product.");
            Assert.IsNotEmpty(price.Text);
        }

        // Unity IAP answers a restore and then a purchase fetch; a callback never answered would leave the call waiting.
        [UnityTest]
        public IEnumerator Restore_OnTheFakeStore_Ends()
        {
            yield return ConnectToTheFakeStore();

            Task<FlockStoreRestore> restoring = _purchases.RestoreAsync();
            yield return FlockTestWait.Until(() => restoring.IsCompleted, "The store answered the restore and listed its purchases");

            Assert.IsFalse(restoring.IsFaulted, restoring.Exception?.GetBaseException().Message);
            Assert.AreEqual(0, restoring.Result.Problems.Count);
        }

        // Named as text: Unity IAP 5.3 has no UserNotAuthenticated, and a reason the installed version lacks is skipped.
        [TestCase("UserCancelled", FlockStoreFailureReasons.Cancelled)]
        [TestCase("DuplicateTransaction", FlockStoreFailureReasons.AlreadyOwned)]
        [TestCase("ExistingPurchasePending", FlockStoreFailureReasons.AlreadyPending)]
        [TestCase("PaymentDeclined", FlockStoreFailureReasons.PaymentDeclined)]
        [TestCase("ProductUnavailable", FlockStoreFailureReasons.ProductUnavailable)]
        [TestCase("PurchasingUnavailable", FlockStoreFailureReasons.StoreUnavailable)]
        [TestCase("StoreNotConnected", FlockStoreFailureReasons.StoreUnavailable)]
        [TestCase("UserNotAuthenticated", FlockStoreFailureReasons.NotSignedInToTheStore)]
        [TestCase("SignatureInvalid", FlockStoreFailureReasons.Unknown)]
        [TestCase("Unknown", FlockStoreFailureReasons.Unknown)]
        public void EachStoreFailure_ReadsAsItsReason(string storeReason, string expected)
        {
            if (!Enum.TryParse(storeReason, out PurchaseFailureReason reason))
                Assert.Ignore($"This Unity IAP version has no {storeReason}.");
            Assert.AreEqual(expected, FlockUnityIapConnection.ReasonOf(reason));
        }
    }

    // Live: the setup check against the real backend, whose Google Play keys for this game are asked about a made-up purchase.
    // Manual only: needs FlockConfig.asset, the backend, and the game's Google Play settings saved in the Flock dashboard.
    public class FlockStorePurchasesLiveTests
    {
        private const string LiveDeviceId = "flock-sdk-live-shop";
        private FlockStorePurchases _store;

        [TearDown]
        public void TearDown()
        {
            _store?.Dispose();
            _store = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [UnityTest, Explicit("Hits the live Flock backend, which asks Google Play; run manually from the Test Runner.")]
        public IEnumerator Live_SetupCheck_FindsFlockCanAskGooglePlay_WithTheGamesKeys()
        {
            FlockConfigAsset asset = Resources.Load<FlockConfigAsset>("FlockConfig");
            if (asset == null || !asset.IsValid(out string _) || string.IsNullOrEmpty(asset.gameVersionId))
            {
                Assert.Ignore("No usable FlockConfig.asset (missing fields or unresolved Game Version ID).");
                yield break;
            }
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            FlockClient.Create(asset.ToInitConfig(), new NullFlockLogger());
            Task signIn = SignInAsync();
            yield return FlockTestWait.Until(() => signIn.IsCompleted, "The live test device signed in");
            Assert.IsFalse(signIn.IsFaulted, signIn.Exception?.GetBaseException().Message);

            Task<FlockStorePurchases> connecting = FlockStorePurchases.ConnectAsync(PaymentProviders.GooglePlay);
            yield return FlockTestWait.Until(() => connecting.IsCompleted, "Unity IAP's fake store connected and Flock's products registered");
            Assert.IsFalse(connecting.IsFaulted, connecting.Exception?.GetBaseException().Message);
            _store = connecting.Result;

            Task<FlockStoreSetupCheck> checking = _store.CheckSetupAsync();
            yield return FlockTestWait.Until(() => checking.IsCompleted, "The server asked Google Play about a made-up purchase", 30f);
            Assert.IsFalse(checking.IsFaulted, checking.Exception?.GetBaseException().Message);
            FlockStoreSetupCheck check = checking.Result;
            Assert.IsTrue(check.FlockCanAskTheStore, check.StoreProblem?.Message);
            Assert.IsNull(check.StoreProblem);
            Assert.AreEqual(0, check.ItemsWithoutAProductId.Count, "Every real-money item of this game has a Google Play product id.");

            // Counter-case: the same made-up purchase for an item with no Google Play product id is refused before Google is asked.
            ShopItem walletItem = null;
            Task<PaginatedResponse<Shop>> shops = FlockClient.Instance.Shop.GetAllAsync();
            yield return FlockTestWait.Until(() => shops.IsCompleted, "The shops were read");
            foreach (Shop shop in shops.Result.Items)
                foreach (ShopItem item in shop.ShopItems)
                    if (walletItem == null && !item.StoreProductIds.ContainsKey(PaymentProviders.GooglePlay))
                        walletItem = item;
            Assert.IsNotNull(walletItem, "Control: this game sells an item with no Google Play product id.");
            Task<PurchaseResult> refused = FlockClient.Instance.Shop.RedeemStoreReceiptAsync(walletItem.Id, StoreReceipt.FromGooglePlay("gems_100", "flock-setup-check"));
            yield return FlockTestWait.Until(() => refused.IsCompleted, "The made-up purchase for an item without a product id was answered");
            FlockException refusal = refused.Exception?.GetBaseException() as FlockException;
            Assert.IsNotNull(refusal, "Refused.");
            Assert.AreEqual(FlockErrorCode.PaymentProviderReceiptProductMismatch, refusal.ErrorCode, refusal.Message);
            Debug.Log($"[Flock] Live setup check: Google Play answered the made-up purchase as unknown with this game's keys; {check.ProductsTheStoreDidNotFind.Count} products the fake store did not find.");
        }

        private static async Task SignInAsync()
        {
            try
            {
                await FlockClient.Instance.Authentication.LoginWithDeviceAsync(LiveDeviceId);
            }
            catch (FlockException notRegistered) when (notRegistered.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
            {
                await FlockClient.Instance.Authentication.RegisterWithDeviceAsync(LiveDeviceId);
            }
        }
    }
}
#endif
