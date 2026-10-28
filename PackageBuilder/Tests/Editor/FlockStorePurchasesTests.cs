#if FLOCK_UNITY_IAP_5
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Providers;
using Flock.Tests.Support;
using NUnit.Framework;

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
            public bool RegisterWaitsForCancel;

            public event Action<FlockStoreOrder> OrderPaid;
            public event Action<FlockStoreOrder> OrderDeferred;
            public event Action<string, string, bool> OrderFailed;

            public Task ConnectAsync()
            {
                Calls.Add("connect");
                OnConnect?.Invoke();
                if (ConnectFails)
                    throw new InvalidOperationException("The store did not connect");
                return Task.CompletedTask;
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

            public void Pay(string productId, string transactionId) => OrderPaid?.Invoke(new FlockStoreOrder(productId, transactionId, null));
            public void Defer(string productId) => OrderDeferred?.Invoke(new FlockStoreOrder(productId, null, null));
            public void Fail(string productId, string reason, bool cancelled) => OrderFailed?.Invoke(productId, reason, cancelled);
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

        private static FlockFakeTransport Backend(FlockHttpResponse redemption)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
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
                store.OnBuy = productId => store.Fail(productId, "UserCancelled: closed the dialog", true);

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
                store.OnBuy = productId => store.Fail(productId, "PaymentDeclined: card refused", false);

                FlockStorePurchase purchase = h.Run(() => purchases.BuyAsync("gems"));

                Assert.AreEqual(FlockStorePurchaseOutcome.Failed, purchase.Outcome);
                Assert.AreEqual("PaymentDeclined: card refused", purchase.StoreFailure);
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
