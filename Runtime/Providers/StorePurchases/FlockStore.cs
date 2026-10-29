using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using Flock.Models;
using UnityEngine;

namespace Flock.Providers
{
    /// <summary>Why a store did not take a payment (<see cref="FlockStoreException.Reason"/>). Strings rather than an enum, so a reason added later still reads.</summary>
    public static class FlockStoreFailureReasons
    {
        /// <summary>The player cancelled in the store's dialog.</summary>
        public const string Cancelled = "cancelled";
        /// <summary>The player already owns this one-time product: show it as owned rather than for sale.</summary>
        public const string AlreadyOwned = "already_owned";
        /// <summary>An earlier purchase of this product is still waiting for its payment.</summary>
        public const string AlreadyPending = "already_pending";
        /// <summary>The store declined the payment.</summary>
        public const string PaymentDeclined = "payment_declined";
        /// <summary>The store does not sell this product here, or it is not set up in the store.</summary>
        public const string ProductUnavailable = "product_unavailable";
        /// <summary>The store could not be reached, or purchases are switched off on this device.</summary>
        public const string StoreUnavailable = "store_unavailable";
        /// <summary>Nobody is signed in to the store on this device.</summary>
        public const string NotSignedInToTheStore = "not_signed_in_to_the_store";
        /// <summary>Any other reason; the exception's message has the store's words.</summary>
        public const string Unknown = "unknown";
    }

    /// <summary>The store did not take the payment for a real-money purchase, so nothing was charged. <see cref="Reason"/> says why; the message has the store's own words.</summary>
    public sealed class FlockStoreException : FlockException
    {
        /// <summary>Why the store did not take the payment, one of <see cref="FlockStoreFailureReasons"/>.</summary>
        public string Reason { get; }

        internal FlockStoreException(string reason, string message) : base(message)
        {
            Reason = reason;
        }
    }

    /// <summary>A product's price as the store shows it to this player: in their currency, with their taxes.</summary>
    public sealed class FlockStorePrice
    {
        /// <summary>The price as the store writes it, such as "SAR 4.59" or "$1.09": show this one.</summary>
        public string Text { get; }

        /// <summary>The price as a number, in <see cref="CurrencyCode"/>.</summary>
        public decimal Amount { get; }

        /// <summary>The currency's ISO 4217 code, such as SAR or USD.</summary>
        public string CurrencyCode { get; }

        internal FlockStorePrice(string text, decimal amount, string currencyCode)
        {
            Text = text;
            Amount = amount;
            CurrencyCode = currencyCode;
        }
    }

    /// <summary>What <see cref="FlockStore.RestoreAsync"/> found: the one-time items Flock holds for the signed-in player from the store's purchases, and the purchases it could not deliver.</summary>
    public sealed class FlockStoreRestore
    {
        /// <summary>The shop items Flock holds for the signed-in player from this store account's purchases; one delivered before grants nothing more.</summary>
        public IReadOnlyList<ShopItem> Restored { get; }

        /// <summary>Purchases Flock did not deliver, such as one bought on another Flock account.</summary>
        public IReadOnlyList<FlockUndeliveredPurchase> NotDelivered { get; }

        internal FlockStoreRestore(IReadOnlyList<ShopItem> restored, IReadOnlyList<FlockUndeliveredPurchase> notDelivered)
        {
            Restored = restored;
            NotDelivered = notDelivered;
        }
    }

    /// <summary>What <see cref="FlockStore.CheckSetupAsync"/> found, without buying anything.</summary>
    public sealed class FlockStoreSetupCheck
    {
        /// <summary>Real-money items with a product id for this store that the store did not find: check the id and that the product is active in the store.</summary>
        public IReadOnlyList<ShopItem> NotFoundInTheStore { get; }

        /// <summary>Real-money items with no product id for this store: they cannot be bought here.</summary>
        public IReadOnlyList<ShopItem> MissingProductId { get; }

        /// <summary>Whether Flock's server could ask the store about a purchase with the game's keys. False with <see cref="ServerProblem"/> set when it could not; false with no problem when there was no item to ask about.</summary>
        public bool ServerCanCheckPurchases { get; }

        /// <summary>Why Flock's server could not ask the store (its ErrorCode and Hint say what to fix), or null.</summary>
        public FlockException ServerProblem { get; }

