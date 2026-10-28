using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>How a store purchase of a Flock shop item ended.</summary>
    public enum FlockStorePurchaseOutcome
    {
        /// <summary>The store charged the player, Flock delivered the item, and the store's purchase was finished.</summary>
        Delivered,
        /// <summary>The store is waiting for the payment or for a parent's approval ("Ask to Buy"); <see cref="FlockStorePurchases.PurchaseDelivered"/> reports it once paid.</summary>
        Pending,
        /// <summary>The player cancelled in the store's dialog; nothing was charged.</summary>
        Cancelled,
        /// <summary>The store did not take the payment (declined, the product unavailable, the store out of reach); nothing was charged. <see cref="FlockStorePurchase.StoreFailure"/> says why.</summary>
        Failed
    }

    /// <summary>A store purchase of a Flock shop item, and how it ended.</summary>
    public sealed class FlockStorePurchase
    {
        public FlockStorePurchaseOutcome Outcome { get; }

        /// <summary>The shop item bought.</summary>
        public ShopItem Item { get; }

        /// <summary>What Flock delivered; null unless the outcome is Delivered.</summary>
        public PurchaseResult Delivery { get; }

        /// <summary>The store's own reason when the outcome is Failed; null otherwise.</summary>
        public string StoreFailure { get; }

        internal FlockStorePurchase(FlockStorePurchaseOutcome outcome, ShopItem item, PurchaseResult delivery, string storeFailure)
        {
            Outcome = outcome;
            Item = item;
            Delivery = delivery;
            StoreFailure = storeFailure;
        }
    }

    /// <summary>A store purchase the player paid for that Flock did not deliver. It stays unfinished in the store, which hands it back at the next launch to be sent again.</summary>
    public sealed class FlockStorePurchaseProblem
    {
        /// <summary>The shop item, or null when two shop items sell the store's product.</summary>
        public ShopItem Item { get; }

        /// <summary>The store's product id.</summary>
        public string ProductId { get; }

        /// <summary>Why Flock did not deliver: its ErrorCode and Hint say what to do.</summary>
        public FlockException Error { get; }

        internal FlockStorePurchaseProblem(ShopItem item, string productId, FlockException error)
        {
            Item = item;
            ProductId = productId;
            Error = error;
        }
    }

    /// <summary>Sells Flock shop items for real money through Unity IAP 5: the store charges the player, Flock checks the purchase with the store and delivers the item, and the store's purchase is finished only once Flock delivered. A purchase Flock refused stays unfinished, so the store hands it back at the next launch, where it is sent again once the player is signed in.</summary>
    public sealed class FlockStorePurchases : IDisposable
    {
        /// <summary>A purchase delivered outside a <see cref="BuyAsync"/> call: one that was pending and is now paid, or one left unfinished at an earlier launch. Raised on the main thread.</summary>
        public event Action<FlockStorePurchase> PurchaseDelivered;

        /// <summary>A purchase the player paid for that Flock did not deliver, outside a <see cref="BuyAsync"/> call. Raised on the main thread.</summary>
        public event Action<FlockStorePurchaseProblem> PurchaseNeedsAttention;

        /// <summary>The store this connection buys from: <see cref="PaymentProviders.AppleAppStore"/> or <see cref="PaymentProviders.GooglePlay"/>.</summary>
        public string Store { get; }

        private sealed class Buying
        {
            public ShopItem Item;
            public readonly TaskCompletionSource<FlockStorePurchase> Done = new TaskCompletionSource<FlockStorePurchase>();
        }

        private readonly IFlockStoreConnection _connection;
        private readonly Dictionary<string, ShopItem> _itemsById = new Dictionary<string, ShopItem>();
        private readonly Dictionary<string, ShopItem> _itemByProduct = new Dictionary<string, ShopItem>();
        private readonly HashSet<string> _productsSoldTwice = new HashSet<string>();
        private readonly Dictionary<string, Buying> _buying = new Dictionary<string, Buying>();
        // A store's unfinished purchase is sent once a launch (payment plan D-P3); the next launch sends it again.
        private readonly HashSet<string> _sentThisLaunch = new HashSet<string>();
        // Purchases the store handed over before the shops were read or anyone signed in; sent once both are true.
        private readonly List<FlockStoreOrder> _waiting = new List<FlockStoreOrder>();
        private bool _ready;
        private bool _disposed;
        // The client this connection was made under: once another takes its place, the connection no longer acts.
        private FlockClient _client;
        // One connection a client: two would each send every purchase and raise every event.
        private static FlockStorePurchases _connected;

        private FlockStorePurchases(IFlockStoreConnection connection, string store)
        {
            _connection = connection;
            Store = store;
        }

        /// <summary>Connects Unity IAP's store, reads the game's shops and registers each item that has a product id for this store (a currency pack or consumable as a consumable product, a standard item as a one-time product). Once a player is signed in, purchases left unfinished are sent to Flock. <paramref name="store"/> is found from the platform on a phone; in the editor, where Unity IAP's fake store answers, name the store to test with. Connect once and keep the result: a second connect while it is connected is refused, so Dispose it before connecting again. A store that never answers is waited for until <paramref name="cancellationToken"/> is cancelled.</summary>
        public static Task<FlockStorePurchases> ConnectAsync(string store = null, CancellationToken cancellationToken = default)
        {
            string chosen = store ?? StoreOfThisPlatform();
            return ConnectAsync(new FlockUnityIapConnection(), chosen, cancellationToken);
        }

        internal static async Task<FlockStorePurchases> ConnectAsync(IFlockStoreConnection connection, string store, CancellationToken cancellationToken)
        {
            FlockStorePurchases purchases = new FlockStorePurchases(connection, store);
            try
            {
                if (store != PaymentProviders.AppleAppStore && store != PaymentProviders.GooglePlay)
                    throw new FlockValidationException($"'{store}' is not a store Unity IAP buys from") { Hint = "Pass PaymentProviders.AppleAppStore or PaymentProviders.GooglePlay." };
                purchases._client = FlockClient.Instance;
                if (_connected != null && _connected._client == purchases._client)
                    throw new FlockValidationException("The store is already connected") { Hint = "Keep the FlockStorePurchases that ConnectAsync returned and use it everywhere, or Dispose it before connecting again." };
                _connected = purchases;
                // Listening first: a store hands back unfinished purchases as it connects, and those are held until the shops are read.
                purchases.ListenToTheStore();
                await connection.ConnectAsync();
                List<ShopItem> catalog = await ReadCatalogAsync(cancellationToken);
                await connection.RegisterProductsAsync(purchases.LearnProducts(catalog), cancellationToken);
            }
            catch
            {
                purchases.Dispose();
                throw;
            }
            purchases.BeReady();
            return purchases;
        }

        /// <summary>Buys a shop item with real money: sets the signed-in player's account token on the store, starts the store's purchase and, once the store has charged, has Flock deliver it before the store's purchase is finished. Throws <see cref="FlockException"/> when Flock refuses a purchase the player paid for (it stays unfinished and comes back at the next launch); a store that does not take the payment ends as <see cref="FlockStorePurchaseOutcome.Failed"/>.</summary>
        public async Task<FlockStorePurchase> BuyAsync(string shopItemId, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlockStorePurchases));
            if (FlockClient.Instance != _client)
                throw new FlockValidationException("This store connection was made before the Flock client was created again") { Hint = "Dispose it and call FlockStorePurchases.ConnectAsync again once the new client is created." };
            if (string.IsNullOrEmpty(shopItemId))
                throw new FlockValidationException("Shop Item ID is empty") { Hint = "Pass the shop item's Id (ShopItem.Id)." };
            if (!_itemsById.TryGetValue(shopItemId, out ShopItem item))
                throw new FlockValidationException("That shop item was not in the game's shops when the store connected") { Hint = "Pass a ShopItem.Id from this game's shops. An item added in the Flock dashboard since then is sold once this connection is disposed and ConnectAsync runs again." };
            if (!item.StoreProductIds.TryGetValue(Store, out string productId) || !IsSoldHere(item))
                throw new FlockValidationException($"This shop item has no {Store} product id") { Hint = $"Enter the item's {Store} product id under Store product IDs on the shop item in the Flock dashboard, exactly as the store has it, then connect again." };
            if (_buying.ContainsKey(productId))
                throw new FlockValidationException("This product is already being bought") { Hint = "Wait for the first BuyAsync of it to end before starting another." };
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };

            // Taken before the first await, so a second call for this product meanwhile is refused rather than overwriting this one.
            Buying buying = new Buying { Item = item };
            _buying[productId] = buying;
            try
            {
                string accountToken = await FlockClient.Instance.Shop.GetStoreAccountTokenAsync(cancellationToken);
                _connection.SetAccountToken(accountToken);
                // A cancelled wait leaves the store's purchase going: once paid it is delivered through PurchaseDelivered.
                using (cancellationToken.Register(() => buying.Done.TrySetCanceled()))
                {
                    _connection.Buy(productId);
                    return await buying.Done.Task;
                }
            }
            finally
            {
                if (_buying.TryGetValue(productId, out Buying current) && current == buying)
                    _buying.Remove(productId);
            }
        }

        /// <summary>Stops listening to the store and to sign-ins; a <see cref="BuyAsync"/> still waiting ends as cancelled.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_connected == this)
                _connected = null;
            _connection.OrderPaid -= HandlePaid;
            _connection.OrderDeferred -= HandleDeferred;
            _connection.OrderFailed -= HandleFailed;
            _connection.StopListening();
            FlockEvents.OnAuthenticated -= HandleSignedIn;
            _waiting.Clear();
            foreach (Buying buying in new List<Buying>(_buying.Values))
                buying.Done.TrySetCanceled();
            _buying.Clear();
        }

        private static string StoreOfThisPlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                    return PaymentProviders.GooglePlay;
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.tvOS:
                case RuntimePlatform.OSXPlayer:
                    return PaymentProviders.AppleAppStore;
                default:
                    throw new FlockValidationException($"No store sells in-app purchases on {Application.platform}") { Hint = "On a phone the store is found by itself. In the editor, where Unity IAP's fake store answers, name the store to test with: FlockStorePurchases.ConnectAsync(PaymentProviders.GooglePlay)." };
            }
        }

        private static async Task<List<ShopItem>> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            List<ShopItem> catalog = new List<ShopItem>();
            int page = 1;
            while (true)
            {
                PaginatedResponse<Shop> shops = await FlockClient.Instance.Shop.GetAllAsync(page, 100, cancellationToken);
                if (shops?.Items == null || shops.Items.Length == 0)
                    return catalog;
                foreach (Shop shop in shops.Items)
                    if (shop?.ShopItems != null)
                        catalog.AddRange(shop.ShopItems);
                if (page * 100 >= shops.Total)
                    return catalog;
                page++;
            }
        }

        // A standard item is a one-time product; a currency pack or a consumable a consumable one; a kind added later is not sold here.
        private static bool IsSoldHere(ShopItem item)
            => item.Type == ShopItemTypes.Standard || item.Type == ShopItemTypes.CurrencyPack || item.Type == ShopItemTypes.Consumable;

        private List<FlockStoreProduct> LearnProducts(List<ShopItem> catalog)
        {
            List<FlockStoreProduct> products = new List<FlockStoreProduct>();
            foreach (ShopItem item in catalog)
            {
                if (item == null || string.IsNullOrEmpty(item.Id))
                    continue;
                _itemsById[item.Id] = item;
                if (!item.StoreProductIds.TryGetValue(Store, out string productId) || string.IsNullOrEmpty(productId))
                    continue;
                if (!IsSoldHere(item))
                {
                    FlockClient.Instance.Logger.LogWarning($"Shop item '{item.Name}' is a kind ('{item.Type}') this SDK does not sell through the store yet; it is left out.");
                    continue;
                }
                if (_itemByProduct.TryGetValue(productId, out ShopItem first))
                {
                    if (first.Id != item.Id)
                        _productsSoldTwice.Add(productId);
                    continue;
                }
                _itemByProduct[productId] = item;
                products.Add(new FlockStoreProduct(productId, item.Type != ShopItemTypes.Standard));
            }
            foreach (string productId in _productsSoldTwice)
            {
                // Still registered for BuyAsync, which names the item; never matched to one when the store hands a purchase back.
                _itemByProduct.Remove(productId);
                FlockClient.Instance.Logger.LogWarning($"Two shop items sell the {Store} product '{productId}'; a purchase of it is sent to Flock only from BuyAsync, which names the item.");
            }
            return products;
        }

        private void ListenToTheStore()
        {
            _connection.OrderPaid += HandlePaid;
            _connection.OrderDeferred += HandleDeferred;
            _connection.OrderFailed += HandleFailed;
        }

        private void BeReady()
        {
            _ready = true;
            FlockEvents.OnAuthenticated += HandleSignedIn;
            if (FlockClient.Instance.IsAuthenticated)
                HandleSignedIn(null);
        }

        // Purchases that waited for a sign-in go now, and the store is asked for any it still holds (Google hands them back only when asked).
        private void HandleSignedIn(FlockAuthInfo signIn)
        {
            if (_disposed)
                return;
            List<FlockStoreOrder> waiting = new List<FlockStoreOrder>(_waiting);
            _waiting.Clear();
            foreach (FlockStoreOrder order in waiting)
                HandlePaid(order);
            _connection.FetchUnfinishedPurchases();
        }

        // The checks run as the store hands the purchase over; only sending it to Flock waits.
        private void HandlePaid(FlockStoreOrder order)
        {
            if (_disposed || !FlockClient.IsInitialized || FlockClient.Instance != _client || string.IsNullOrEmpty(order.ProductId))
                return;
            if (!_ready)
            {
                Hold(order);
                return;
            }
            _buying.TryGetValue(order.ProductId, out Buying buying);
            ShopItem item = buying?.Item;
            if (item == null && !_itemByProduct.TryGetValue(order.ProductId, out item) && !_productsSoldTwice.Contains(order.ProductId))
                return; // Not a Flock item's product: the game finishes its own purchases.
            if (!FlockClient.Instance.IsAuthenticated)
            {
                Hold(order);
                return;
            }
            if (!_sentThisLaunch.Add(order.TransactionId))
                return;
            if (item == null)
            {
                RaiseNeedsAttention(null, order.ProductId, new FlockValidationException($"Two shop items sell the {Store} product '{order.ProductId}', so this purchase cannot be matched to one")
                {
                    Hint = "Give each shop item its own store product id in the Flock dashboard. The purchase stays unfinished in the store and is matched at the next launch."
                });
                return;
            }
            Send(order, item, buying);
        }

        private async void Send(FlockStoreOrder order, ShopItem item, Buying buying)
        {
            PurchaseResult delivery;
            try
            {
                delivery = await FlockClient.Instance.Shop.RedeemStoreReceiptAsync(item.Id, ReceiptOf(order));
            }
            catch (FlockException refused)
            {
                if (buying == null || !buying.Done.TrySetException(refused))
                    RaiseNeedsAttention(item, order.ProductId, refused);
                return;
            }
            catch (OperationCanceledException)
            {
                // The player signed out or changed while it was sent: it stays unfinished for the next launch.
                buying?.Done.TrySetCanceled();
                return;
            }

            try
            {
                _connection.Finish(order);
            }
            catch (Exception notFinished)
            {
                // Delivered all the same: the store hands it back, and Flock answers the repeat with this delivery.
                FlockClient.Instance.Logger.LogWarning($"Flock delivered the purchase of '{item.Name}', but the store could not finish it: {notFinished.Message}");
            }
            FlockStorePurchase delivered = new FlockStorePurchase(FlockStorePurchaseOutcome.Delivered, item, delivery, null);
            if (buying == null || !buying.Done.TrySetResult(delivered))
                FlockEvents.InvokeEach(PurchaseDelivered, delivered, $"{nameof(FlockStorePurchases)}.{nameof(PurchaseDelivered)}");
        }

        private void Hold(FlockStoreOrder order)
        {
            if (!_waiting.Exists(waiting => waiting.TransactionId == order.TransactionId))
                _waiting.Add(order);
        }

        private void HandleDeferred(FlockStoreOrder order)
        {
            if (order.ProductId != null && _buying.TryGetValue(order.ProductId, out Buying buying))
                buying.Done.TrySetResult(new FlockStorePurchase(FlockStorePurchaseOutcome.Pending, buying.Item, null, null));
        }

        private void HandleFailed(string productId, string reason, bool cancelled)
        {
            if (productId == null || !_buying.TryGetValue(productId, out Buying buying))
                return;
            buying.Done.TrySetResult(cancelled
                ? new FlockStorePurchase(FlockStorePurchaseOutcome.Cancelled, buying.Item, null, null)
                : new FlockStorePurchase(FlockStorePurchaseOutcome.Failed, buying.Item, null, reason));
        }

        private StoreReceipt ReceiptOf(FlockStoreOrder order)
            => Store == PaymentProviders.AppleAppStore
                ? StoreReceipt.FromAppleAppStore(order.TransactionId)
                : StoreReceipt.FromGooglePlay(order.ProductId, order.TransactionId);

        private void RaiseNeedsAttention(ShopItem item, string productId, FlockException error)
            => FlockEvents.InvokeEach(PurchaseNeedsAttention, new FlockStorePurchaseProblem(item, productId, error), $"{nameof(FlockStorePurchases)}.{nameof(PurchaseNeedsAttention)}");
    }
}
