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
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.Editor
{
    // The store purchase rules, driven through a store of the test's own: Flock delivers before the store's purchase is
    // finished, a refused one stays unfinished, unfinished ones wait for a sign-in and go once a launch, and orders for
    // products no Flock item sells are the game's.
    public class FlockStorePurchasesTests
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
            public void Fail(string productId, string reason, string text, bool cancelled) => OrderFailed?.Invoke(new FlockStoreFailure(productId, reason, text, cancelled));
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
            return transport;
        }

        private static FlockStorePurchases Connect(FlockTestClient h, FakeStore store)
            => h.Run(() => FlockStorePurchases.ConnectAsync(store, PaymentProviders.GooglePlay, CancellationToken.None));

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
                FlockStorePurchases purchases = Connect(h, store);

                FlockStorePurchase purchase = h.Run(() => purchases.BuyAsync("gems"));

                Assert.AreEqual(FlockStorePurchaseOutcome.Delivered, purchase.Outcome);
                Assert.AreEqual("pur-1", purchase.Delivery.PurchaseId);
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
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);

                h.Run(() => purchases.BuyAsync("gems"));

                int token = store.Calls.IndexOf("token:" + AccountToken);
                Assert.GreaterOrEqual(token, 0, "The account token is set on the store.");
                Assert.Less(token, store.Calls.IndexOf("buy:" + Gems), "Set before the store's purchase starts, so the store keeps it with the purchase.");
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
                FlockStorePurchases purchases = Connect(h, store);

                FlockException refused = Assert.Catch<FlockException>(() => h.Run(() => purchases.BuyAsync("gems")));

                Assert.AreEqual(FlockErrorCode.PaymentProviderStoreCredentialsRejected, refused.ErrorCode);
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("finish:")), "A refused purchase is never finished: the store hands it back.");
            }
        }

        [Test]
        public void Buy_PlayerCancels_EndsCancelled_AndFlockIsNotAsked()
        {
            FakeStore store = new FakeStore();
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Delivered));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);
                store.OnBuy = productId => store.Fail(productId, FlockStoreFailureReasons.Cancelled, "UserCancelled: closed the dialog", true);

                FlockStorePurchase purchase = h.Run(() => purchases.BuyAsync("gems"));

                Assert.AreEqual(FlockStorePurchaseOutcome.Cancelled, purchase.Outcome);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
            }
        }

        [Test]
        public void Buy_StoreDeclines_EndsFailed_WithTheStoresReason()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);
                store.OnBuy = productId => store.Fail(productId, FlockStoreFailureReasons.PaymentDeclined, "PaymentDeclined: card refused", false);

                FlockStorePurchase purchase = h.Run(() => purchases.BuyAsync("gems"));

                Assert.AreEqual(FlockStorePurchaseOutcome.Failed, purchase.Outcome);
                Assert.AreEqual("PaymentDeclined: card refused", purchase.StoreFailure);
                Assert.AreEqual(FlockStoreFailureReasons.PaymentDeclined, purchase.StoreFailureReason, "The reason a game branches on, apart from the store's own words.");
            }
        }

        [Test]
        public void Buy_WaitingForPayment_EndsPending_ThenIsDeliveredThroughTheEvent()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);
                store.OnBuy = productId => store.Defer(productId);
                List<FlockStorePurchase> deliveredLater = new List<FlockStorePurchase>();
                purchases.PurchaseDelivered += deliveredLater.Add;

                FlockStorePurchase purchase = h.Run(() => purchases.BuyAsync("gems"));
                store.Pay(Gems, "token-2");

                Assert.AreEqual(FlockStorePurchaseOutcome.Pending, purchase.Outcome);
                Assert.AreEqual(1, deliveredLater.Count, "Paid later, it is delivered through PurchaseDelivered.");
                Assert.AreEqual("gems", deliveredLater[0].Item.Id);
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
                FlockStorePurchases purchases = Connect(h, store);

                Assert.Throws<FlockAuthException>(() => h.Run(() => purchases.BuyAsync("gems")));
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")));
            }
        }

        [TestCase("shield", Description = "No product id for this store")]
        [TestCase("pass", Description = "A kind not sold through the store")]
        [TestCase("no-such-item", Description = "Not in the game's shops")]
        public void Buy_AnItemThisStoreDoesNotSell_Throws_AndTheStoreIsNotAsked(string shopItemId)
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Ok(Delivered))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);

                FlockValidationException ex = Assert.Throws<FlockValidationException>(() => h.Run(() => purchases.BuyAsync(shopItemId)));

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
                FlockStorePurchases purchases = Connect(h, store);
                List<FlockStorePurchase> delivered = new List<FlockStorePurchase>();
                purchases.PurchaseDelivered += delivered.Add;

                store.Pay(Sword, "token-old");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt), "Nobody is signed in to redeem it for.");

                SignIn(h, "player-a");

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                Assert.AreEqual(1, delivered.Count);
                Assert.AreEqual("sword", delivered[0].Item.Id);
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
                FlockStorePurchases purchases = Connect(h, store);
                List<FlockStorePurchaseProblem> problems = new List<FlockStorePurchaseProblem>();
                purchases.PurchaseNeedsAttention += problems.Add;

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
                FlockStorePurchases purchases = Connect(h, store);
                int events = 0;
                purchases.PurchaseDelivered += purchase => events++;
                purchases.PurchaseNeedsAttention += problem => events++;

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
                FlockStorePurchases purchases = Connect(h, store);
                List<FlockStorePurchaseProblem> problems = new List<FlockStorePurchaseProblem>();
                purchases.PurchaseNeedsAttention += problems.Add;

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
                FlockStorePurchases purchases = Connect(h, store);

                FlockStorePurchase purchase = h.Run(() => purchases.BuyAsync("twin-b"));

                Assert.AreEqual(FlockStorePurchaseOutcome.Delivered, purchase.Outcome);
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
                FlockStorePurchases purchases = Connect(h, store);
                transport.GateNext(FlockEndpoints.ShopAccountToken);

                // Neither call is waited on: both resume on the main thread, and a second call let through would wait on the gate.
                Task<FlockStorePurchase> first = purchases.BuyAsync("gems");
                Task<FlockStorePurchase> second = purchases.BuyAsync("gems");
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

                Task<FlockStorePurchases> connecting = FlockStorePurchases.ConnectAsync(store, PaymentProviders.GooglePlay, cancel.Token);
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

                Task<FlockStorePurchases> connecting = FlockStorePurchases.ConnectAsync(store, PaymentProviders.GooglePlay, cancel.Token);
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
                FlockStorePurchases first = Connect(h, store);

                Assert.Throws<FlockValidationException>(() => Connect(h, second));
                CollectionAssert.Contains(second.Calls, "stop", "The refused connection stopped listening to its store.");
                CollectionAssert.DoesNotContain(store.Calls, "stop", "The first connection still listens.");

                first.Dispose();
                Assert.DoesNotThrow(() => Connect(h, new FakeStore()));
            }
        }

        [Test]
        public void AConnectionMadeUnderAnEarlierClient_NoLongerActs()
        {
            FakeStore store = new FakeStore();
            FlockStorePurchases old;
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
                // Not waited on: a buy let through would wait for a store purchase that never comes.
                Task<FlockStorePurchase> buy = old.BuyAsync("gems");
                Assert.IsTrue(buy.IsFaulted, "A buy on the earlier client's connection is refused.");
                Assert.IsInstanceOf<FlockValidationException>(buy.Exception.InnerException);
                Assert.DoesNotThrow(() => Connect(h, new FakeStore()), "A connection under the new client is not refused.");
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
                FlockStorePurchases purchases = Connect(h, store);

                FlockStorePrice price = purchases.GetStorePrice("gems");

                Assert.AreEqual("SAR 3.99", price.Text);
                Assert.AreEqual(3.99m, price.Amount);
                Assert.AreEqual("SAR", price.CurrencyCode);
                Assert.IsNull(purchases.GetStorePrice("sword"), "The store did not find the sword's product.");
                Assert.Throws<FlockValidationException>(() => purchases.GetStorePrice("shield"), "An item with no product id for this store has no store price to show.");
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
                FlockStorePurchases purchases = Connect(h, store);

                FlockStoreRestore restore = h.Run(() => purchases.RestoreAsync());

                Assert.AreEqual(1, restore.Restored.Count);
                Assert.AreEqual("sword", restore.Restored[0].Item.Id);
                Assert.AreEqual(FlockStorePurchaseOutcome.Delivered, restore.Restored[0].Outcome);
                Assert.AreEqual(0, restore.Problems.Count);
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
                FlockStorePurchases purchases = Connect(h, store);

                FlockStoreRestore restore = h.Run(() => purchases.RestoreAsync());

                Assert.AreEqual(1, restore.Restored.Count);
                Assert.AreEqual("gems", restore.Restored[0].Item.Id);
                Assert.AreEqual(2, restore.Problems.Count);
                Assert.AreEqual(FlockErrorCode.PaymentProviderReceiptBelongsToAnotherPlayer, restore.Problems[0].Error.ErrorCode);
                Assert.AreEqual("sword", restore.Problems[0].Item.Id);
                Assert.IsNull(restore.Problems[1].Item, "A product two items sell cannot be matched to one.");
                Assert.AreEqual(Twin, restore.Problems[1].ProductId);
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
                FlockStorePurchases purchases = Connect(h, store);

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
                FlockStorePurchases purchases = Connect(h, store);

                FlockStoreSetupCheck check = h.Run(() => purchases.CheckSetupAsync());

                CollectionAssert.AreEqual(new[] { "sword" }, check.ProductsTheStoreDidNotFind.Select(item => item.Id).ToArray());
                CollectionAssert.AreEqual(new[] { "shield" }, check.ItemsWithoutAProductId.Select(item => item.Id).ToArray(), "A kind not sold through the store (the pass) is not listed.");
                Assert.IsTrue(check.FlockCanAskTheStore, "The store answered the made-up purchase as unknown, so Flock's keys work.");
                Assert.IsNull(check.StoreProblem);
                Assert.IsFalse(check.AllGood);
                StringAssert.Contains("\"purchase_token\":\"flock-setup-check\"", transport.AllTo(FlockEndpoints.ShopValidateReceipt)[0].JsonBody, "Only a made-up purchase is sent.");
                Assert.IsFalse(store.Calls.Exists(call => call.StartsWith("buy:")), "Nothing is bought.");
            }
        }

        [Test]
        public void SetupCheck_TheStoreRefusingFlocksKeys_IsTheStoreProblem()
        {
            FakeStore store = new FakeStore();
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Status(422, CredentialsRejected))))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);

                FlockStoreSetupCheck check = h.Run(() => purchases.CheckSetupAsync());

                Assert.IsFalse(check.FlockCanAskTheStore);
                Assert.AreEqual(FlockErrorCode.PaymentProviderStoreCredentialsRejected, check.StoreProblem.ErrorCode);
                Assert.IsNotEmpty(check.StoreProblem.Hint);
                Assert.IsFalse(check.AllGood);
            }
        }

        [Test]
        public void SetupCheck_EveryProductFound_AndFlockCanAsk_IsAllGood()
        {
            FakeStore store = new FakeStore();
            store.Prices[Gems] = new FlockStorePrice("$1.99", 1.99m, "USD");
            string onlyGems = "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" +
                Item("gems", "currency_pack", "{\"google_play\":\"" + Gems + "\"}") + "]}],\"total\":1,\"page\":1,\"limit\":100}";
            using (FlockTestClient h = FlockTestClient.Create(Backend(FlockFakeTransport.Status(422, ReceiptInvalid), onlyGems)))
            {
                h.SetReachable(true);
                h.LoginAs("player-a");
                FlockStorePurchases purchases = Connect(h, store);

                Assert.IsTrue(h.Run(() => purchases.CheckSetupAsync()).AllGood);
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
                FlockStorePurchases purchases = Connect(h, store);

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
                FlockStorePurchases purchases = Connect(h, store);
                purchases.Dispose();

                Assert.Throws<ObjectDisposedException>(() => purchases.GetStorePrice("gems"));
                Assert.Throws<ObjectDisposedException>(() => h.Run(() => purchases.RestoreAsync()));
                Assert.Throws<ObjectDisposedException>(() => h.Run(() => purchases.CheckSetupAsync()));
                Assert.Throws<ObjectDisposedException>(() => h.Run(() => purchases.BuyAsync("gems")));
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
                FlockStorePurchases purchases = Connect(h, store);

                purchases.Dispose();
                store.Pay(Sword, "token-late");

                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopValidateReceipt));
                CollectionAssert.Contains(store.Calls, "stop");
            }
        }
    }
}
#endif