        /// <summary>True when nothing above needs fixing.</summary>
        public bool IsReady => ServerCanCheckPurchases && NotFoundInTheStore.Count == 0 && MissingProductId.Count == 0;

        internal FlockStoreSetupCheck(IReadOnlyList<ShopItem> notFound, IReadOnlyList<ShopItem> missingProductId, bool serverCanCheck, FlockException serverProblem)
        {
            NotFoundInTheStore = notFound;
            MissingProductId = missingProductId;
            ServerCanCheckPurchases = serverCanCheck;
            ServerProblem = serverProblem;
        }
    }

    /// <summary>A store purchase the player paid for that Flock did not deliver. It stays unfinished in the store, which hands it back at the next launch to be sent again.</summary>
    public sealed class FlockUndeliveredPurchase
    {
        /// <summary>The shop item, or null when two shop items sell the store's product.</summary>
        public ShopItem Item { get; }

        /// <summary>The store's product id.</summary>
        public string ProductId { get; }

        /// <summary>Why Flock did not deliver: its ErrorCode and Hint say what to do.</summary>
        public FlockException Error { get; }

        internal FlockUndeliveredPurchase(ShopItem item, string productId, FlockException error)
        {
            Item = item;
            ProductId = productId;
            Error = error;
        }
    }

    /// <summary>The game's connection to Google Play or the App Store through Unity IAP 5. Connect it once at start; then <c>Shop.PurchaseAsync</c> buys real-money items through it: the store charges the player, Flock checks the purchase with the store and delivers the item, and the store's purchase is finished only once Flock delivered. A purchase Flock refused stays unfinished, so the store hands it back at the next launch, where it is sent again once the player is signed in.</summary>
    public sealed class FlockStore : IDisposable, IFlockStoreCheckout
    {
        /// <summary>Raised for every real-money purchase Flock delivers: one bought just now, one the store was waiting to be paid for, and one left unfinished at an earlier launch (not for <see cref="RestoreAsync"/>, which returns its own list). Refresh the wallet and inventory here. Raised on the main thread.</summary>
        public event Action<ShopItem, PurchaseResult> PurchaseCompleted;

        /// <summary>Raised for every purchase the player paid for that Flock did not deliver; it stays unfinished in the store and is sent again at the next launch. Raised on the main thread.</summary>
        public event Action<FlockUndeliveredPurchase> PurchaseNotDelivered;

        /// <summary>The store this connection buys from: <see cref="PaymentProviders.AppleAppStore"/> or <see cref="PaymentProviders.GooglePlay"/>.</summary>
        public string PaymentProvider { get; }

        private sealed class Buying
        {
            public ShopItem Item;
            public readonly TaskCompletionSource<PurchaseResult> Done = new TaskCompletionSource<PurchaseResult>();
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
        private static FlockStore _connected;

        private FlockStore(IFlockStoreConnection connection, string paymentProvider)
        {
            _connection = connection;
            PaymentProvider = paymentProvider;
        }

        /// <summary>Connects Unity IAP's store, reads the game's shops and registers each item that has a product id for this store (a currency pack or consumable as a consumable product, a standard item as a one-time product); from then on <c>Shop.PurchaseAsync</c> buys real-money items through it. Once a player is signed in, purchases left unfinished are sent to Flock. <paramref name="store"/> is found from the platform on a phone; in the editor, where Unity IAP's fake store answers, name the store to test with. Connect once and keep the result: a second connect while it is connected is refused, so Dispose it before connecting again. A store that never answers is waited for until <paramref name="cancellationToken"/> is cancelled.</summary>
        public static Task<FlockStore> ConnectAsync(string store = null, CancellationToken cancellationToken = default)
        {
            string chosen = store ?? StoreOfThisPlatform();
            return ConnectAsync(new FlockUnityIapConnection(), chosen, cancellationToken);
        }

