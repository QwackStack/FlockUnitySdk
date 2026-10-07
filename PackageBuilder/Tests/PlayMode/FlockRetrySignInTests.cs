using System;
using System.Collections;
using System.Threading.Tasks;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // A call that acts as the signed-in player goes out as that sign-in or not at all: a retry waiting when another player signs
    // in (or this one signs out) is cancelled, never sent with the next player's token. Calls that need only the API key retry.
    public class FlockRetrySignInTests
    {
        private const string UnreadCount = "notification/unread_count";
        private const string Board = "leaderboard/by-name/weekly";
        private const string ShopItem = "shop_item/item-1";
        private const string Purchase = "shop/transaction";
        private const string Transactions = "analytics/transactions";

        private FlockTestClient _h;
        private FlockFakeTransport _transport;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            _transport = new FlockFakeTransport();
            _h = FlockTestClient.Create(_transport, config =>
                config.RetryPolicy = new RetryPolicy { MaxRetries = 1, InitialDelay = TimeSpan.FromMilliseconds(50), UseJitter = false });
            _h.LoginAs("player-a");
        }

        [TearDown]
        public void TearDown()
        {
            _transport.ReleaseGate();
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private static IEnumerator Done(Task task, string what) => FlockTestWait.Until(() => task.IsCompleted, what);

        private static FlockHttpResponse RateLimited() => FlockFakeTransport.Coded(429, "request.rate_limited");

        [UnityTest]
        public IEnumerator ARetryOfACallThatActsForThePlayer_IsCancelledWhenAnotherPlayerSignsIn()
        {
            _transport.OnSequence(UnreadCount, RateLimited(), FlockFakeTransport.Ok("{\"result\":{\"count\":3}}"));
            _transport.GateNext(UnreadCount);
            Task<int> unread = _h.Client.Notification.GetUnreadCountAsync();
            yield return FlockTestWait.Until(() => _transport.CountTo(UnreadCount) == 1, "The first try is on its way");

            _h.LoginAs("player-b");
            _transport.ReleaseGate();
            yield return Done(unread, "the call gave up");

            Assert.IsTrue(unread.IsCanceled, "Cancelled, not sent as player-b");
            Assert.AreEqual(1, _transport.CountTo(UnreadCount));
        }

        [UnityTest]
        public IEnumerator ARetryOfACallThatActsForThePlayer_IsCancelledWhenThePlayerSignsOut()
        {
            _transport.OnSequence(UnreadCount, RateLimited(), FlockFakeTransport.Ok("{\"result\":{\"count\":3}}"));
            _transport.GateNext(UnreadCount);
            Task<int> unread = _h.Client.Notification.GetUnreadCountAsync();
            yield return FlockTestWait.Until(() => _transport.CountTo(UnreadCount) == 1, "The first try is on its way");

            _h.Client.Authentication.Logout();
            _transport.ReleaseGate();
            yield return Done(unread, "the call gave up");

            Assert.IsTrue(unread.IsCanceled);
            Assert.AreEqual(1, _transport.CountTo(UnreadCount));
        }

        [UnityTest]
        public IEnumerator ARetryOfACallThatActsForThePlayer_GoesOutWhileTheSignInLasts()
        {
            _transport.OnSequence(UnreadCount, RateLimited(), FlockFakeTransport.Ok("{\"result\":{\"count\":3}}"));
            _transport.GateNext(UnreadCount);
            Task<int> unread = _h.Client.Notification.GetUnreadCountAsync();
            yield return FlockTestWait.Until(() => _transport.CountTo(UnreadCount) == 1, "The first try is on its way");

            _transport.ReleaseGate();
            yield return Done(unread, "the call finished");

            Assert.AreEqual(3, unread.Result, "Retried and answered");
            Assert.AreEqual(2, _transport.CountTo(UnreadCount));
        }

        [UnityTest]
        public IEnumerator ACallThatNeedsOnlyTheApiKey_StillRetriesWhenAnotherPlayerSignsIn()
        {
            _transport.OnSequence(Board, RateLimited(), FlockFakeTransport.Ok("{\"result\":{\"id\":\"lb-1\",\"name\":\"weekly\"}}"));
            _transport.GateNext(Board);
            Task<Leaderboard> board = _h.Client.Leaderboard.GetByNameAsync("weekly");
            yield return FlockTestWait.Until(() => _transport.CountTo(Board) == 1, "The first try is on its way");

            _h.LoginAs("player-b");
            _transport.ReleaseGate();
            yield return Done(board, "the read finished");

            Assert.IsFalse(board.IsCanceled || board.IsFaulted, "A read every player gets the same answer to is not called off");
            Assert.AreEqual("lb-1", board.Result.Id);
            Assert.AreEqual(2, _transport.CountTo(Board));
        }

        [UnityTest]
        public IEnumerator APurchaseWhoseSignInEndedBeforeItsRequest_IsNeverSent_AndRecordsNothingForTheNextPlayer()
        {
            _transport.On(ShopItem, FlockFakeTransport.Ok("{\"id\":\"item-1\",\"price\":10,\"currency\":\"coins\"}"));
            _transport.On(Purchase, FlockFakeTransport.Ok("{}"));
            _transport.On(Transactions, FlockFakeTransport.Ok("{}"));
            _transport.GateNext(ShopItem);
            Task<PurchaseResult> purchase = _h.Client.Shop.PurchaseAsync("item-1");
            yield return FlockTestWait.Until(() => _transport.CountTo(ShopItem) == 1, "The item read is on its way");

            _h.LoginAs("player-b");
            _transport.ReleaseGate();
            yield return Done(purchase, "the purchase gave up");

            Assert.IsTrue(purchase.IsCanceled);
            Assert.AreEqual(0, _transport.CountTo(Purchase), "Never sent with player-b's token");
            Assert.AreEqual(0, _transport.CountTo(Transactions), "No purchase credited to player-b");
        }

        [UnityTest]
        public IEnumerator APurchaseRetriedAfterAPlayerSwitch_IsCancelled_AndRecordsNoFailureForTheNextPlayer()
        {
            _transport.On(ShopItem, FlockFakeTransport.Ok("{\"id\":\"item-1\",\"price\":10,\"currency\":\"coins\"}"));
            _transport.OnSequence(Purchase, RateLimited(), FlockFakeTransport.Ok("{}"));
            _transport.On(Transactions, FlockFakeTransport.Ok("{}"));
            _transport.GateNext(Purchase);
            Task<PurchaseResult> purchase = _h.Client.Shop.PurchaseAsync("item-1");
            yield return FlockTestWait.Until(() => _transport.CountTo(Purchase) == 1, "The purchase's first try is on its way");

            _h.LoginAs("player-b");
            _transport.ReleaseGate();
            yield return Done(purchase, "the purchase gave up");

            Assert.IsTrue(purchase.IsCanceled);
            Assert.AreEqual(1, _transport.CountTo(Purchase));
            Assert.AreEqual(1, _transport.CountTo(Transactions), "Only the start, recorded while player-a was signed in");
        }
    }
}
