using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // The store purchase rules, driven through Shop.PurchaseAsync and a store of the test's own: Flock delivers before the
    // store's purchase is finished, a refused one stays unfinished, unfinished ones wait for a sign-in and go once a launch,
    // and orders for products no Flock item sells are the game's.
    public class FlockStoreTests
    {
        private sealed class FakeStore : IFlockStoreConnection
        {
            public readonly List<string> Calls = new List<string>();
            public Action<string> OnBuy;
            public Action OnConnect;
            public bool ConnectFails;
            public bool ConnectNeverAnswers;
            public bool RegisterWaitsForCancel;
            public readonly Dictionary<string, FlockStorePrice> Prices = new Dictionary<string, FlockStorePrice>();
            public readonly List<FlockStoreOrder> Owned = new List<FlockStoreOrder>();
            public readonly List<FlockStoreOrder> Unfinished = new List<FlockStoreOrder>();

            public event Action<FlockStoreOrder> OrderPaid;
            public event Action<FlockStoreOrder> OrderDeferred;
            public event Action<FlockStoreFailure> OrderFailed;

            public Task ConnectAsync()
            {
                Calls.Add("connect");
                OnConnect?.Invoke();
                if (ConnectFails)
                    throw new InvalidOperationException("The store did not connect");
                return ConnectNeverAnswers ? new TaskCompletionSource<bool>().Task : Task.CompletedTask;
            }

            public Task RegisterProductsAsync(IReadOnlyList<FlockStoreProduct> products, CancellationToken cancellationToken)
            {
                foreach (FlockStoreProduct product in products)
                    Calls.Add($"register:{product.ProductId}:{(product.Consumable ? "consumable" : "one-time")}");
                return RegisterWaitsForCancel ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.CompletedTask;
            }

            public void SetAccountToken(string accountToken) => Calls.Add("token:" + accountToken);

            public void Buy(string productId)
            {
                Calls.Add("buy:" + productId);
                OnBuy?.Invoke(productId);
            }

            public void Finish(FlockStoreOrder order) => Calls.Add("finish:" + order.TransactionId);
            public void FetchUnfinishedPurchases() => Calls.Add("fetch");
            public void StopListening() => Calls.Add("stop");

            public FlockStorePrice PriceOf(string productId) => Prices.TryGetValue(productId, out FlockStorePrice price) ? price : null;

            public Task<IReadOnlyList<FlockStoreOrder>> FetchOwnedPurchasesAsync(CancellationToken cancellationToken)
            {
                Calls.Add("restore");
                // As Unity IAP's fetch does: every unfinished purchase is handed over as paid before the owned ones come back.
                foreach (FlockStoreOrder order in Unfinished)
                    OrderPaid?.Invoke(order);
                return Task.FromResult<IReadOnlyList<FlockStoreOrder>>(new List<FlockStoreOrder>(Owned));
            }

            public void Pay(string productId, string transactionId) => OrderPaid?.Invoke(new FlockStoreOrder(productId, transactionId, null));
            public void Defer(string productId) => OrderDeferred?.Invoke(new FlockStoreOrder(productId, null, null));
            public void Fail(string productId, string reason, string text) => OrderFailed?.Invoke(new FlockStoreFailure(productId, reason, text));
            public void Own(string productId, string transactionId) => Owned.Add(new FlockStoreOrder(productId, transactionId, null));
        }

        private const string Gems = "com.flock.gems";
        private const string Sword = "com.flock.sword";
        private const string Twin = "com.flock.twin";
        private const string AccountToken = "c1601ba6-6805-5c7b-92d0-40ccdbedd6eb";

        private static string Item(string id, string type, string skus)
            => "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"type\":\"" + type + "\",\"price\":199,\"currency\":\"USD\",\"is_real_money\":true,\"store_skus\":" + skus + "}";

        private static readonly string GemsItem = Item("gems", "currency_pack", "{\"google_play\":\"" + Gems + "\",\"apple_app_store\":\"com.flock.gems.ios\"}");
        private static readonly string SwordItem = Item("sword", "standard", "{\"google_play\":\"" + Sword + "\"}");

        private static readonly string Catalog =
            "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" +
            GemsItem + "," +
            SwordItem + "," +
            Item("shield", "standard", "{}") + "," +
            Item("pass", "subscription", "{\"google_play\":\"com.flock.pass\"}") + "," +
            Item("twin-a", "consumable", "{\"google_play\":\"" + Twin + "\"}") + "," +
            Item("twin-b", "consumable", "{\"google_play\":\"" + Twin + "\"}") +
            "]}],\"total\":1,\"page\":1,\"limit\":100}";

        private const string Delivered =
            "{\"purchase_id\":\"pur-1\",\"item_type\":\"currency_pack\",\"status\":\"paid\",\"granted\":[{\"type\":\"currency\",\"code\":\"gold\",\"amount\":100}]}";

        private static FlockFakeTransport Backend(FlockHttpResponse redemption, string catalog = null)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(catalog ?? Catalog));
            transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok("{\"player_id\":\"player-a\",\"account_token\":\"" + AccountToken + "\"}"));
            transport.On(FlockEndpoints.ShopValidateReceipt, redemption);
            ItemReads(transport, catalog ?? Catalog);
            return transport;
        }

        // Each catalog item answers its own read, as the server does, for a purchase made with no store connected; the shop answers its own
        // read with what it sells, which the store checks before taking a payment.
        private static void ItemReads(FlockFakeTransport transport, string catalog)
        {
            JToken shop = JObject.Parse(catalog)["items"][0];
            transport.On(FlockEndpoints.ShopById((string)shop["id"]), FlockFakeTransport.Ok(shop.ToString()));
            foreach (JToken item in shop["shop_items"])
                transport.On(FlockEndpoints.ShopItemById((string)item["id"]), FlockFakeTransport.Ok(item.ToString()));
        }

        private static FlockStore Connect(FlockTestClient h, FakeStore store)
            => h.Run(() => FlockStore.ConnectAsync(store, PaymentProviders.GooglePlay, CancellationToken.None));

        private static void SignIn(FlockTestClient h, string playerId)
        {
            h.LoginAs(playerId);
            FlockEvents.InvokeAuthenticated(new FlockAuthInfo(playerId, FlockAuthMethod.Device));
        }

        [Test]
        public void Connect_RegistersEachItemsProductForThisStore_WithItsKind()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);

                Connect(h, store);

                CollectionAssert.Contains(store.Calls, "register:" + Gems + ":consumable", "A currency pack is a consumable product.");
                CollectionAssert.Contains(store.Calls, "register:" + Sword + ":one-time", "A standard item is a one-time product.");
                CollectionAssert.DoesNotContain(store.Calls, "register:com.flock.pass:consumable", "A kind this SDK does not sell through the store is left out.");
                CollectionAssert.DoesNotContain(store.Calls, "register:com.flock.gems.ios:consumable", "Only this store's product ids.");
            }
        }

        [Test]
        public void Buy_Delivered_IsFinishedInTheStoreOnlyOnceFlockDelivered()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
            transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok("{\"player_id\":\"player-a\",\"account_token\":\"" + AccountToken + "\"}"));
            ItemReads(transport, Catalog);
            bool finishedBeforeFlockAnswered = true;
            transport.Default(request =>
            {
                finishedBeforeFlockAnswered = store.Calls.Exists(call => call.StartsWith("finish:"));
                return FlockFakeTransport.Ok(Delivered);
            });
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                List<string> completed = new List<string>();
                purchases.PurchaseCompleted += (item, result) => completed.Add(item.Id + ":" + result.PurchaseId);

                PurchaseResult purchase = h.Run(() => h.Client.Shop.PurchaseAsync("gems"));

                Assert.IsTrue(purchase.IsPaid);
                Assert.AreEqual("pur-1", purchase.PurchaseId);
                CollectionAssert.AreEqual(new[] { "gems:pur-1" }, completed, "Every delivery is reported, the one the call answers included.");
                Assert.IsFalse(finishedBeforeFlockAnswered, "When Flock answered, the store's purchase was still unfinished.");
                Assert.AreEqual(1, store.Calls.FindAll(call => call == "finish:token-1").Count, "Finished once Flock delivered.");
                Assert.AreEqual("{\"shop_item_id\":\"gems\",\"provider\":\"google_play\",\"receipt\":{\"product_id\":\"" + Gems + "\",\"purchase_token\":\"token-1\"}}",
                    transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody);
            }
        }

        [Test]
        public void Buy_SetsThePlayersAccountToken_BeforeTheStoreBuys()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                h.Run(() => h.Client.Shop.PurchaseAsync("gems"));

                int token = store.Calls.IndexOf("token:" + AccountToken);
                Assert.GreaterOrEqual(token, 0, "The account token is set on the store.");
                Assert.Less(token, store.Calls.IndexOf("buy:" + Gems), "Set before the store's purchase starts, so the store keeps it with the purchase.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopItemById("gems")), "The connected store's catalog names the item, so it is not read again.");
            }
        }

        [Test]
        public void Buy_FlockRefuses_ItStaysUnfinished_AndTheCallThrowsWithTheCode()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Status(422,
                "{\"detail\":{\"code\":\"payment_provider.store_credentials_rejected\",\"message\":\"Google rejected this game's store credentials (service account refused (HTTP 400)); the studio's payment settings need fixing before receipts can be verified\"}}"))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                List<FlockUndeliveredPurchase> notDelivered = new List<FlockUndeliveredPurchase>();
                purchases.PurchaseNotDelivered += notDelivered.Add;

                FlockException refused = Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));

                Assert.AreEqual(FlockErrorCode.PaymentProviderStoreCredentialsRejected, refused.ErrorCode);
                Assert.AreEqual(1, notDelivered.Count, "Every paid purchase Flock did not deliver is reported, the one the call throws for included.");
                Assert.AreEqual(FlockErrorCode.PaymentProviderStoreCredentialsRejected, notDelivered[0].Error.ErrorCode);
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")), "A refused purchase is never finished: the store hands it back.");
            }
        }

        [Test]
        public void Buy_PlayerCancels_ThrowsCancelled_AndFlockIsNotAsked()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                store.OnBuy = productId => store.Fail(productId, FlockStoreFailureReasons.Cancelled, "UserCancelled: closed the dialog");

                FlockStoreException cancelled = Assert.Throws<FlockStoreException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));

                Assert.AreEqual(FlockStoreFailureReasons.Cancelled, cancelled.Reason);
                StringAssert.Contains("Nothing was charged", cancelled.Hint);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        [Test]
        public void Buy_StoreDeclines_ThrowsTheStoresReason_AndItsWords()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                store.OnBuy = productId => store.Fail(productId, FlockStoreFailureReasons.PaymentDeclined, "PaymentDeclined: card refused");

                FlockStoreException declined = Assert.Throws<FlockStoreException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));

                Assert.AreEqual(FlockStoreFailureReasons.PaymentDeclined, declined.Reason, "The reason a game branches on, apart from the store's own words.");
                StringAssert.Contains("PaymentDeclined: card refused", declined.Message);
                Assert.IsNotEmpty(declined.Hint);
            }
        }

        // Google Play answers a declined card with BILLING_UNAVAILABLE, as for purchases switched off (measured with its declining test card).
        [Test]
        public void Buy_GooglePlayCannotTakeThePayment_ReadsStoreUnavailable_AndTheFixNamesBothCauses()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                store.OnBuy = productId => store.Fail(productId, FlockStoreFailureReasons.StoreUnavailable, "PurchasingUnavailable:  - Google BillingResponseCode = 3");

                FlockStoreException unavailable = Assert.Throws<FlockStoreException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));

                Assert.AreEqual(FlockStoreFailureReasons.StoreUnavailable, unavailable.Reason);
                StringAssert.Contains("Nothing was charged", unavailable.Hint);
                StringAssert.Contains("online", unavailable.Hint);
                StringAssert.Contains("payment method", unavailable.Hint, "A declined card arrives this way on Google Play, so the fix names it.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        [Test]
        public void Buy_WaitingForPayment_AnswersPending_ThenIsReportedCompleted()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                store.OnBuy = productId => store.Defer(productId);
                List<ShopItem> completed = new List<ShopItem>();
                purchases.PurchaseCompleted += (item, result) => completed.Add(item);

                PurchaseResult purchase = h.Run(() => h.Client.Shop.PurchaseAsync("gems"));
                Assert.AreEqual(0, completed.Count, "Nothing is delivered while the store waits for the payment.");
                store.Pay(Gems, "token-2");

                Assert.AreEqual(PurchaseResultStatuses.Pending, purchase.Status);
                Assert.IsFalse(purchase.IsPaid);
                Assert.AreEqual(1, completed.Count, "Paid later, it is reported through PurchaseCompleted.");
                Assert.AreEqual("gems", completed[0].Id);
                CollectionAssert.Contains(store.Calls, "finish:token-2");
            }
        }

        [Test]
        public void Buy_NotSignedIn_Throws_AndTheStoreIsNotAsked()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                Connect(h, store);

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")));
            }
        }

        [TestCase("shield", Description = "No product id for this store")]
        [TestCase("pass", Description = "A kind not sold through the store")]
        [TestCase("late", Description = "Added to the shops since the store connected")]
        public void Buy_AnItemThisStoreDoesNotSell_Throws_AndTheStoreIsNotAsked(string shopItemId)
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            transport.On(FlockEndpoints.ShopItemById("late"), FlockFakeTransport.Ok(Item("late", "consumable", "{\"google_play\":\"com.flock.late\"}")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);

                FlockValidationException ex = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync(shopItemId)));

                Assert.IsNotEmpty(ex.Hint);
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")));
            }
        }

        [Test]
        public void UnfinishedPurchase_BeforeSignIn_WaitsForIt_ThenIsSentAndDelivered()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockStore purchases = Connect(h, store);
                List<ShopItem> delivered = new List<ShopItem>();
                purchases.PurchaseCompleted += (item, result) => delivered.Add(item);

                store.Pay(Sword, "token-old");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "Nobody is signed in to redeem it for.");

                SignIn(h, "player-a");

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                Assert.AreEqual(1, delivered.Count);
                Assert.AreEqual("sword", delivered[0].Id);
                CollectionAssert.Contains(store.Calls, "finish:token-old");
            }
        }

        // Google hands an unfinished purchase back only when asked, so each sign-in asks.
        [Test]
        public void EachSignIn_AsksTheStoreForUnfinishedPurchases()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                Assert.AreEqual(1, store.Calls.FindAll(call => call == "fetch").Count, "Connecting while signed in asks at once.");

                SignIn(h, "player-b");

                Assert.AreEqual(2, store.Calls.FindAll(call => call == "fetch").Count);
            }
        }

        [Test]
        public void UnfinishedPurchase_IsSentOnceALaunch_AndARefusalNeedsAttention()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Status(403,
                "{\"detail\":{\"code\":\"payment_provider.receipt_belongs_to_another_player\",\"message\":\"Receipt refused: this store transaction was redeemed by another player\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                List<FlockUndeliveredPurchase> problems = new List<FlockUndeliveredPurchase>();
                purchases.PurchaseNotDelivered += problems.Add;

                store.Pay(Sword, "token-old");
                store.Pay(Sword, "token-old");

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "Sent once a launch; the next launch sends it again.");
                Assert.AreEqual(1, problems.Count);
                Assert.AreEqual(FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer, problems[0].Error.ErrorCode);
                Assert.AreEqual("sword", problems[0].Item.Id);
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")), "It stays unfinished, for the player it belongs to.");
            }
        }

        [Test]
        public void OrderForAProductNoFlockItemSells_IsLeftToTheGame()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                int events = 0;
                purchases.PurchaseCompleted += (item, result) => events++;
                purchases.PurchaseNotDelivered += problem => events++;

                store.Pay("com.game.own.thing", "token-game");

                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")), "The game finishes its own purchases.");
                Assert.AreEqual(0, events);
            }
        }

        [Test]
        public void UnfinishedPurchaseOfAProductTwoItemsSell_NeedsAttention_AndIsNotSent()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                List<FlockUndeliveredPurchase> problems = new List<FlockUndeliveredPurchase>();
                purchases.PurchaseNotDelivered += problems.Add;

                store.Pay(Twin, "token-twin");

                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "Neither item can be told from the other.");
                Assert.AreEqual(1, problems.Count);
                Assert.IsNull(problems[0].Item);
                Assert.IsNotEmpty(problems[0].Error.Hint);
            }
        }

        [Test]
        public void Buy_AProductTwoItemsSell_IsSentForTheItemTheCallNames()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-twin");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                PurchaseResult purchase = h.Run(() => h.Client.Shop.PurchaseAsync("twin-b"));

                Assert.IsTrue(purchase.IsPaid);
                StringAssert.Contains("\"shop_item_id\":\"twin-b\"", transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody);
            }
        }

        // A store hands back unfinished purchases as it connects, before the shops are read: those are held, not dropped.
        [Test]
        public void PurchaseHandedBackWhileConnecting_IsSentOnceConnectedAndSignedIn()
        {
            FakeStore store = new FakeStore();
            store.OnConnect = () => store.Pay(Sword, "token-early");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");

                Connect(h, store);

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                CollectionAssert.Contains(store.Calls, "finish:token-early");
            }
        }

        // The product is taken before the account token is read, so a second call meanwhile is refused instead of overwriting the first.
        [Test]
        public void TwoBuysOfOneProductAtOnce_TheSecondIsRefused()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                transport.GateNext(FlockEndpoints.ShopAccountToken);

                // Neither call is waited on: both resume on the main thread, and a second call let through would wait on the gate.
                Task<PurchaseResult> first = h.Client.Shop.PurchaseAsync("gems");
                Task<PurchaseResult> second = h.Client.Shop.PurchaseAsync("gems");
                try
                {
                    Assert.IsTrue(second.IsFaulted, "Refused while the first call reads its token, not let through to take the first one's place.");
                    Assert.IsInstanceOf<FlockValidationException>(second.Exception.InnerException);
                    Assert.IsFalse(first.IsCompleted);
                    Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")), "No purchase was started by the refused call.");
                }
                finally
                {
                    transport.ReleaseGate();
                }
            }
        }

        [Test]
        public void ConnectFailing_StopsListeningToTheStore()
        {
            FakeStore store = new FakeStore { ConnectFails = true };
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);

                Assert.Catch<InvalidOperationException>(() => Connect(h, store));

                CollectionAssert.Contains(store.Calls, "stop");
            }
        }

        // One way to pay real money a client: a store connecting over a web checkout would take its place in Shop.PurchaseAsync.
        [Test]
        public void Connect_BesideAWebCheckout_IsRefused_AndTheWebCheckoutStays()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                FlockStore checkout = h.Run(() => FlockStore.ConnectAsync(null, null, new FlockStoreOptions(), CancellationToken.None));

                FlockValidationException refused = Assert.Throws<FlockValidationException>(() => Connect(h, store));

                StringAssert.Contains("Dispose it before connecting again", refused.Hint);
                Assert.AreSame(checkout, h.Client.Shop.ConnectedStore, "The web checkout stays the way to pay.");
                CollectionAssert.DoesNotContain(store.Calls, "connect", "The refused store was never connected.");
                checkout.Dispose();
            }
        }

        // The place is taken as the connect starts, so a second connect made while the store connects is refused at once and the store stays.
        [Test]
        public void AWebCheckoutConnect_WhileTheStoreConnects_IsRefused_AndTheStoreStays()
        {
            FakeStore store = new FakeStore();
            Task<FlockStore> second = null;
            store.OnConnect = () => second = FlockStore.ConnectAsync(null, null, new FlockStoreOptions(), CancellationToken.None);
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);

                FlockStore connected = Connect(h, store);

                Assert.IsNotNull(second);
                Assert.IsInstanceOf<FlockValidationException>(second.Exception?.GetBaseException(), "The second connect was refused at once.");
                Assert.AreSame(connected, h.Client.Shop.ConnectedStore, "The store that connected first stays the way to pay.");
                connected.Dispose();
            }
        }

        // The public connect: a store is reached through the store library Unity IAP's assembly registers, and this build target's own way
        // to pay is taken when nothing is named.
        [TestCase(null)]
        [TestCase("")]
        [TestCase(PaymentProviders.GooglePlay)]
        public void Connect_ThroughThePublicCall_ReachesAStoreThroughTheStoreLibrary(string named)
        {
            FakeStore store = new FakeStore();
            Func<IFlockStoreConnection> library = FlockStore.StoreLibrary;
            FlockStore.StoreLibrary = () => store;
            try
            {
                using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
                {
                    h.SetReachable(true);

                    FlockStore connected = h.Run(() => FlockStore.ConnectAsync(new FlockStoreOptions { PaymentProvider = named }));

                    string expected = string.IsNullOrEmpty(named) ? FlockStore.PaymentProviderOfThisPlatform() : named;
                    Assert.AreEqual(expected, connected.PaymentProvider);
                    Assert.AreEqual(expected == null, connected.IsWebCheckout);
                    Assert.AreEqual(expected != null, store.Calls.Contains("connect"), "A store is reached through the store library, a web checkout never.");
                    if (expected != null)
                        CollectionAssert.Contains(store.Calls, "register:" + Gems + ":consumable");
                    connected.Dispose();
                }
            }
            finally
            {
                FlockStore.StoreLibrary = library;
            }
        }

        // Android and iOS builds pay through their store; every other build target through a web checkout with the game's one active gateway.
        [Test]
        public void ThisBuildTargetsWayToPay_IsItsStoreOnAPhone_AndAWebCheckoutElsewhere()
        {
#if UNITY_ANDROID
            Assert.AreEqual(PaymentProviders.GooglePlay, FlockStore.PaymentProviderOfThisPlatform());
#elif UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS
            Assert.AreEqual(PaymentProviders.AppleAppStore, FlockStore.PaymentProviderOfThisPlatform());
#else
            Assert.IsNull(FlockStore.PaymentProviderOfThisPlatform());
#endif
        }

        // A phone's store rules forbid paying for digital goods elsewhere: without Unity IAP 5 a store is refused with the package to install,
        // nothing is read or connected, and a real-money purchase opens no web checkout.
        [TestCase(PaymentProviders.GooglePlay)]
        [TestCase(PaymentProviders.AppleAppStore)]
        public void Connect_AStore_WithoutUnityIap5_IsRefused_AndNoWebCheckoutOpens(string store)
        {
            Func<IFlockStoreConnection> library = FlockStore.StoreLibrary;
            FlockStore.StoreLibrary = null;
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            try
            {
                using (FlockTestClient h = FlockTestClient.Create(transport))
                {
                    h.SetReachable(true);
                    h.LoginAs("player-a");

                    FlockValidationException refused = Assert.Throws<FlockValidationException>(() => h.Run(() => FlockStore.ConnectAsync(new FlockStoreOptions { PaymentProvider = store })));

                    StringAssert.Contains("Unity IAP 5", refused.Message);
#if FLOCK_UNITY_IAP_5
                    // Unity IAP 5 is in this project, so its Flock connection was left out of the build.
                    StringAssert.Contains("Flock.Shop.UnityIap", refused.Hint);
#else
                    StringAssert.Contains("com.unity.purchasing", refused.Hint);
#endif
                    Assert.IsNull(h.Client.Shop.ConnectedStore);
                    Assert.AreEqual(0, transport.CountTo(FlockEndpoints.Shop + "?"), "Nothing was read for a connect that was refused.");
                    Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));
                    Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopCheckout), "No web checkout was opened instead.");
                    Assert.DoesNotThrow(() => Connect(h, new FakeStore()).Dispose(), "The refused connect took no place.");
                }
            }
            finally
            {
                FlockStore.StoreLibrary = library;
            }
        }

        // A name Flock does not pay through, or a store library handed a gateway's name, is refused before anything is connected.
        [TestCase("steam", false)]
        [TestCase("", false)]
        [TestCase(PaymentProviders.Stripe, true)]
        public void Connect_ToWhatFlockDoesNotPayThrough_IsRefused(string paymentProvider, bool withAStoreLibrary)
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);

                FlockValidationException refused = Assert.Throws<FlockValidationException>(() =>
                    h.Run(() => FlockStore.ConnectAsync(withAStoreLibrary ? store : null, paymentProvider, new FlockStoreOptions(), CancellationToken.None)));

                StringAssert.Contains("PaymentProviders", refused.Hint);
                Assert.IsNull(h.Client.Shop.ConnectedStore);
                CollectionAssert.DoesNotContain(store.Calls, "connect");
            }
        }

        // Unity IAP's connect takes no token: a store that never answers is waited for only until the caller's token is cancelled.
        // Frames go by as in a game, since the connect resumes on the main thread.
        [UnityTest]
        public IEnumerator Connect_IsCancelledWhileTheStoreConnects_AndStopsListening()
        {
            FakeStore store = new FakeStore { ConnectNeverAnswers = true };
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                CancellationTokenSource cancel = new CancellationTokenSource();

                Task<FlockStore> connecting = FlockStore.ConnectAsync(store, PaymentProviders.GooglePlay, cancel.Token);
                cancel.Cancel();

                // A bounded wait: a connect that ignored the token would never end.
                yield return FlockTestWait.Until(() => connecting.IsCompleted, "Connecting ended once cancelled");
                Assert.Catch<OperationCanceledException>(() => connecting.GetAwaiter().GetResult());
                CollectionAssert.Contains(store.Calls, "stop");
                Assert.DoesNotThrow(() => Connect(h, new FakeStore()), "The cancelled connection gave its place up.");
            }
        }

        // The store retries a product fetch it cannot make; the caller's token is what ends the wait.
        [Test]
        public void Connect_IsCancelledWhileTheStoreFindsProducts_AndStopsListening()
        {
            FakeStore store = new FakeStore { RegisterWaitsForCancel = true };
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                CancellationTokenSource cancel = new CancellationTokenSource();

                Task<FlockStore> connecting = FlockStore.ConnectAsync(store, PaymentProviders.GooglePlay, cancel.Token);
                cancel.Cancel();

                // A bounded wait: a fetch that ignored the token would never end.
                Assert.IsTrue(((IAsyncResult)connecting).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "Connecting ended once cancelled.");
                Assert.Catch<OperationCanceledException>(() => connecting.GetAwaiter().GetResult());
                CollectionAssert.Contains(store.Calls, "stop");
            }
        }

        [Test]
        public void PurchaseAfterTheClientShutDown_IsIgnored()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                FlockClient.Shutdown();

                Assert.DoesNotThrow(() => store.Pay(Sword, "token-late"));
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")));
            }
        }

        [Test]
        public void ASecondConnect_WhileTheFirstIsConnected_IsRefused_UntilTheFirstIsDisposed()
        {
            FakeStore store = new FakeStore();
            FakeStore second = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                FlockStore first = Connect(h, store);

                Assert.Throws<FlockValidationException>(() => Connect(h, second));
                CollectionAssert.Contains(second.Calls, "stop", "The refused connection stopped listening to its store.");
                CollectionAssert.DoesNotContain(store.Calls, "stop", "The first connection still listens.");

                first.Dispose();
                Assert.DoesNotThrow(() => Connect(h, new FakeStore()));
            }
        }

        [Test]
        public void AConnectionMadeUnderAnEarlierClient_NoLongerActs_AndIsNotTheNewClientsStore()
        {
            FakeStore store = new FakeStore();
            FlockStore old;
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                old = Connect(h, store);
            }
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");

                store.Pay(Sword, "token-old-client");

                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "The new client's own connection sends it, not this one.");
                FlockValidationException noStore = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));
                StringAssert.Contains("FlockStore.ConnectAsync", noStore.Hint, "The new client has no store connected until it connects one.");
                Assert.Throws<FlockValidationException>(() => old.GetPrice("gems"), "The earlier client's connection refuses every call.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")));
                Assert.DoesNotThrow(() => Connect(h, new FakeStore()), "A connection under the new client is not refused.");
            }
        }

        // One call for every item: an item priced in a game currency goes to the wallet even with a store connected.
        [Test]
        public void AWalletItem_WithTheStoreConnected_IsBoughtFromTheWallet_AndTheStoreIsNotAsked()
        {
            FakeStore store = new FakeStore();
            string withWalletItem = "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" +
                Item("gems", "currency_pack", "{\"google_play\":\"" + Gems + "\"}") + "," +
                "{\"id\":\"potion\",\"name\":\"potion\",\"type\":\"consumable\",\"price\":20,\"currency\":\"Gold\",\"is_real_money\":false,\"store_skus\":{}}" +
                "]}],\"total\":1,\"page\":1,\"limit\":100}";
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered), withWalletItem);
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-wallet\",\"item_type\":\"consumable\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);

                PurchaseResult bought = h.Run(() => h.Client.Shop.PurchaseAsync("potion"));

                Assert.AreEqual("pur-wallet", bought.PurchaseId);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopTransaction), "Bought from the wallet.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")), "The store is never asked about a wallet item.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        private const string ReceiptInvalid =
            "{\"detail\":{\"code\":\"payment_provider.receipt_invalid\",\"message\":\"Receipt rejected: Google returned 400\"}}";

        private const string CredentialsRejected =
            "{\"detail\":{\"code\":\"payment_provider.store_credentials_rejected\",\"message\":\"Google rejected this game's store credentials (HTTP 401); the studio's payment settings need fixing before receipts can be verified\"}}";

        [Test]
        public void StorePrice_IsTheStoresOwn_OrNullWhenTheStoreDidNotFindTheProduct()
        {
            FakeStore store = new FakeStore();
            store.Prices[Gems] = new FlockStorePrice("SAR 3.99", 3.99m, "SAR");
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                FlockStore purchases = Connect(h, store);

                FlockStorePrice price = purchases.GetPrice("gems");

                Assert.AreEqual("SAR 3.99", price.Text);
                Assert.AreEqual(3.99m, price.Amount);
                Assert.AreEqual("SAR", price.CurrencyCode);
                Assert.IsNull(purchases.GetPrice("sword"), "The store did not find the sword's product.");
                Assert.Throws<FlockValidationException>(() => purchases.GetPrice("shield"), "An item with no product id for this store has no store price to show.");
            }
        }

        [Test]
        public void Restore_HasFlockDeliverEachOwnedPurchaseOfItsItems_AndFinishesNothing()
        {
            FakeStore store = new FakeStore();
            store.Own(Sword, "token-owned");
            store.Own("com.game.own.thing", "token-game");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                FlockStoreRestore restore = h.Run(() => purchases.RestoreAsync());

                Assert.AreEqual(1, restore.Restored.Count);
                Assert.AreEqual("sword", restore.Restored[0].Id);
                Assert.AreEqual(0, restore.NotDelivered.Count);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "The game's own product is left to the game.");
                Assert.AreEqual("{\"shop_item_id\":\"sword\",\"provider\":\"google_play\",\"receipt\":{\"product_id\":\"" + Sword + "\",\"purchase_token\":\"token-owned\"}}",
                    transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody);
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")), "An owned purchase was finished long ago; restoring finishes nothing.");
            }
        }

        [Test]
        public void Restore_ARefusal_IsListed_AndTheRestAreStillDelivered()
        {
            FakeStore store = new FakeStore();
            store.Own(Sword, "token-theirs");
            store.Own(Gems, "token-mine");
            store.Own(Twin, "token-twin");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            transport.OnSequence(FlockEndpoints.ShopValidateReceipt,
                FlockFakeTransport.Status(403, "{\"detail\":{\"code\":\"payment_provider.receipt_belongs_to_another_player\",\"message\":\"Receipt refused: the store purchase belongs to a different player\"}}"),
                FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                FlockStoreRestore restore = h.Run(() => purchases.RestoreAsync());

                Assert.AreEqual(1, restore.Restored.Count);
                Assert.AreEqual("gems", restore.Restored[0].Id);
                Assert.AreEqual(2, restore.NotDelivered.Count);
                Assert.AreEqual(FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer, restore.NotDelivered[0].Error.ErrorCode);
                Assert.AreEqual("sword", restore.NotDelivered[0].Item.Id);
                Assert.IsNull(restore.NotDelivered[1].Item, "A product two items sell cannot be matched to one.");
                Assert.AreEqual(Twin, restore.NotDelivered[1].ProductId);
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "The product two items sell is not sent.");
            }
        }

        // Measured on a phone: Flock unreachable while paying, Google then answering "already owned", whose fix is to restore.
        [Test]
        public void Restore_SendsAgainAnUnfinishedPurchaseWhoseSendFailedThisLaunch_AndFinishesItOnceDelivered()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            transport.OnSequence(FlockEndpoints.ShopValidateReceipt,
                FlockFakeTransport.Status(422, "{\"detail\":{\"code\":\"payment_provider.store_credentials_rejected\",\"message\":\"Google rejected this game's store credentials\"}}"),
                FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                List<ShopItem> completed = new List<ShopItem>();
                purchases.PurchaseCompleted += (item, result) => completed.Add(item);
                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("sword")));
                store.Unfinished.Add(new FlockStoreOrder(Sword, "token-1", null));

                FlockStoreRestore restore = h.Run(() => purchases.RestoreAsync());

                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "The player asked, so the purchase is sent again this launch.");
                Assert.AreEqual(1, store.Calls.FindAll(call => call == "finish:token-1").Count, "Finished once Flock delivered it.");
                Assert.AreEqual(1, completed.Count, "An unfinished purchase goes through PurchaseCompleted, as the restore promises.");
                Assert.AreEqual("sword", completed[0].Id);
                Assert.AreEqual(0, restore.Restored.Count, "The restore's own list is the store's finished purchases.");
            }
        }

        // Frames go by as in a game, since the held send resumes on the main thread.
        [UnityTest]
        public IEnumerator Restore_LeavesAPurchaseStillBeingSentToItsSend_AndItIsSentOnce()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                transport.GateNext(FlockEndpoints.ShopValidateReceipt);
                store.Pay(Sword, "token-1");
                store.Unfinished.Add(new FlockStoreOrder(Sword, "token-1", null));

                h.Run(() => purchases.RestoreAsync());
                transport.ReleaseGate();
                yield return FlockTestWait.Until(() => store.Calls.Contains("finish:token-1"), "The send under way finished the purchase");

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "A send under way is not started a second time.");
                Assert.AreEqual(1, store.Calls.FindAll(call => call == "finish:token-1").Count);
            }
        }

        [Test]
        public void Restore_NotSignedIn_Throws_AndTheStoreIsNotAsked()
        {
            FakeStore store = new FakeStore();
            store.Own(Sword, "token-owned");
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                FlockStore purchases = Connect(h, store);

                Assert.Throws<FlockAuthException>(() => h.Run(() => purchases.RestoreAsync()));
                CollectionAssert.DoesNotContain(store.Calls, "restore");
            }
        }

        [Test]
        public void SetupCheck_ListsWhatTheStoreDidNotFind_AndItemsWithoutAProductId()
        {
            FakeStore store = new FakeStore();
            store.Prices[Gems] = new FlockStorePrice("$1.99", 1.99m, "USD");
            store.Prices[Twin] = new FlockStorePrice("$1.99", 1.99m, "USD");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Status(422, ReceiptInvalid));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                FlockStoreSetupCheck check = h.Run(() => purchases.CheckSetupAsync());

                CollectionAssert.AreEqual(new[] { "sword" }, check.NotFoundInTheStore.Select(item => item.Id).ToArray());
                CollectionAssert.AreEqual(new[] { "shield" }, check.MissingProductId.Select(item => item.Id).ToArray(), "A kind not sold through the store (the pass) is not listed.");
                Assert.IsTrue(check.ServerCanCheckPurchases, "The store answered the made-up purchase as unknown, so Flock's keys work.");
                Assert.IsNull(check.ServerProblem);
                Assert.IsFalse(check.IsReady);
                StringAssert.Contains("\"purchase_token\":\"flock-setup-check\"", transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody, "Only a made-up purchase is sent.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")), "Nothing is bought.");
            }
        }

        [Test]
        public void SetupCheck_TheStoreRefusingFlocksKeys_IsTheServerProblem()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Status(422, CredentialsRejected))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                FlockStoreSetupCheck check = h.Run(() => purchases.CheckSetupAsync());

                Assert.IsFalse(check.ServerCanCheckPurchases);
                Assert.AreEqual(FlockErrorCode.PaymentProviderStoreCredentialsRejected, check.ServerProblem.ErrorCode);
                Assert.IsNotEmpty(check.ServerProblem.Hint);
                Assert.IsFalse(check.IsReady);
            }
        }

        [Test]
        public void SetupCheck_EveryProductFound_AndTheServerCanCheck_IsReady()
        {
            FakeStore store = new FakeStore();
            store.Prices[Gems] = new FlockStorePrice("$1.99", 1.99m, "USD");
            string onlyGems = "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" +
                Item("gems", "currency_pack", "{\"google_play\":\"" + Gems + "\"}") + "]}],\"total\":1,\"page\":1,\"limit\":100}";
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Status(422, ReceiptInvalid), onlyGems)))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                Assert.IsTrue(h.Run(() => purchases.CheckSetupAsync()).IsReady);
            }
        }

        [Test]
        public void SetupCheck_NotSignedIn_Throws_AndNothingIsSent()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Status(422, ReceiptInvalid));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                FlockStore purchases = Connect(h, store);

                Assert.Throws<FlockAuthException>(() => h.Run(() => purchases.CheckSetupAsync()));
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        [Test]
        public void AfterDispose_EveryCallIsRefused_AndTheStoreIsNotAsked()
        {
            FakeStore store = new FakeStore();
            store.Prices[Gems] = new FlockStorePrice("$1.99", 1.99m, "USD");
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                purchases.Dispose();

                Assert.Throws<ObjectDisposedException>(() => purchases.GetPrice("gems"));
                Assert.Throws<ObjectDisposedException>(() => h.Run(() => purchases.RestoreAsync()));
                Assert.Throws<ObjectDisposedException>(() => h.Run(() => purchases.CheckSetupAsync()));
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")), "A disposed store is no longer the shop's, so a real-money purchase says to connect one.");
                CollectionAssert.DoesNotContain(store.Calls, "restore");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")));
            }
        }

        [Test]
        public void Dispose_StopsListening()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);

                purchases.Dispose();
                store.Pay(Sword, "token-late");

                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                CollectionAssert.Contains(store.Calls, "stop");
            }
        }

        // Switched off in the dashboard after the store connected: the shop no longer lists it for players, so the store is never asked to charge.
        [TestCase(false)]
        [TestCase(true)]
        public void Buy_AnItemSwitchedOffSinceTheStoreConnected_IsRefusedBeforeTheStoreTakesPayment(bool theWholeShop)
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                h.Run(() => h.Client.Shop.GetAllAsync());
                transport.On(FlockEndpoints.ShopById("shop-1"), theWholeShop
                    ? FlockFakeTransport.Coded(404, "shop.shop_not_found")
                    : FlockFakeTransport.Ok(JObject.Parse(Catalog.Replace(GemsItem + ",", ""))["items"][0].ToString()));

                FlockException refused = Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));

                Assert.AreEqual(FlockErrorCode.ShopItemNotForSale, refused.ErrorCode);
                Assert.IsFalse(refused.OutcomeUnknown, "Nothing was charged.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")), "The store was never asked to charge.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                int shopReads = transport.CountTo(FlockEndpoints.Shop + "?");
                h.Run(() => h.Client.Shop.GetAllAsync());
                Assert.AreEqual(shopReads + 1, transport.CountTo(FlockEndpoints.Shop + "?"), "The next read of the shops asks the server, which leaves it out.");
            }
        }

        [Test]
        public void Buy_AnItemStillForSale_ReadsTheShop_AndKeepsTheCachedCatalog()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                int shopReads = transport.CountTo(FlockEndpoints.Shop + "?");

                h.Run(() => h.Client.Shop.PurchaseAsync("gems"));
                h.Run(() => h.Client.Shop.GetAllAsync());

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopById("shop-1")), "Checked with the server once, before the store charged.");
                Assert.AreEqual(shopReads, transport.CountTo(FlockEndpoints.Shop + "?"), "Control: still for sale, so the cached shops are kept.");
            }
        }

        // The store's backend, whose read of the item's shop (made just before the store's dialog) runs duringTheRead as it answers.
        private static FlockFakeTransport BackendWhoseShopRead(Action duringTheRead)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
            transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok("{\"player_id\":\"player-a\",\"account_token\":\"" + AccountToken + "\"}"));
            transport.On(FlockEndpoints.ShopValidateReceipt, FlockFakeTransport.Ok(Delivered));
            string shop = JObject.Parse(Catalog)["items"][0].ToString();
            transport.Default(request =>
            {
                if (!request.Url.Contains(FlockEndpoints.ShopById("shop-1")))
                    return FlockFakeTransport.Ok("{}");
                duringTheRead();
                return FlockFakeTransport.Ok(shop);
            });
            return transport;
        }

        // Bought for the player who asked: one who signed in while the item's shop was read gets nothing from this call.
        [Test]
        public void Buy_AnotherPlayerSignsInWhileTheShopIsRead_TheStoreIsNotAsked()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            Action duringTheShopRead = null;
            FlockFakeTransport transport = BackendWhoseShopRead(() => duringTheShopRead?.Invoke());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                duringTheShopRead = () => h.LoginAs("player-b");

                Assert.Catch<OperationCanceledException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems")));

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopById("shop-1")), "Control: the item's shop was read for the player who asked.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("token:") || call.StartsWith("buy:")), "No account token was set and the store was never asked to charge.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        // Cancelled while the item's shop was read: the store's dialog never opens for a call that reported cancelled.
        [Test]
        public void Buy_CancelledWhileTheShopIsRead_TheStoreIsNotAsked()
        {
            FakeStore store = new FakeStore();
            store.OnBuy = productId => store.Pay(productId, "token-1");
            CancellationTokenSource giveUp = new CancellationTokenSource();
            Action duringTheShopRead = null;
            FlockFakeTransport transport = BackendWhoseShopRead(() => duringTheShopRead?.Invoke());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                // Kept for this sign-in, so the purchase has it without waiting on anything.
                h.Run(() => h.Client.Shop.GetStoreAccountTokenAsync());
                duringTheShopRead = giveUp.Cancel;

                Assert.Catch<OperationCanceledException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("gems", giveUp.Token)));

                CollectionAssert.Contains(store.Calls, "token:" + AccountToken, "Control: the call went as far as the store's purchase.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")), "The store's dialog never opened.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        // Frames until done holds or 10 s pass, without failing: the test asserts in its own body, so a failure still disposes its client.
        private static IEnumerator FramesUntil(Func<bool> done)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until)
                yield return null;
        }

        // A sign-out ends the call waiting on the store's dialog; the store's purchase is held, then delivered at the next sign-in.
        [UnityTest]
        public IEnumerator Buy_SigningOutWhileTheStoreWaits_EndsTheCallAsCancelled_AndItIsDeliveredAtTheNextSignIn()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("gems");
                Assert.IsTrue(store.Calls.Contains("buy:" + Gems) && !buying.IsCompleted, "Control: the call waits on the store's dialog.");

                h.Client.Authentication.Logout();
                yield return FramesUntil(() => buying.IsCompleted);

                Assert.IsTrue(buying.IsCanceled, "The wait ended with the sign-in, as cancelled: " + buying.Status);
                store.Pay(Gems, "token-1");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "Held while nobody is signed in.");
                SignIn(h, "player-a");
                yield return FramesUntil(() => store.Calls.Contains("finish:token-1"));
                CollectionAssert.Contains(store.Calls, "finish:token-1", "Delivered at the next sign-in.");
                purchases.Dispose();
            }
        }

        // Another player signing in ends the call waiting on the store's dialog, as a sign-out does; the same sign-in announced again does not.
        [UnityTest]
        public IEnumerator Buy_AnotherPlayerSigningInWhileTheStoreWaits_EndsTheCallAsCancelled()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("gems");

                FlockEvents.InvokeAuthenticated(new FlockAuthInfo("player-a", FlockAuthMethod.Device));
                yield return null;
                Assert.IsFalse(buying.IsCompleted, "Control: the same sign-in announced again keeps the wait.");

                SignIn(h, "player-b");
                yield return FramesUntil(() => buying.IsCompleted);

                Assert.IsTrue(buying.IsCanceled, "The wait ended with the sign-in, as cancelled: " + buying.Status);
                purchases.Dispose();
            }
        }

        // Flock shutting down ends the call waiting on the store's dialog, as Dispose does; the connection made under it no longer acts.
        [UnityTest]
        public IEnumerator Buy_FlockShuttingDownWhileTheStoreWaits_EndsTheCallAsCancelled()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                Connect(h, store);
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("gems");
                Assert.IsTrue(store.Calls.Contains("buy:" + Gems) && !buying.IsCompleted, "Control: the call waits on the store's dialog.");

                FlockClient.Shutdown();
                yield return FramesUntil(() => buying.IsCompleted);

                Assert.IsTrue(buying.IsCanceled, "The wait ended with Flock, as cancelled: " + buying.Status);
            }
        }

        // The store's backend, whose redemption runs duringIt as it answers, delivered.
        private static FlockFakeTransport BackendWhoseRedemption(Action duringIt)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
            transport.On(FlockEndpoints.ShopAccountToken, FlockFakeTransport.Ok("{\"player_id\":\"player-a\",\"account_token\":\"" + AccountToken + "\"}"));
            ItemReads(transport, Catalog);
            transport.Default(request =>
            {
                if (!request.Url.Contains(FlockEndpoints.ShopValidateReceipt))
                    return FlockFakeTransport.Ok("{}");
                duringIt();
                return FlockFakeTransport.Ok(Delivered);
            });
            return transport;
        }

        // Restored for the player who asked: once another signs in, the rest of the store's purchases are never sent as them.
        [Test]
        public void Restore_AnotherPlayerSignsInMeanwhile_TheRestAreNotSent()
        {
            FakeStore store = new FakeStore();
            store.Own(Sword, "token-1");
            store.Own(Gems, "token-2");
            Action duringTheRedemption = null;
            FlockFakeTransport transport = BackendWhoseRedemption(() => duringTheRedemption?.Invoke());
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStore purchases = Connect(h, store);
                duringTheRedemption = () => h.LoginAs("player-b");

                Assert.Catch<OperationCanceledException>(() => h.Run(() => purchases.RestoreAsync()));

                Assert.IsTrue(transport.Sent(FlockEndpoints.ShopValidateReceipt), "Control: the first purchase was sent for the player who asked.");
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "The rest were never sent as the player who signed in meanwhile.");
            }
        }

        // An earlier launch that read the whole catalog, on the same disk as the test's next launch.
        private static void ALaunchThatSoldEverything(string sharedDir, string apiUrl = null)
        {
            FlockTestClient earlier = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered)), config =>
            {
                config.OfflineCacheDirectory = sharedDir;
                if (apiUrl != null)
                    config.ApiUrl = apiUrl;
            });
            earlier.SetReachable(true);
            Connect(earlier, new FakeStore()).Dispose();
            FlockClient.Shutdown();
        }

        private static void InASharedFolder(Action<string> test)
        {
            string sharedDir = Path.Combine(Path.GetTempPath(), "flock_store_" + Guid.NewGuid().ToString("N"));
            try
            {
                test(sharedDir);
            }
            finally
            {
                if (Directory.Exists(sharedDir))
                    Directory.Delete(sharedDir, true);
            }
        }

        // Paid for and not delivered (the game closed first), then the item left the shops: the store still hands it back, and Flock delivers it.
        [Test]
        public void AnUnfinishedPurchase_OfAnItemTheShopsNoLongerList_IsDeliveredAtTheNextLaunch()
        {
            InASharedFolder(sharedDir =>
            {
                ALaunchThatSoldEverything(sharedDir);
                FakeStore store = new FakeStore();
                FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered), Catalog.Replace(GemsItem + ",", ""));
                transport.On(FlockEndpoints.ShopItemById("gems"), FlockFakeTransport.Ok(GemsItem.Replace("{\"id\"", "{\"status\":\"inactive\",\"id\"")));
                using (FlockTestClient h = FlockTestClient.Create(transport, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h.SetReachable(true);
                    h.LoginAs("player-a");
                    FlockStore purchases = Connect(h, store);
                    List<string> completed = new List<string>();
                    purchases.PurchaseCompleted += (item, result) => completed.Add(item.Id);

                    store.Pay(Gems, "token-paid");

                    CollectionAssert.Contains(store.Calls, "register:" + Gems + ":consumable", "Its product is still registered, so the store hands the purchase back.");
                    Assert.AreEqual("{\"shop_item_id\":\"gems\",\"provider\":\"google_play\",\"receipt\":{\"product_id\":\"" + Gems + "\",\"purchase_token\":\"token-paid\"}}",
                        transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody);
                    CollectionAssert.Contains(store.Calls, "finish:token-paid", "Finished once Flock delivered.");
                    CollectionAssert.AreEqual(new[] { "gems" }, completed);
                }
            });
        }

        [Test]
        public void AnUnfinishedPurchase_OfAnItemNoLongerFound_NeedsAttention_AndStaysUnfinished()
        {
            InASharedFolder(sharedDir =>
            {
                ALaunchThatSoldEverything(sharedDir);
                FakeStore store = new FakeStore();
                FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered), Catalog.Replace(GemsItem + ",", ""));
                transport.On(FlockEndpoints.ShopItemById("gems"), FlockFakeTransport.Coded(404, "shop_item.shop_item_not_found"));
                using (FlockTestClient h = FlockTestClient.Create(transport, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h.SetReachable(true);
                    h.LoginAs("player-a");
                    FlockStore purchases = Connect(h, store);
                    List<FlockUndeliveredPurchase> problems = new List<FlockUndeliveredPurchase>();
                    purchases.PurchaseNotDelivered += problems.Add;

                    store.Pay(Gems, "token-paid");

                    Assert.AreEqual(1, problems.Count, "Reported, not left silently.");
                    Assert.AreEqual(Gems, problems[0].ProductId);
                    Assert.AreEqual(FlockErrorCode.ShopItemShopItemNotFound, problems[0].Error.ErrorCode);
                    Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                    Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")), "It stays unfinished in the store.");
                }
            });
        }

        [Test]
        public void Restore_DeliversAnOwnedPurchaseOfAnItemTheShopsNoLongerList()
        {
            InASharedFolder(sharedDir =>
            {
                ALaunchThatSoldEverything(sharedDir);
                FakeStore store = new FakeStore();
                store.Own(Sword, "token-owned");
                FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered), Catalog.Replace(SwordItem + ",", ""));
                transport.On(FlockEndpoints.ShopItemById("sword"), FlockFakeTransport.Ok(SwordItem));
                using (FlockTestClient h = FlockTestClient.Create(transport, config => config.OfflineCacheDirectory = sharedDir))
                {
                    h.SetReachable(true);
                    h.LoginAs("player-a");
                    FlockStore purchases = Connect(h, store);

                    FlockStoreRestore restore = h.Run(() => purchases.RestoreAsync());

                    Assert.AreEqual(1, restore.Restored.Count);
                    Assert.AreEqual("sword", restore.Restored[0].Id);
                    StringAssert.Contains("\"shop_item_id\":\"sword\"", transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody);
                }
            });
        }

        // Kept for the game and backend that sold them: another game on the same device knows nothing of them.
        [Test]
        public void ProductsSoldBefore_AreKeptForTheirOwnGame()
        {
            InASharedFolder(sharedDir =>
            {
                ALaunchThatSoldEverything(sharedDir, "https://another-backend.invalid");
                FakeStore store = new FakeStore();
                using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered), Catalog.Replace(GemsItem + ",", "")),
                    config => config.OfflineCacheDirectory = sharedDir))
                {
                    h.SetReachable(true);

                    Connect(h, store);

                    CollectionAssert.DoesNotContain(store.Calls, "register:" + Gems + ":consumable");
                    CollectionAssert.Contains(store.Calls, "register:" + Sword + ":one-time", "Control: this game's own catalog is registered.");
                }
            });
        }
    }
}