        internal static async Task<FlockStore> ConnectAsync(IFlockStoreConnection connection, string store, CancellationToken cancellationToken)
        {
            FlockStore connected = new FlockStore(connection, store);
            try
            {
                if (store != PaymentProviders.AppleAppStore && store != PaymentProviders.GooglePlay)
                    throw new FlockValidationException($"'{store}' is not a store Unity IAP buys from") { Hint = "Pass PaymentProviders.AppleAppStore or PaymentProviders.GooglePlay." };
                connected._client = FlockClient.Instance;
                if (_connected != null && _connected._client == connected._client)
                    throw new FlockValidationException("The store is already connected") { Hint = "Keep the FlockStore that ConnectAsync returned and use it everywhere, or Dispose it before connecting again." };
                _connected = connected;
                // Listening first: a store hands back unfinished purchases as it connects, and those are held until the shops are read.
                connected.ListenToTheStore();
                await DoneOrCancelled(connection.ConnectAsync(), cancellationToken);
                List<ShopItem> catalog = await ReadCatalogAsync(cancellationToken);
                await connection.RegisterProductsAsync(connected.LearnProducts(catalog), cancellationToken);
            }
            catch
            {
                connected.Dispose();
                throw;
            }
            connected.BeReady();
            return connected;
        }

        ShopItem IFlockStoreCheckout.Find(string shopItemId)
            => !_disposed && shopItemId != null && _itemsById.TryGetValue(shopItemId, out ShopItem item) ? item : null;

        // Shop.PurchaseAsync's real-money path: sets the signed-in player's account token on the store, starts the store's purchase and, once the
        // store has charged, has Flock deliver it before the store's purchase is finished. A store that does not take the payment throws FlockStoreException.
        async Task<PurchaseResult> IFlockStoreCheckout.PurchaseAsync(ShopItem shopItem, CancellationToken cancellationToken)
        {
            ThrowIfUnusable();
            string productId = ProductOf(shopItem?.Id, out ShopItem item);
            if (_buying.ContainsKey(productId))
                throw new FlockValidationException("This product is already being bought") { Hint = "Wait for the first PurchaseAsync of it to end before starting another." };
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };

            // Taken before the first await, so a second call for this product meanwhile is refused rather than overwriting this one.
            Buying buying = new Buying { Item = item };
            _buying[productId] = buying;
            try
            {
                string accountToken = await FlockClient.Instance.Shop.GetStoreAccountTokenAsync(cancellationToken);
                _connection.SetAccountToken(accountToken);
                // A cancelled wait leaves the store's purchase going: once paid it is delivered through PurchaseCompleted.
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

        /// <summary>The item's price as the store shows it to this player, in their currency and with their taxes: show this rather than the item's Flock price. Null when the store did not find the item's product; throws for an item with no product id for this store.</summary>
        public FlockStorePrice GetPrice(string shopItemId)
        {
            ThrowIfUnusable();
            return _connection.PriceOf(ProductOf(shopItemId, out ShopItem _));
        }

        /// <summary>Asks the store for the one-time purchases this store account already owns and has Flock deliver each to the signed-in player; a purchase Flock delivered before grants nothing more. Purchases still unfinished go through <see cref="PurchaseCompleted"/> as usual. One made for another Flock account is refused and listed in <see cref="FlockStoreRestore.NotDelivered"/>. Throws <see cref="FlockException"/> when the store cannot list the purchases (on Apple, also when the player does not sign in to the store); a store that never answers is waited for until <paramref name="cancellationToken"/> is cancelled.</summary>
        public async Task<FlockStoreRestore> RestoreAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfUnusable();
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            IReadOnlyList<FlockStoreOrder> owned = await _connection.FetchOwnedPurchasesAsync(cancellationToken);
            List<ShopItem> restored = new List<ShopItem>();
            List<FlockUndeliveredPurchase> notDelivered = new List<FlockUndeliveredPurchase>();
            foreach (FlockStoreOrder order in owned)
            {
                if (string.IsNullOrEmpty(order.ProductId))
                    continue;
                if (_productsSoldTwice.Contains(order.ProductId))
                {
                    notDelivered.Add(new FlockUndeliveredPurchase(null, order.ProductId, SoldTwice(order.ProductId, " Then restore again.")));
                    continue;
                }
                // Not a Flock item's product: the game restores its own purchases.
                if (!_itemByProduct.TryGetValue(order.ProductId, out ShopItem item))
                    continue;
                try
                {
                    await FlockClient.Instance.Shop.RedeemStoreReceiptAsync(item.Id, ReceiptOf(order), cancellationToken);
                    restored.Add(item);
                }
                catch (FlockException refused)
                {
                    notDelivered.Add(new FlockUndeliveredPurchase(item, order.ProductId, refused));
                }
            }
            return new FlockStoreRestore(restored, notDelivered);
        }

