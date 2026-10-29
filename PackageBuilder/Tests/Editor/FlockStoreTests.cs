#if FLOCK_UNITY_IAP_5
using System;
using System.Collections;
using System.Collections.Generic;
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

        private static readonly string Catalog =
            "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" +
            Item("gems", "currency_pack", "{\"google_play\":\"" + Gems + "\",\"apple_app_store\":\"com.flock.gems.ios\"}") + "," +
            Item("sword", "standard", "{\"google_play\":\"" + Sword + "\"}") + "," +
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

        // Each catalog item answers its own read, as the server does, for a purchase made with no store connected.
        private static void ItemReads(FlockFakeTransport transport, string catalog)
        {
            foreach (JToken item in JObject.Parse(catalog)["items"][0]["shop_items"])
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
    }
}
#endif
