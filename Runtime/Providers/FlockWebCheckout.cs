using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    // FlockStore's way to pay away from a phone's store: Shop.PurchaseAsync opens a checkout with the game's web gateway (Stripe, Tap or Moyasar),
    // opens its payment page and reads the purchase until it is paid, failed or expired; checkouts left open are found again at each sign-in.
    internal sealed class FlockWebCheckout : IDisposable
    {
        // One checkout being read until it ends, and the purchase call waiting for it, if one is.
        private sealed class Watch
        {
            public string PurchaseId;
            public string ShopItemId;
            public ShopItem Item;
            public FlockRepeatingCall Call;
            public TaskCompletionSource<PurchaseResult> Done;
            // Running since this client opened the checkout; null for one found in the player's list, which the server already holds.
            public System.Diagnostics.Stopwatch SinceOpened;
        }

        private static readonly TimeSpan DefaultCheckEvery = TimeSpan.FromSeconds(3);
        // The server saves a new checkout just after answering (0.36 s measured), so one opened here may read as not found for this long.
        internal static TimeSpan NewCheckoutSavedWithin = TimeSpan.FromSeconds(30);

        private readonly FlockStoreOptions _options;
        // FlockStore's PurchaseCompleted, raised once for every paid checkout; the item is null when it can no longer be read.
        private readonly Action<ShopItem, PurchaseResult> _paid;
        // The checkout being read, by purchase id: one read each, whoever started it.
        private readonly Dictionary<string, Watch> _watches = new Dictionary<string, Watch>();
        // Purchases this sign-in saw end, so a list of open checkouts answered before one ended does not read and report it again.
        private readonly HashSet<string> _ended = new HashSet<string>();
        // Items a purchase call is buying now, so a second call for one is refused rather than opening two checkouts.
        private readonly HashSet<string> _buying = new HashSet<string>();
        // The client this connection was made under: once another takes its place, the connection no longer acts.
        private readonly FlockClient _client;
        private bool _disposed;

        internal FlockWebCheckout(FlockClient client, FlockStoreOptions options, Action<ShopItem, PurchaseResult> paid)
        {
            _client = client;
            _options = options;
            _paid = paid;
        }

        // Listens to sign-ins from now on, and reads the signed-in player's checkouts still open until they end.
        internal void Start()
        {
            FlockEvents.OnAuthenticated += HandleSignedIn;
            FlockEvents.OnLoggedOut += EndWhatTheLastSignInStarted;
            FlockEvents.OnAuthExpired += EndWhatTheLastSignInStarted;
            FlockEvents.OnShutdown += Dispose;
            if (_client.IsAuthenticated)
                FindOpenCheckouts();
        }

        // Shop.PurchaseAsync's real-money path: opens the checkout, opens its payment page and waits for the payment. A checkout that ends
        // unpaid throws FlockStoreException; a cancelled wait leaves the checkout being read, and once paid it is reported through PurchaseCompleted.
        internal async Task<PurchaseResult> PurchaseAsync(ShopItem item, CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlockStore));
            if (!_client.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            int signIn = _client.SignInNumber;
            if (!_buying.Add(item.Id))
                throw new FlockValidationException("This item is already being bought") { Hint = "Wait for the first PurchaseAsync of it to end before starting another." };

            try
            {
                FlockCheckout checkout = await _client.Shop.StartCheckoutAsync(item.Id, _options.PaymentProvider, _options.ReturnUrl, _options.CancelUrl, cancellationToken);
                // Opened for a sign-in that ended while it was sent: its page is never shown, and the server closes it unpaid.
                if (_disposed || _client.SignInNumber != signIn)
                    throw new OperationCanceledException("The player signed out or changed while the checkout was opened, so its page was not shown");

                Watch watch = WatchCheckout(checkout.PurchaseId, item.Id, item, openedHere: true);
                watch.Done = watch.Done ?? new TaskCompletionSource<PurchaseResult>();
                Task<PurchaseResult> outcome = watch.Done.Task;
                try
                {
                    OpenPaymentPage(checkout.PaymentUrl);
                }
                catch
                {
                    // The game's opener failed: this call ends with its exception, and the checkout is still read in case the page opened.
                    if (watch.Done != null && watch.Done.Task == outcome)
                        watch.Done = null;
                    throw;
                }
                using (cancellationToken.Register(() => watch.Done.TrySetCanceled()))
                    return await outcome;
            }
            finally
            {
                _buying.Remove(item.Id);
            }
        }

        // Stops reading checkouts and listening to sign-ins; a purchase still waiting ends as cancelled. A checkout paid afterwards is still delivered by the server, and found again at the next connect.
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            FlockEvents.OnAuthenticated -= HandleSignedIn;
            FlockEvents.OnLoggedOut -= EndWhatTheLastSignInStarted;
            FlockEvents.OnAuthExpired -= EndWhatTheLastSignInStarted;
            FlockEvents.OnShutdown -= Dispose;
            foreach (Watch watch in new List<Watch>(_watches.Values))
            {
                watch.Call.Stop();
                watch.Done?.TrySetCanceled();
            }
            _watches.Clear();
        }

        private TimeSpan CheckEvery => _options.CheckEvery > TimeSpan.Zero ? _options.CheckEvery : DefaultCheckEvery;

        private void OpenPaymentPage(string paymentUrl)
        {
            if (_options.OpenPaymentPage != null)
                _options.OpenPaymentPage(paymentUrl);
            else
                Application.OpenURL(paymentUrl);
        }

        // One read per checkout: a purchase call and a sign-in's search for open checkouts that find the same one share it.
        private Watch WatchCheckout(string purchaseId, string shopItemId, ShopItem item, bool openedHere)
        {
            if (_watches.TryGetValue(purchaseId, out Watch watching) && watching.Call.IsRunning)
            {
                watching.Item = watching.Item ?? item;
                return watching;
            }

            Watch watch = new Watch { PurchaseId = purchaseId, ShopItemId = shopItemId, Item = item };
            if (openedHere)
                watch.SinceOpened = System.Diagnostics.Stopwatch.StartNew();
            _watches[purchaseId] = watch;
            watch.Call = _client.RepeatingCalls.Start<PlayerPurchase>("Web checkout " + purchaseId, CheckEvery,
                token => ReadAsync(watch, token),
                purchase => HandleRead(watch, purchase),
                failure => HandleStopped(watch, failure));
            return watch;
        }

        // Null while a checkout opened here is not saved yet, so it is read again.
        private async Task<PlayerPurchase> ReadAsync(Watch watch, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.Shop.GetPurchaseAsync(watch.PurchaseId, cancellationToken);
            }
            catch (FlockException notSavedYet) when (notSavedYet.ErrorCode == FlockErrorCode.ShopPurchaseNotFound
                                                      && watch.SinceOpened != null && watch.SinceOpened.Elapsed < NewCheckoutSavedWithin)
            {
                _client.Logger.LogDebug($"Web checkout {watch.PurchaseId} is not saved yet, reading it again");
                return null;
            }
        }

        private void HandleRead(Watch watch, PlayerPurchase purchase)
        {
            if (purchase == null || purchase.IsPending)
                return;
            watch.Call.Stop();
            Forget(watch);
            _ended.Add(watch.PurchaseId);
            if (purchase.IsPaid)
            {
                HandlePaid(watch, purchase);
                return;
            }

            string reason = purchase.Status == PurchaseResultStatuses.Failed ? FlockStoreFailureReasons.PaymentDeclined
                : purchase.Status == PurchaseResultStatuses.Expired ? FlockStoreFailureReasons.Expired
                : FlockStoreFailureReasons.Unknown;
            watch.Done?.TrySetException(new FlockStoreException(reason, $"The web checkout ended {purchase.Status}, so nothing was bought")
            {
                Hint = FixFor(reason),
                Operation = "Purchase shop item"
            });
        }

        // A read the server refused for good (another player's or an unknown purchase) ends the wait with its reason.
        private void HandleStopped(Watch watch, Exception failure)
        {
            Forget(watch);
            _ended.Add(watch.PurchaseId);
            watch.Done?.TrySetException(failure);
        }

        private void HandlePaid(Watch watch, PlayerPurchase purchase)
        {
            _client.Shop.ForgetPlayerDataAfterPayment();
            PurchaseResult result = new PurchaseResult
            {
                PurchaseId = purchase.Id,
                ItemType = purchase.ItemType,
                Status = purchase.Status,
                Granted = purchase.Granted ?? new List<ShopItemReward>()
            };
            watch.Done?.TrySetResult(result);
            if (watch.Item != null)
                _paid(watch.Item, result);
            else
                ReportFoundCheckoutPaid(watch.ShopItemId, result);
        }

        // A checkout found at a sign-in names its item by id alone: read it for the event, which says null when it can no longer be read.
        private async void ReportFoundCheckoutPaid(string shopItemId, PurchaseResult result)
        {
            ShopItem item = null;
            try
            {
                item = await _client.Shop.GetItemAsync(shopItemId);
            }
            catch (Exception notRead)
            {
                _client.Logger.LogWarning($"A web checkout was paid for an item that could not be read ({notRead.Message}); PurchaseCompleted carries no item.");
            }
            if (!_disposed)
                _paid(item, result);
        }

        private void Forget(Watch watch)
        {
            if (_watches.TryGetValue(watch.PurchaseId, out Watch current) && current == watch)
                _watches.Remove(watch.PurchaseId);
        }

        // A sign-in that ended takes its waits and reads with it: a wait ends as cancelled, and a checkout still open is found again at that player's next sign-in.
        private void EndWhatTheLastSignInStarted()
        {
            _ended.Clear();
            foreach (Watch watch in new List<Watch>(_watches.Values))
            {
                if (watch.Call.SignInNumber == _client.SignInNumber && watch.Call.IsRunning)
                    continue;
                watch.Call.Stop();
                Forget(watch);
                watch.Done?.TrySetException(new OperationCanceledException("The player signed out or changed while the checkout was waited for; once paid it is reported at their next sign-in"));
            }
        }

        private void HandleSignedIn(FlockAuthInfo signIn)
        {
            if (_disposed)
                return;
            EndWhatTheLastSignInStarted();
            FindOpenCheckouts();
        }

        // The player's checkouts still open, from an earlier launch, another device or a wait the game gave up on, are read until they end.
        internal async Task FindOpenCheckoutsAsync(CancellationToken cancellationToken = default)
        {
            int signIn = _client.SignInNumber;
            const int PageSize = 100;
            for (int page = 1; ; page++)
            {
                PaginatedResponse<PlayerPurchase> open = await _client.Shop.GetPurchasesAsync(PurchaseResultStatuses.Pending, page, PageSize, cancellationToken);
                if (_disposed || _client.SignInNumber != signIn)
                    return;
                foreach (PlayerPurchase purchase in open.Items)
                {
                    if (purchase != null && purchase.IsPending && IsWebGateway(purchase.PaymentProvider) && !_ended.Contains(purchase.Id))
                        WatchCheckout(purchase.Id, purchase.ShopItemId, null, openedHere: false);
                }
                if (open.Items.Length < PageSize || page * PageSize >= open.Total)
                    return;
            }
        }

        private async void FindOpenCheckouts()
        {
            try
            {
                await FindOpenCheckoutsAsync();
            }
            catch (OperationCanceledException)
            {
                // The player signed out or changed meanwhile; their next sign-in looks again.
            }
            catch (Exception notListed)
            {
                _client.Logger.LogWarning($"The player's open web checkouts could not be listed ({notListed.Message}); they are looked for again at the next sign-in or connect.");
            }
        }

        internal static bool IsWebGateway(string provider)
            => provider == PaymentProviders.Stripe || provider == PaymentProviders.Tap || provider == PaymentProviders.Moyasar;

        private static string FixFor(string reason)
        {
            switch (reason)
            {
                case FlockStoreFailureReasons.PaymentDeclined: return "Nothing was charged: the gateway declined the payment or the player gave up on it. Buy again to open a new checkout, where the player can pay another way.";
                case FlockStoreFailureReasons.Expired: return "Nothing was charged: the player did not pay within the hour the checkout stays open. Buy again to open a new one.";
                default: return "Nothing was delivered by this checkout; read the player's purchases with Shop.GetPurchasesAsync to see where it got to, and buy again if it was not paid.";
            }
        }
    }
}