        /// <summary>Checks the store setup without buying anything: which real-money items the store did not find or have no product id for this store, and whether Flock's server can ask the store about a purchase with the game's keys (it is asked about a made-up purchase, which the store answers as unknown when the keys work). Needs a signed-in player.</summary>
        public async Task<FlockStoreSetupCheck> CheckSetupAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfUnusable();
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            List<ShopItem> notFound = new List<ShopItem>();
            List<ShopItem> missingProductId = new List<ShopItem>();
            ShopItem askAbout = null;
            string askedProduct = null;
            foreach (ShopItem item in _itemsById.Values)
            {
                if (!item.IsRealMoney || !IsSoldHere(item))
                    continue;
                if (!item.StoreProductIds.TryGetValue(PaymentProvider, out string productId) || string.IsNullOrEmpty(productId))
                {
                    missingProductId.Add(item);
                    continue;
                }
                if (_connection.PriceOf(productId) == null)
                    notFound.Add(item);
                if (askAbout == null)
                {
                    askAbout = item;
                    askedProduct = productId;
                }
            }

            bool serverCanCheck = false;
            FlockException serverProblem = null;
            if (askAbout != null)
            {
                try
                {
                    await FlockClient.Instance.Shop.RedeemStoreReceiptAsync(askAbout.Id, MadeUpReceipt(askedProduct), cancellationToken);
                    serverCanCheck = true;
                }
                catch (FlockException refused) when (refused.ErrorCode == FlockErrorCode.PaymentProviderReceiptInvalid)
                {
                    serverCanCheck = true;
                }
                catch (FlockException refused)
                {
                    serverProblem = refused;
                }
            }
            return new FlockStoreSetupCheck(notFound, missingProductId, serverCanCheck, serverProblem);
        }

