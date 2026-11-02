using System;
using System.Collections;
using System.Collections.Generic;
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
    // Web checkout: the checkout opened once and never sent again after a lost answer, the purchase read until it ends, one PurchaseCompleted for every
    // paid checkout (the call's own, a cancelled wait's, one found at a sign-in), and every wait and read ended with the sign-in it began under.
    // The waits resume on the main thread, so these let frames go by and send the reads due each frame, as the game's frames do.
    public class FlockWebCheckoutTests
    {
        private const string PaymentPage = "https://checkout.stripe.com/c/pay/cs_test_a1";
        private const string Checkout = "{\"purchase_id\":\"pur-1\",\"provider\":\"stripe\",\"payment_url\":\"" + PaymentPage + "\",\"expires_at\":\"2026-10-31T13:00:00Z\"}";
        private const string Sword =
            "{\"id\":\"sword\",\"name\":\"Sword\",\"type\":\"standard\",\"price\":799,\"currency\":\"SAR\",\"is_real_money\":true,\"store_skus\":{},\"status\":\"active\"}";
        private const string GoldItem =
            "{\"id\":\"gold-item\",\"name\":\"GoldItem\",\"type\":\"standard\",\"price\":20,\"currency\":\"Gold\",\"is_real_money\":false,\"store_skus\":{},\"status\":\"active\"}";

        private static string Purchase(string id, string status, string granted = "[]")
            => "{\"id\":\"" + id + "\",\"shop_item_id\":\"sword\",\"item_type\":\"standard\",\"status\":\"" + status + "\",\"price_amount\":799,\"price_currency\":\"SAR\"," +
               "\"settlement_method\":\"external\",\"payment_provider\":\"stripe\",\"granted\":" + granted + ",\"player_inventory_id\":" + (status == "paid" ? "\"inv-1\"" : "null") + "," +
               "\"created_at\":\"2026-10-31T12:00:00Z\",\"settled_at\":" + (status == "paid" ? "\"2026-10-31T12:05:00Z\"" : "null") + ",\"refunded_at\":null,\"expires_at\":\"2026-10-31T13:00:00Z\"}";

        private const string Catalog = "{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" + Sword + "," + GoldItem + "]}],\"total\":1,\"page\":1,\"limit\":100}";

        private static string OpenList(params string[] purchases)
            => "{\"items\":[" + string.Join(",", purchases) + "],\"total\":" + purchases.Length + ",\"page\":1,\"limit\":100}";

        private static FlockFakeTransport Backend(params FlockHttpResponse[] reads)
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.ShopItemById("sword"), FlockFakeTransport.Ok(Sword));
            transport.On(FlockEndpoints.ShopItemById("gold-item"), FlockFakeTransport.Ok(GoldItem));
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
            transport.On(FlockEndpoints.ShopCheckout, FlockFakeTransport.Ok(Checkout));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList()));
            if (reads.Length > 0)
                transport.OnSequence(FlockEndpoints.ShopPurchaseById("pur-1"), reads);
            return transport;
        }

        private static void SignIn(FlockTestClient h, string playerId)
        {
            h.LoginAs(playerId);
            FlockEvents.InvokeAuthenticated(new FlockAuthInfo(playerId, FlockAuthMethod.Device));
        }

        // A web checkout through the game's one active gateway, as FlockStore.ConnectAsync connects one away from a phone.
        private static FlockStore Connect(FlockTestClient h, List<string> pagesOpened)
            => h.Run(() => FlockStore.ConnectAsync(null, null, new FlockStoreOptions { OpenPaymentPage = pagesOpened.Add, CheckEvery = TimeSpan.FromMilliseconds(1) }, CancellationToken.None));

        // Sends the reads due on each frame until done, as the game's frames do in a player.
        private static IEnumerator Frames(FlockTestClient h, Func<bool> done, string what, float seconds = 10f)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until)
            {
                h.Client.RepeatingCalls.SendThoseDue();
                yield return null;
            }
            Assert.IsTrue(done(), what);
        }

        private static IEnumerator SomeFrames(FlockTestClient h, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Thread.Sleep(2);
                h.Client.RepeatingCalls.SendThoseDue();
                yield return null;
            }
        }

        private static Exception FailureOf(Task task) => task.Exception?.GetBaseException();

        // The calls themselves.

        [Test]
        public void StartCheckout_SendsTheItemAndWhatIsSet_AndLeavesTheRestOut()
        {
            FlockFakeTransport transport = Backend();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                FlockCheckout checkout = h.Run(() => h.Client.Shop.StartCheckoutAsync("sword", PaymentProviders.Stripe, "https://game.example/done"));

                Assert.AreEqual("{\"shop_item_id\":\"sword\",\"provider\":\"stripe\",\"return_url\":\"https://game.example/done\"}", transport.LastTo(FlockEndpoints.ShopCheckout).JsonBody);
                Assert.AreEqual("pur-1", checkout.PurchaseId);
                Assert.AreEqual(PaymentPage, checkout.PaymentUrl);
                Assert.AreEqual(new DateTime(2026, 10, 31, 13, 0, 0, DateTimeKind.Utc), checkout.ExpiresAt.Value.ToUniversalTime());
            }
        }

        [TestCase(500)]
        [TestCase(0)]
        public void StartCheckout_WhoseAnswerNeverCame_IsNotSentAgain(int status)
        {
            FlockFakeTransport transport = Backend();
            transport.On(FlockEndpoints.ShopCheckout, status == 0 ? FlockFakeTransport.Timeout() : FlockFakeTransport.Status(500, "<html>Internal Server Error</html>"));
            using (FlockTestClient h = FlockTestClient.Create(transport, config => config.RetryPolicy = new RetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero }))
            {
                h.LoginAs("player-a");

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.StartCheckoutAsync("sword")));

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopCheckout), "A checkout is never sent again after an answer that did not come.");
            }
        }

        [Test]
        public void StartCheckout_AnAnswerWithNoPaymentPage_FailsTheCall()
        {
            FlockFakeTransport transport = Backend();
            transport.On(FlockEndpoints.ShopCheckout, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-1\",\"provider\":\"stripe\"}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                FlockSerializationException unread = Assert.Throws<FlockSerializationException>(() => h.Run(() => h.Client.Shop.StartCheckoutAsync("sword")));
                Assert.IsNotEmpty(unread.Hint);
            }
        }

        [Test]
        public void GetPurchase_ReadsThePurchase_AndGetPurchases_SendsTheStatus()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "paid", "[{\"type\":\"currency\",\"code\":\"Gold\",\"amount\":10}]")));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList(Purchase("pur-2", "pending"))));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                PlayerPurchase purchase = h.Run(() => h.Client.Shop.GetPurchaseAsync("pur-1"));
                PaginatedResponse<PlayerPurchase> open = h.Run(() => h.Client.Shop.GetPurchasesAsync(PurchaseResultStatuses.Pending, 2, 50));

                Assert.IsTrue(purchase.IsPaid);
                Assert.AreEqual(799, purchase.PriceAmount);
                Assert.AreEqual("inv-1", purchase.PlayerInventoryId);
                Assert.AreEqual(10, purchase.Granted[0].Amount);
                StringAssert.EndsWith("shop/purchases?page=2&limit=50&status=pending", transport.LastTo(FlockEndpoints.ShopPurchases + "?").Url);
                Assert.IsTrue(open.Items[0].IsPending);
            }
        }

        [Test]
        public void GetPurchases_ByAStatusTheServerDoesNotHave_ReadsAsItsOwnCode_WithItsFix()
        {
            FlockFakeTransport transport = Backend();
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Coded(422, "shop.invalid_purchase_status"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                FlockException refused = Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.GetPurchasesAsync("unpaid")));

                Assert.AreEqual(FlockErrorCode.ShopInvalidPurchaseStatus, refused.ErrorCode);
                StringAssert.Contains("PurchaseResultStatuses", refused.Hint);
            }
        }

        // Switched off in the dashboard: the cached shops stop showing it, as after a wallet purchase's refusal; another refusal keeps them.
        [TestCase("shop.item_not_for_sale", 409, 2)]
        [TestCase("shop.not_a_real_currency_item", 422, 1)]
        public void StartCheckout_RefusedAsNotForSale_DropsTheCachedShops(string code, int status, int shopReads)
        {
            FlockFakeTransport transport = Backend();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok("{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" + Sword + "]}],\"total\":1,\"page\":1,\"limit\":100}"));
            transport.On(FlockEndpoints.ShopCheckout, FlockFakeTransport.Coded(status, code));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                h.SetReachable(true);
                h.Run(() => h.Client.Shop.GetAllAsync());

                FlockException refused = Assert.Catch<FlockException>(() => h.Run(() => h.Client.Shop.StartCheckoutAsync("sword")));
                h.Run(() => h.Client.Shop.GetAllAsync());

                Assert.AreEqual(code, refused.Code);
                Assert.AreEqual(shopReads, transport.CountTo(FlockEndpoints.Shop + "?"));
            }
        }

        // The purchase call through web checkout.

        [UnityTest]
        public IEnumerator Buy_OpensThePaymentPage_ReadsUntilPaid_AndReportsItOnce()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "pending")),
                FlockFakeTransport.Ok(Purchase("pur-1", "paid", "[{\"type\":\"currency\",\"code\":\"Gold\",\"amount\":10}]")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                List<string> pages = new List<string>();
                FlockStore checkout = Connect(h, pages);
                List<ShopItem> completed = new List<ShopItem>();
                checkout.PurchaseCompleted += (item, result) => completed.Add(item);

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");

                Assert.IsFalse(buying.IsFaulted, FailureOf(buying)?.ToString());
                Assert.IsTrue(buying.Result.IsPaid);
                Assert.AreEqual(10, buying.Result.Granted[0].Amount);
                CollectionAssert.AreEqual(new[] { PaymentPage }, pages);
                Assert.AreEqual(3, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")), "Read until it ended, then no more.");
                yield return SomeFrames(h, 5);
                Assert.AreEqual(3, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")));
                Assert.AreEqual(1, completed.Count, "Reported once, the call's own included.");
                Assert.AreEqual("sword", completed[0].Id);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopTransaction), "A real-money item never goes to the wallet.");
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Buy_Paid_DropsTheCachedWallet_SoTheNextReadAsksTheServer()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            transport.On(FlockEndpoints.PlayerTemplate, FlockFakeTransport.Ok(
                "{\"error\":{\"code\":null},\"response\":{\"message\":null,\"code\":null},\"result\":[{\"id\":\"tpl-wallet\",\"name\":\"currencies\",\"schema\":[],\"data\":[],\"tag\":\"currency\"}]}"));
            transport.OnSequence("player_data?page=", WalletPage(0), WalletPage(100));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                Assert.AreEqual(0, h.Run(() => h.Client.Player.GetWalletAsync()).BalanceOf("Gems"));
                FlockStore checkout = Connect(h, new List<string>());

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");

                Assert.AreEqual(100, h.Run(() => h.Client.Player.GetWalletAsync()).BalanceOf("Gems"), "The server's balance, not the cached 0.");
                checkout.Dispose();
            }
        }

        private static FlockHttpResponse WalletPage(int gems)
            => FlockFakeTransport.Ok("{\"items\":[{\"id\":\"pd-wallet\",\"player_template_id\":\"tpl-wallet\",\"player_id\":\"player-a\",\"data\":[{\"type\":\"object\",\"value\":[" +
                "{\"type\":\"integer\",\"value\":" + gems + ",\"type_name\":\"currency\",\"field_name\":\"Gems\"}],\"type_name\":\"wallet\",\"field_name\":\"game_currencies\"}]}],\"total\":1,\"page\":1,\"limit\":100}");

        [UnityTest]
        public IEnumerator Buy_ACheckoutThatEndsUnpaid_ThrowsWhy_AndReportsNothing(
            [Values(PurchaseResultStatuses.Failed, PurchaseResultStatuses.Expired)] string status)
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", status)));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());
                int completed = 0;
                checkout.PurchaseCompleted += (item, result) => completed++;

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");

                FlockStoreException notTaken = FailureOf(buying) as FlockStoreException;
                Assert.IsNotNull(notTaken, FailureOf(buying)?.ToString());
                Assert.AreEqual(status == PurchaseResultStatuses.Failed ? FlockStoreFailureReasons.PaymentDeclined : FlockStoreFailureReasons.Expired, notTaken.Reason);
                StringAssert.Contains("Nothing was charged", notTaken.Hint);
                Assert.AreEqual(0, completed);
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator SigningOutWhileWaiting_EndsTheWait_AndNothingMoreIsRead()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")) >= 1, "The checkout was read");

                h.Client.Authentication.Logout();
                int readsAtSignOut = transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1"));
                yield return Frames(h, () => buying.IsCompleted, "The wait ended with the sign-in");
                yield return SomeFrames(h, 10);

                Assert.IsTrue(buying.IsCanceled, "Ended as cancelled: " + buying.Status);
                Assert.AreEqual(readsAtSignOut, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")), "Nothing is read for a sign-in that ended.");
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator TheCheckoutsAnswerAfterSignOut_OpensNoPage()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                List<string> pages = new List<string>();
                FlockStore checkout = Connect(h, pages);
                transport.GateNext(FlockEndpoints.ShopCheckout);
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopCheckout) == 1, "The checkout was sent");

                h.Client.Authentication.Logout();
                transport.ReleaseGate();
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");
                yield return SomeFrames(h, 5);

                Assert.IsTrue(buying.IsCanceled, "Ended as cancelled: " + buying.Status);
                Assert.AreEqual(0, pages.Count, "No page is opened for a player who signed out.");
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")));
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator CancellingTheWait_LeavesTheCheckoutRead_AndItIsReportedOncePaid()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "pending")),
                FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());
                List<ShopItem> completed = new List<ShopItem>();
                checkout.PurchaseCompleted += (item, result) => completed.Add(item);
                CancellationTokenSource giveUp = new CancellationTokenSource();
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword", giveUp.Token);
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")) >= 1, "The checkout was read");

                giveUp.Cancel();
                yield return Frames(h, () => buying.IsCompleted, "The wait ended");
                Assert.IsTrue(buying.IsCanceled);
                yield return Frames(h, () => completed.Count == 1, "The checkout paid later is reported");

                Assert.AreEqual("sword", completed[0].Id);
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ACheckoutFoundAtSignIn_IsReadUntilPaid_AndReportedWithItsItem()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList(Purchase("pur-1", "pending"))));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockStore checkout = Connect(h, new List<string>());
                List<ShopItem> completed = new List<ShopItem>();
                checkout.PurchaseCompleted += (item, result) => completed.Add(item);

                SignIn(h, "player-a");
                yield return Frames(h, () => completed.Count == 1, "The checkout from an earlier launch is reported once paid");

                StringAssert.Contains("status=pending", transport.LastTo(FlockEndpoints.ShopPurchases + "?").Url);
                Assert.AreEqual("sword", completed[0]?.Id, "Its item, read by id.");
                yield return SomeFrames(h, 5);
                Assert.AreEqual(1, completed.Count);
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator TheCallsOwnCheckout_FoundAgainAtConnect_IsReadOnce_AndReportedOnce()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList(Purchase("pur-1", "pending"))));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                transport.GateNext(FlockEndpoints.ShopPurchases + "?");
                FlockStore checkout = Connect(h, new List<string>());
                int completed = 0;
                checkout.PurchaseCompleted += (item, result) => completed++;
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopCheckout) == 1, "The checkout was opened");

                transport.ReleaseGate();
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");
                yield return SomeFrames(h, 5);

                Assert.IsTrue(buying.Result.IsPaid);
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")), "One read at a time for one checkout.");
                Assert.AreEqual(1, completed);
                CollectionAssert.IsEmpty(h.Logger.Warnings, "The sign-in's search shared the call's read rather than failing to start a second.");
                checkout.Dispose();
            }
        }

        // A list of open checkouts answered while one was still pending, handed over after it was paid, must not read and report it again.
        [UnityTest]
        public IEnumerator AStaleListOfOpenCheckouts_AfterOneWasPaid_DoesNotReportItAgain()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList(Purchase("pur-1", "pending"))));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                transport.GateNext(FlockEndpoints.ShopPurchases + "?");
                FlockStore checkout = Connect(h, new List<string>());
                int completed = 0;
                checkout.PurchaseCompleted += (item, result) => completed++;
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "Paid while the list's answer was held");

                transport.ReleaseGate();
                yield return SomeFrames(h, 10);

                Assert.IsTrue(buying.Result.IsPaid);
                Assert.AreEqual(2, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")), "Not read again for the stale list.");
                Assert.AreEqual(1, completed, "Reported once.");
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ShuttingFlockDown_EndsTheWait()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")));
            FlockTestClient h = FlockTestClient.Create(transport);
            try
            {
                h.LoginAs("player-a");
                Connect(h, new List<string>());
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")) >= 1, "The checkout was read");

                FlockClient.Shutdown();
                yield return FlockTestWait.Until(() => buying.IsCompleted, "The wait ended with Flock");

                Assert.IsTrue(buying.IsCanceled);
            }
            finally
            {
                h.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator APlayerSwitchWhileTheItemIsRead_BuysNothing()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());
                transport.GateNext(FlockEndpoints.ShopItemById("sword"));
                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopItemById("sword")) == 1, "The item read was sent");

                SignIn(h, "player-b");
                transport.ReleaseGate();
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");

                Assert.IsTrue(buying.IsCanceled, "Ended as cancelled: " + buying.Status);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopCheckout), "Nothing is opened for a player who did not ask.");
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator AnOpenerThatThrows_EndsTheCall_AndTheCheckoutIsStillReportedOncePaid()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = h.Run(() => FlockStore.ConnectAsync(null, null, new FlockStoreOptions
                {
                    OpenPaymentPage = _ => throw new InvalidOperationException("no browser"),
                    CheckEvery = TimeSpan.FromMilliseconds(1)
                }, CancellationToken.None));
                int completed = 0;
                checkout.PurchaseCompleted += (item, result) => completed++;

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted && completed == 1, "The call ended and the checkout was reported once paid");

                Assert.IsInstanceOf<InvalidOperationException>(FailureOf(buying));
                yield return SomeFrames(h, 5);
                Assert.AreEqual(1, completed);
                checkout.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator AReadTheServerRefusesForGood_EndsTheWaitWithItsCode()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Coded(404, "shop.purchase_not_found"));
            TimeSpan savedWithin = FlockWebCheckout.NewCheckoutSavedWithin;
            // Past the time a new checkout takes to be saved, so not found is the server's last word.
            FlockWebCheckout.NewCheckoutSavedWithin = TimeSpan.Zero;
            try
            {
                using (FlockTestClient h = FlockTestClient.Create(transport))
                {
                    h.LoginAs("player-a");
                    FlockStore checkout = Connect(h, new List<string>());

                    Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                    yield return Frames(h, () => buying.IsCompleted, "The wait ended");
                    yield return SomeFrames(h, 5);

                    Assert.AreEqual(FlockErrorCode.ShopPurchaseNotFound, (FailureOf(buying) as FlockException)?.ErrorCode);
                    Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")), "Not read again after the server's refusal.");
                    checkout.Dispose();
                }
            }
            finally
            {
                FlockWebCheckout.NewCheckoutSavedWithin = savedWithin;
            }
        }

        // The server saves a new checkout just after answering (measured live 2026-10-31), so its first read can answer not found.
        [UnityTest]
        public IEnumerator ACheckoutJustOpened_ReadAsNotFound_IsReadAgain_AndEndsPaid()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Coded(404, "shop.purchase_not_found"), FlockFakeTransport.Coded(404, "shop.purchase_not_found"),
                FlockFakeTransport.Ok(Purchase("pur-1", "pending")), FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());
                List<ShopItem> completed = new List<ShopItem>();
                checkout.PurchaseCompleted += (item, result) => completed.Add(item);

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "The purchase ended");

                Assert.IsFalse(buying.IsFaulted, FailureOf(buying)?.ToString());
                Assert.IsTrue(buying.Result.IsPaid);
                Assert.AreEqual(4, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")));
                Assert.AreEqual(1, completed.Count);
                checkout.Dispose();
            }
        }

        // One listed as open is already saved, so not found there is the server's last word.
        [UnityTest]
        public IEnumerator ACheckoutFoundAtSignIn_ReadAsNotFound_IsNotReadAgain()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Coded(404, "shop.purchase_not_found"), FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList(Purchase("pur-1", "pending"))));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockStore checkout = Connect(h, new List<string>());
                List<ShopItem> completed = new List<ShopItem>();
                checkout.PurchaseCompleted += (item, result) => completed.Add(item);

                SignIn(h, "player-a");
                yield return Frames(h, () => transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")) == 1, "The listed checkout was read");
                yield return SomeFrames(h, 10);

                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopPurchaseById("pur-1")), "Not read again after not found.");
                Assert.AreEqual(0, completed.Count);
                checkout.Dispose();
            }
        }

        // The token refresh failing ends the sign-in, which takes its waits with it.
        [UnityTest]
        public IEnumerator ASignInThatExpires_EndsTheWait()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Status(401, "{\"detail\":{\"code\":\"auth.token_expired\",\"message\":\"Token expired\"}}"));
            transport.On(FlockEndpoints.PlayerTokenRefresh, FlockFakeTransport.Status(401, "{\"detail\":{\"code\":\"auth.invalid_refresh_token\",\"message\":\"Invalid refresh token\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "The wait ended with the sign-in");

                Assert.IsFalse(h.Client.IsAuthenticated, "Precondition: the refresh failed and the sign-in ended");
                Assert.IsTrue(buying.IsCanceled, "Ended as cancelled: " + buying.Status);
                checkout.Dispose();
            }
        }

        // The connection.

        // The gateway the connection was made with is the one each checkout is opened with, whatever the game's options say afterwards.
        [UnityTest]
        public IEnumerator AGatewayNamed_IsTheOneEachCheckoutIsOpenedWith()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStoreOptions options = new FlockStoreOptions { OpenPaymentPage = _ => { }, CheckEvery = TimeSpan.FromMilliseconds(1) };
                FlockStore checkout = h.Run(() => FlockStore.ConnectAsync(null, PaymentProviders.Tap, options, CancellationToken.None));
                options.PaymentProvider = PaymentProviders.GooglePlay;

                Task<PurchaseResult> buying = h.Client.Shop.PurchaseAsync("sword");
                yield return Frames(h, () => buying.IsCompleted, "Paid through the gateway named");

                Assert.AreEqual(PaymentProviders.Tap, checkout.PaymentProvider);
                Assert.IsTrue(checkout.IsWebCheckout);
                Assert.IsTrue(buying.Result.IsPaid);
                StringAssert.Contains("\"provider\":\"tap\"", transport.LastTo(FlockEndpoints.ShopCheckout).JsonBody);
                checkout.Dispose();
            }
        }

        // A web checkout charges the item's own price, kept in the currency's smallest unit as Flock's server counts it.
        [TestCase("SAR", 799, "7.99", 2)]
        [TestCase("sar", 199, "1.99", 2)]
        [TestCase("KWD", 1250, "1.250", 3)]
        [TestCase("JPY", 500, "500", 0)]
        public void GetPrice_IsTheItemsOwnPrice_InTheCurrencysSmallestUnit(string currency, int price, string amount, int digits)
        {
            FlockFakeTransport transport = Backend();
            string item = "{\"id\":\"sword\",\"name\":\"Sword\",\"type\":\"standard\",\"price\":" + price + ",\"currency\":\"" + currency + "\",\"is_real_money\":true,\"store_skus\":{},\"status\":\"active\"}";
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok("{\"items\":[{\"id\":\"shop-1\",\"name\":\"main\",\"shop_items\":[" + item + "]}],\"total\":1,\"page\":1,\"limit\":100}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockStore checkout = Connect(h, new List<string>());

                FlockStorePrice shown = checkout.GetPrice("sword");

                decimal expected = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
                Assert.AreEqual(expected, shown.Amount);
                Assert.AreEqual(currency.ToUpperInvariant(), shown.CurrencyCode);
                Assert.AreEqual(currency.ToUpperInvariant() + " " + expected.ToString("N" + digits, System.Globalization.CultureInfo.CurrentCulture), shown.Text);
                checkout.Dispose();
            }
        }

        [TestCase("gold-item", "game currency")]
        [TestCase("shield", "not in the game's shops")]
        public void GetPrice_OfAnItemWithNoRealMoneyPrice_IsRefused(string shopItemId, string why)
        {
            using (FlockTestClient h = FlockTestClient.Create(Backend()))
            {
                FlockStore checkout = Connect(h, new List<string>());

                FlockValidationException refused = Assert.Throws<FlockValidationException>(() => checkout.GetPrice(shopItemId));

                StringAssert.Contains(why, refused.Message);
                Assert.IsNotEmpty(refused.Hint);
                checkout.Dispose();
            }
        }

        // Flock already holds what a web checkout bought: a restore reads the player's checkouts still open again, and reports each one paid.
        [UnityTest]
        public IEnumerator Restore_ReadsTheOpenCheckoutsAgain_AndReportsOnePaid()
        {
            FlockFakeTransport transport = Backend(FlockFakeTransport.Ok(Purchase("pur-1", "paid")));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());
                int completed = 0;
                checkout.PurchaseCompleted += (item, result) => completed++;
                yield return SomeFrames(h, 3);
                int listedAtConnect = transport.CountTo(FlockEndpoints.ShopPurchases + "?");
                transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Ok(OpenList(Purchase("pur-1", "pending"))));

                FlockStoreRestore restore = h.Run(() => checkout.RestoreAsync());
                yield return Frames(h, () => completed == 1, "The checkout the restore found was reported once paid");

                Assert.AreEqual(listedAtConnect + 1, transport.CountTo(FlockEndpoints.ShopPurchases + "?"));
                Assert.IsEmpty(restore.Restored);
                Assert.IsEmpty(restore.NotDelivered);
                checkout.Dispose();
            }
        }

        [Test]
        public void Restore_WhenTheOpenCheckoutsCannotBeListed_ThrowsWhy()
        {
            FlockFakeTransport transport = Backend();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                FlockStore checkout = Connect(h, new List<string>());
                h.LoginAs("player-a");
                transport.On(FlockEndpoints.ShopPurchases + "?", FlockFakeTransport.Coded(422, "shop.invalid_purchase_status"));

                FlockException refused = Assert.Catch<FlockException>(() => h.Run(() => checkout.RestoreAsync()));

                Assert.AreEqual(FlockErrorCode.ShopInvalidPurchaseStatus, refused.ErrorCode);
                checkout.Dispose();
            }
        }

        // Only a checkout shows whether a gateway is connected, so the check says so rather than ready, and opens none.
        [Test]
        public void CheckSetup_OnAWebCheckout_SaysHowToCheckTheGateway_AndOpensNoCheckout()
        {
            FlockFakeTransport transport = Backend();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());

                FlockStoreSetupCheck check = h.Run(() => checkout.CheckSetupAsync());

                StringAssert.Contains("test card", check.NotChecked);
                Assert.IsFalse(check.IsReady);
                Assert.IsNull(check.ServerProblem);
                Assert.IsEmpty(check.MissingProductId);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopCheckout));
                checkout.Dispose();
            }
        }

        // A connect that could not read the shops leaves nothing behind: no way to pay, no listening to sign-ins, and its place free.
        [Test]
        public void AConnectWhoseShopsCannotBeRead_IsRefused_AndListensToNothing()
        {
            FlockFakeTransport transport = Backend();
            transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Coded(404, "shop.shop_not_found"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Catch<FlockException>(() => Connect(h, new List<string>()));
                SignIn(h, "player-a");

                Assert.IsNull(h.Client.Shop.ConnectedStore);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopPurchases + "?"), "No checkouts were looked for at the sign-in.");
                transport.On(FlockEndpoints.Shop + "?", FlockFakeTransport.Ok(Catalog));
                Connect(h, new List<string>()).Dispose();
            }
        }

        [Test]
        public void Connecting_AsSecondWayToPay_IsRefused_AndDisposing_LetsAnotherConnect()
        {
            using (FlockTestClient h = FlockTestClient.Create(Backend()))
            {
                FlockStore first = Connect(h, new List<string>());

                FlockValidationException twice = Assert.Throws<FlockValidationException>(() => Connect(h, new List<string>()));
                StringAssert.Contains("FlockStore", twice.Hint);
                Assert.AreSame(first, h.Client.Shop.ConnectedStore, "The first connection stays the way to pay.");

                first.Dispose();
                Assert.IsNull(h.Client.Shop.ConnectedStore);
                Connect(h, new List<string>()).Dispose();
            }
        }

        [Test]
        public void Disposed_TheRealMoneyPurchaseIsRefused_NamingTheConnect()
        {
            FlockFakeTransport transport = Backend();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                Connect(h, new List<string>()).Dispose();

                FlockValidationException refused = Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Shop.PurchaseAsync("sword")));

                StringAssert.Contains("FlockStore.ConnectAsync", refused.Hint);
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopCheckout));
            }
        }

        [Test]
        public void AWalletItem_IsBoughtFromTheWallet_WithACheckoutConnected()
        {
            FlockFakeTransport transport = Backend();
            transport.On(FlockEndpoints.ShopTransaction, FlockFakeTransport.Ok("{\"purchase_id\":\"pur-w\",\"item_type\":\"standard\",\"status\":\"paid\",\"granted\":[]}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                FlockStore checkout = Connect(h, new List<string>());

                PurchaseResult bought = h.Run(() => h.Client.Shop.PurchaseAsync("gold-item"));

                Assert.IsTrue(bought.IsPaid);
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.ShopTransaction));
                Assert.AreEqual(0, transport.CountTo(FlockEndpoints.ShopCheckout));
                checkout.Dispose();
            }
        }
    }
}