        // Unity IAP's connect takes no token, so the caller's ends the wait; the connection it leaves is stopped by the caller's Dispose.
        private static async Task DoneOrCancelled(Task task, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetCanceled()))
                await await Task.WhenAny(task, cancelled.Task);
        }

        private void ThrowIfUnusable()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FlockStore));
            if (FlockClient.Instance != _client)
                throw new FlockValidationException("This store connection was made before the Flock client was created again") { Hint = "Dispose it and call FlockStore.ConnectAsync again once the new client is created." };
        }

        // The store's product for a shop item this connection sells, or a refusal saying why there is none.
        private string ProductOf(string shopItemId, out ShopItem item)
        {
            if (string.IsNullOrEmpty(shopItemId))
                throw new FlockValidationException("Shop Item ID is empty") { Hint = "Pass the shop item's Id (ShopItem.Id)." };
            if (!_itemsById.TryGetValue(shopItemId, out item))
                throw new FlockValidationException("That shop item was not in the game's shops when the store connected") { Hint = "Pass a ShopItem.Id from this game's shops. An item added in the Flock dashboard since then is sold once this connection is disposed and ConnectAsync runs again." };
            if (!item.StoreProductIds.TryGetValue(PaymentProvider, out string productId) || !IsSoldHere(item))
                throw new FlockValidationException($"This shop item has no {PaymentProvider} product id") { Hint = $"Enter the item's {PaymentProvider} product id under Store product IDs on the shop item in the Flock dashboard, exactly as the store has it, then connect again." };
            return productId;
        }

        // A purchase no store issued: the store answers it as unknown when Flock's keys work, and refuses the keys otherwise.
        private StoreReceipt MadeUpReceipt(string productId)
            => PaymentProvider == PaymentProviders.AppleAppStore
                ? StoreReceipt.FromAppleAppStore("0")
                : StoreReceipt.FromGooglePlay(productId, "flock-setup-check");

        /// <summary>Stops listening to the store and to sign-ins; <c>Shop.PurchaseAsync</c> no longer buys real-money items through it, and one still waiting ends as cancelled.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_connected == this)
                _connected = null;
            if (_client != null && _client.Shop != null && _client.Shop.ConnectedStore == this)
                _client.Shop.ConnectedStore = null;
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
                    throw new FlockValidationException($"No store sells in-app purchases on {Application.platform}") { Hint = "On a phone the store is found by itself. In the editor, where Unity IAP's fake store answers, name the store to test with: FlockStore.ConnectAsync(PaymentProviders.GooglePlay)." };
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
                if (!item.StoreProductIds.TryGetValue(PaymentProvider, out string productId) || string.IsNullOrEmpty(productId))
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
                // Still registered for PurchaseAsync, which names the item; never matched to one when the store hands a purchase back.
                _itemByProduct.Remove(productId);
                FlockClient.Instance.Logger.LogWarning($"Two shop items sell the {PaymentProvider} product '{productId}'; a purchase of it is sent to Flock only from PurchaseAsync, which names the item.");
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
            _client.Shop.ConnectedStore = this;
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
                RaiseNotDelivered(null, order.ProductId, SoldTwice(order.ProductId, " The purchase stays unfinished in the store and is matched at the next launch."));
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
                buying?.Done.TrySetException(refused);
                RaiseNotDelivered(item, order.ProductId, refused);
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
            buying?.Done.TrySetResult(delivery);
            FlockEvents.InvokeEach(PurchaseCompleted, item, delivery, $"{nameof(FlockStore)}.{nameof(PurchaseCompleted)}");
        }

        private void Hold(FlockStoreOrder order)
        {
            if (!_waiting.Exists(waiting => waiting.TransactionId == order.TransactionId))
                _waiting.Add(order);
        }

        // The store is waiting for the payment or a parent's approval: the call answers pending, and PurchaseCompleted follows once paid.
        private void HandleDeferred(FlockStoreOrder order)
        {
            if (order.ProductId != null && _buying.TryGetValue(order.ProductId, out Buying buying))
                buying.Done.TrySetResult(new PurchaseResult { ItemType = buying.Item.Type, Status = PurchaseResultStatuses.Pending });
        }

        private void HandleFailed(FlockStoreFailure failure)
        {
            if (failure.ProductId == null || !_buying.TryGetValue(failure.ProductId, out Buying buying))
                return;
            string reason = failure.Reason ?? FlockStoreFailureReasons.Unknown;
            string words = reason == FlockStoreFailureReasons.Cancelled ? "The player cancelled the purchase in the store's dialog" : "The store did not take the payment: " + failure.Text;
            buying.Done.TrySetException(new FlockStoreException(reason, words) { Hint = FixFor(reason), Operation = "Purchase shop item" });
        }

        private static string FixFor(string reason)
        {
            switch (reason)
            {
                case FlockStoreFailureReasons.Cancelled: return "Nothing was charged. Offer it again if the player wants it.";
                case FlockStoreFailureReasons.AlreadyOwned: return "This store account already owns the one-time item: call RestoreAsync to deliver it, and show it as owned.";
                case FlockStoreFailureReasons.AlreadyPending: return "An earlier purchase of it is waiting for its payment; PurchaseCompleted reports it once paid.";
                case FlockStoreFailureReasons.PaymentDeclined: return "Nothing was charged. The player can choose another payment method in the store and try again.";
                case FlockStoreFailureReasons.ProductUnavailable: return "Check the item's product id under Store product IDs in the Flock dashboard matches an active product in the store; CheckSetupAsync lists the ones the store did not find.";
                case FlockStoreFailureReasons.StoreUnavailable: return "Check the device is online and that purchases are allowed on it, then try again.";
                case FlockStoreFailureReasons.NotSignedInToTheStore: return "Ask the player to sign in to the store on this device, then try again.";
                default: return "Nothing was charged. Try again; if it keeps failing, CheckSetupAsync checks the store's setup.";
            }
        }

        private FlockValidationException SoldTwice(string productId, string then)
            => new FlockValidationException($"Two shop items sell the {PaymentProvider} product '{productId}', so this purchase cannot be matched to one")
            {
                Hint = "Give each shop item its own store product id in the Flock dashboard." + then
            };

        private StoreReceipt ReceiptOf(FlockStoreOrder order)
            => PaymentProvider == PaymentProviders.AppleAppStore
                ? StoreReceipt.FromAppleAppStore(order.TransactionId)
                : StoreReceipt.FromGooglePlay(order.ProductId, order.TransactionId);

        private void RaiseNotDelivered(ShopItem item, string productId, FlockException error)
            => FlockEvents.InvokeEach(PurchaseNotDelivered, new FlockUndeliveredPurchase(item, productId, error), $"{nameof(FlockStore)}.{nameof(PurchaseNotDelivered)}");
    }
}
