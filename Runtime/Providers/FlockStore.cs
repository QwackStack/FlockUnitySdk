using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Exceptions;
using Flock.Models;

namespace Flock.Providers
{
    /// <summary>How <see cref="FlockStore.ConnectAsync(FlockStoreOptions, CancellationToken)"/> pays real money; every setting can be left at its default.</summary>
    public sealed class FlockStoreOptions
    {
        /// <summary>What to pay through: null or empty for this platform's own (Google Play on Android, the App Store on iOS, tvOS and visionOS, a web checkout through the game's one active gateway everywhere else), or one of <see cref="PaymentProviders"/>: a store (a Mac App Store build names <see cref="PaymentProviders.AppleAppStore"/>; in the editor Unity IAP's fake store answers for it), or a web gateway (Stripe, Tap or Moyasar) when the game has several active.</summary>
        public string PaymentProvider { get; set; }

        /// <summary>On a web checkout, where the player's browser lands after paying; null leaves it to the server.</summary>
        public string ReturnUrl { get; set; }

        /// <summary>On a web checkout, where the player's browser lands after giving up; null uses <see cref="ReturnUrl"/>.</summary>
        public string CancelUrl { get; set; }

        /// <summary>On a web checkout, opens the gateway's payment page; null opens it with <c>Application.OpenURL</c>. On WebGL a browser may block a tab opened after an answer from the network, so a web game can show a button here whose click opens the page.</summary>
        public Action<string> OpenPaymentPage { get; set; }

        /// <summary>On a web checkout, how often a checkout waiting for its payment is read; 3 seconds when left at zero, negative or infinite (<see cref="TimeSpan.MaxValue"/>).</summary>
        public TimeSpan CheckEvery { get; set; }
    }

    /// <summary>A real-money item's price to show this player: on a phone as the store shows it, in their currency and with their taxes; on a web checkout the item's own price.</summary>
    public sealed class FlockStorePrice
    {
        /// <summary>The price written out, such as "SAR 4.59" or "$1.09": show this one.</summary>
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

    /// <summary>What <see cref="FlockStore.RestoreAsync"/> found: the one-time items Flock holds for the signed-in player from the store's purchases, and the purchases it could not deliver; both empty on a web checkout.</summary>
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

        /// <summary>Whether Flock's server could ask the store about a purchase with the game's keys. False with <see cref="ServerProblem"/> set when it could not; false with no problem when there was no item to ask about, or on a web checkout (see <see cref="NotChecked"/>).</summary>
        public bool ServerCanCheckPurchases { get; }

        /// <summary>Why Flock's server could not ask the store (its ErrorCode and Hint say what to fix), or null.</summary>
        public FlockException ServerProblem { get; }

        /// <summary>What this check could not look at, and how to check it instead, or null: on a web checkout, the gateway.</summary>
        public string NotChecked { get; }

        /// <summary>True when nothing above needs fixing; never on a web checkout, where <see cref="NotChecked"/> says how to check it.</summary>
        public bool IsReady => NotChecked == null && ServerCanCheckPurchases && NotFoundInTheStore.Count == 0 && MissingProductId.Count == 0;

        internal FlockStoreSetupCheck(IReadOnlyList<ShopItem> notFound, IReadOnlyList<ShopItem> missingProductId, bool serverCanCheck, FlockException serverProblem, string notChecked = null)
        {
            NotFoundInTheStore = notFound;
            MissingProductId = missingProductId;
            ServerCanCheckPurchases = serverCanCheck;
            ServerProblem = serverProblem;
            NotChecked = notChecked;
        }
    }

    /// <summary>A store purchase the player paid for that Flock did not deliver. It stays unfinished in the store, which hands it back at the next launch to be sent again. A web checkout has none: Flock's server delivers it.</summary>
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

    // A store's product as Flock sells it: consumable, or one-time.
    internal readonly struct FlockStoreProduct
    {
        public readonly string ProductId;
        public readonly bool Consumable;

        public FlockStoreProduct(string productId, bool consumable)
        {
            ProductId = productId;
            Consumable = consumable;
        }
    }

    // A purchase the store charged for; Handle is the store library's own order.
    internal sealed class FlockStoreOrder
    {
        public readonly string ProductId;
        public readonly string TransactionId;
        public readonly object Handle;

        public FlockStoreOrder(string productId, string transactionId, object handle)
        {
            ProductId = productId;
            TransactionId = transactionId;
            Handle = handle;
        }
    }

    // A purchase the store did not take: the product, why (FlockStoreFailureReasons, Cancelled when the player cancelled), and the store's own words.
    internal readonly struct FlockStoreFailure
    {
        public readonly string ProductId;
        public readonly string Reason;
        public readonly string Text;

        public FlockStoreFailure(string productId, string reason, string text)
        {
            ProductId = productId;
            Reason = reason;
            Text = text;
        }
    }

    // What FlockStore needs of a store library; Unity IAP 5 is the one a game uses.
    internal interface IFlockStoreConnection
    {
        event Action<FlockStoreOrder> OrderPaid;
        event Action<FlockStoreOrder> OrderDeferred;
        event Action<FlockStoreFailure> OrderFailed;

        Task ConnectAsync();
        Task RegisterProductsAsync(IReadOnlyList<FlockStoreProduct> products, CancellationToken cancellationToken);
        void SetAccountToken(string accountToken);
        void Buy(string productId);
        void Finish(FlockStoreOrder order);
        void FetchUnfinishedPurchases();

        // The store's own price of a registered product, or null when the store did not find the product.
        FlockStorePrice PriceOf(string productId);

        // Purchases the store says this store account already owns and has finished, after asking the store to restore them.
        Task<IReadOnlyList<FlockStoreOrder>> FetchOwnedPurchasesAsync(CancellationToken cancellationToken);

        void StopListening();
    }

    /// <summary>The game's way to pay real money, the same on every platform: connect it once at start, and <c>Shop.PurchaseAsync</c> buys real-money items through it. On a phone it is the store, through Unity IAP 5: the store charges the player, Flock checks the purchase with the store and delivers the item, and the store's purchase is finished only once Flock delivered; a purchase Flock refused stays unfinished, so the store hands it back at the next launch, where it is sent again once the player is signed in. Everywhere else it is a web checkout through the game's gateway (Stripe, Tap or Moyasar, connected in the Flock dashboard): the purchase opens the gateway's payment page in the browser and waits until it is paid, failed or expired, and a checkout left open when the game closed is found again at the player's next sign-in while it is still open; one paid meanwhile is delivered by Flock without <see cref="PurchaseCompleted"/>, so refresh the wallet and inventory after each sign-in.</summary>
    public sealed class FlockStore : IDisposable, IFlockStoreCheckout
    {
        /// <summary>Raised for every real-money purchase Flock delivers: one bought just now, one the store or the gateway was waiting to be paid for (a web checkout whose wait the game cancelled included), one left unfinished at an earlier launch or found open at a sign-in, and one a restore sent again (not the finished purchases <see cref="RestoreAsync"/> lists). Refresh the wallet and inventory here. The item is null when a web checkout's item can no longer be read. Raised on the main thread.</summary>
        public event Action<ShopItem, PurchaseResult> PurchaseCompleted;

        /// <summary>Raised for every store purchase the player paid for that Flock did not deliver; it stays unfinished in the store and is sent again at the next launch. Never raised on a web checkout, which Flock's server delivers itself. Raised on the main thread.</summary>
        public event Action<FlockUndeliveredPurchase> PurchaseNotDelivered;

        /// <summary>What this connection pays through: <see cref="PaymentProviders.GooglePlay"/> or <see cref="PaymentProviders.AppleAppStore"/>, the web gateway named in <see cref="FlockStoreOptions.PaymentProvider"/>, or null for a web checkout through the game's one active gateway.</summary>
        public string PaymentProvider { get; }

        /// <summary>True when purchases go through a web checkout (the gateway's payment page in the browser), false through a phone's store.</summary>
        public bool IsWebCheckout => _connection == null;

        // Unity IAP 5's connection, set by its assembly when the game has Unity IAP 5; a store is refused without it.
        internal static Func<IFlockStoreConnection> StoreLibrary;

        private sealed class Buying
        {
            public ShopItem Item;
            // The sign-in the store's dialog was opened under, null until then; the call's wait ends with it.
            public int? SignIn;
            public readonly TaskCompletionSource<PurchaseResult> Done = new TaskCompletionSource<PurchaseResult>();
        }

        // Null on a web checkout, which pays through _web.
        private readonly IFlockStoreConnection _connection;
        private readonly FlockStoreOptions _options;
        private FlockWebCheckout _web;
        private readonly Dictionary<string, ShopItem> _itemsById = new Dictionary<string, ShopItem>();
        private readonly Dictionary<string, ShopItem> _itemByProduct = new Dictionary<string, ShopItem>();
        private readonly HashSet<string> _productsSoldTwice = new HashSet<string>();
        private readonly Dictionary<string, string> _shopOfItem = new Dictionary<string, string>();
        // Products this store sold at an earlier launch for items the shops no longer list (switched off or removed in the dashboard), by product id:
        // a purchase of one paid for and not yet delivered is still sent to Flock, which delivers what was paid for.
        private readonly Dictionary<string, string> _soldBefore = new Dictionary<string, string>();
        private readonly Dictionary<string, Buying> _buying = new Dictionary<string, Buying>();
        // A store's unfinished purchase is sent once a launch (payment plan D-P3); the next launch, or the player's restore, sends it again.
        private readonly HashSet<string> _sentThisLaunch = new HashSet<string>();
        // Purchases being sent to Flock now, which a restore leaves to their send.
        private readonly HashSet<string> _sendingNow = new HashSet<string>();
        // Purchases the store handed over before the shops were read or anyone signed in; sent once both are true.
        private readonly List<FlockStoreOrder> _waiting = new List<FlockStoreOrder>();
        private bool _ready;
        private bool _disposed;
        // The client this connection was made under: once another takes its place, the connection no longer acts.
        private FlockClient _client;
        // One connection a client: two would each send every purchase and raise every event.
        private static FlockStore _connected;

        private FlockStore(IFlockStoreConnection connection, string paymentProvider, FlockStoreOptions options)
        {
            _connection = connection;
            PaymentProvider = paymentProvider;
            // Copied as connected, with the way to pay this connection checked, so a game changing its options later changes nothing here.
            _options = new FlockStoreOptions
            {
                PaymentProvider = paymentProvider,
                ReturnUrl = options.ReturnUrl,
                CancelUrl = options.CancelUrl,
                OpenPaymentPage = options.OpenPaymentPage,
                CheckEvery = options.CheckEvery
            };
        }

        /// <summary>Connects the game's way to pay real money: <see cref="FlockStoreOptions.PaymentProvider"/>, or this platform's own when it is null or empty (Google Play on Android and the App Store on iOS, tvOS and visionOS, through Unity IAP 5; a web checkout everywhere else). It reads the game's shops; a store also registers each item that has a product id for it (a currency pack or consumable as a consumable product, a standard item as a one-time product) and sends Flock the purchases left unfinished once a player is signed in, and a web checkout reads the signed-in player's checkouts still open. From then on <c>Shop.PurchaseAsync</c> buys real-money items through it. A store is refused when Unity IAP 5 is not installed, never replaced by a web checkout, as a phone's store rules forbid paying for digital goods elsewhere. Connect once and keep the result: a second connect while it is connected is refused, so Dispose it before connecting again. A store that never answers is waited for until <paramref name="cancellationToken"/> is cancelled.</summary>
        public static async Task<FlockStore> ConnectAsync(FlockStoreOptions options = null, CancellationToken cancellationToken = default)
        {
            FlockStoreOptions chosen = options ?? new FlockStoreOptions();
            // Empty is a serialized string field left blank: the platform's own, as null is.
            string paymentProvider = string.IsNullOrEmpty(chosen.PaymentProvider) ? PaymentProviderOfThisPlatform() : chosen.PaymentProvider;
            if (!IsAStore(paymentProvider))
                return await ConnectAsync(null, paymentProvider, chosen, cancellationToken);
            if (StoreLibrary == null)
                throw NoStoreLibrary(paymentProvider);
            return await ConnectAsync(StoreLibrary(), paymentProvider, chosen, cancellationToken);
        }

        // The build target's own way to pay: its store on a phone, else a web checkout through the game's one active gateway (null).
        internal static string PaymentProviderOfThisPlatform()
        {
#if UNITY_ANDROID
            return PaymentProviders.GooglePlay;
#elif UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS
            return PaymentProviders.AppleAppStore;
#else
            return null;
#endif
        }

        internal static Task<FlockStore> ConnectAsync(IFlockStoreConnection connection, string store, CancellationToken cancellationToken)
            => ConnectAsync(connection, store, new FlockStoreOptions(), cancellationToken);

        // A store connection pays through a store; none, through a web checkout with the gateway named, or the game's one active gateway when null.
        internal static async Task<FlockStore> ConnectAsync(IFlockStoreConnection connection, string paymentProvider, FlockStoreOptions options, CancellationToken cancellationToken)
        {
            FlockStore connected = new FlockStore(connection, paymentProvider, options);
            try
            {
                bool paysThatWay = connection != null ? IsAStore(paymentProvider) : paymentProvider == null || FlockWebCheckout.IsWebGateway(paymentProvider);
                if (!paysThatWay)
                    throw new FlockValidationException($"'{paymentProvider}' is not a store or a web gateway Flock pays through")
                    {
                        Hint = "Leave FlockStoreOptions.PaymentProvider null for this platform's own, or name one of PaymentProviders: GooglePlay or AppleAppStore (a store, through Unity IAP 5), Stripe, Tap or Moyasar (a web checkout)."
                    };
                connected._client = FlockClient.Instance;
                if (_connected != null && _connected._client == connected._client)
                    throw new FlockValidationException("A way to pay real money is already connected") { Hint = "Keep the FlockStore that ConnectAsync returned and use it everywhere, or Dispose it before connecting again." };
                _connected = connected;
                if (connection != null)
                {
                    // Listening first: a store hands back unfinished purchases as it connects, and those are held until the shops are read.
                    connected.ListenToTheStore();
                    await DoneOrCancelled(connection.ConnectAsync(), cancellationToken);
                }
                List<ShopItem> catalog = await connected.ReadCatalogAsync(cancellationToken);
                connected.KnowItems(catalog);
                if (connection != null)
                {
                    List<FlockStoreProduct> products = connected.LearnProducts(catalog);
                    products.AddRange(connected.RememberProducts());
                    await connection.RegisterProductsAsync(products, cancellationToken);
                }
                connected.BeReady();
                connected._client.Shop.ConnectedStore = connected;
            }
            catch
            {
                connected.Dispose();
                throw;
            }
            return connected;
        }

        // A store sells the items it read at connect; a web checkout's item is read by the shop at each purchase, and checked again by the server as the checkout opens.
        ShopItem IFlockStoreCheckout.Find(string shopItemId)
            => !_disposed && _connection != null && shopItemId != null && _itemsById.TryGetValue(shopItemId, out ShopItem item) ? item : null;

        // Shop.PurchaseAsync's real-money path. A store: sets the signed-in player's account token on the store, starts the store's purchase and, once the
        // store has charged, has Flock deliver it before the store's purchase is finished. A web checkout: opens the checkout and waits for its payment.
        // A store or gateway that does not take the payment throws FlockStoreException.
        async Task<PurchaseResult> IFlockStoreCheckout.PurchaseAsync(ShopItem shopItem, CancellationToken cancellationToken)
        {
            ThrowIfUnusable();
            if (_connection == null)
                return await _web.PurchaseAsync(shopItem, cancellationToken);
            string productId = ProductOf(shopItem?.Id, out ShopItem item);
            if (_buying.ContainsKey(productId))
                throw new FlockValidationException("This product is already being bought") { Hint = "Wait for the first PurchaseAsync of it to end before starting another." };
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            // The sign-in the purchase is for, so the store never charges for a player who signed in while the item was read.
            int signIn = _client.SignInNumber;

            // Taken before the first await, so a second call for this product meanwhile is refused rather than overwriting this one.
            Buying buying = new Buying { Item = item };
            _buying[productId] = buying;
            try
            {
                // Switched off in the dashboard since the store connected: refused before the store takes the payment, as a wallet purchase is.
                if (!await FlockClient.Instance.Shop.IsForSaleAsync(_shopOfItem[item.Id], item.Id, cancellationToken))
                    throw new FlockValidationException($"'{item.Name}' is not for sale right now") { Code = NotForSaleCode, Hint = FlockErrorHints.For(FlockErrorCode.ShopItemNotForSale) };
                string accountToken = await FlockClient.Instance.Shop.GetStoreAccountTokenAsync(cancellationToken);
                // Bought for the player who asked, never for one who signed in while the item was read.
                if (_client.SignInNumber != signIn)
                    throw new OperationCanceledException("The player signed out or changed while the item was read, so nothing was bought");
                _connection.SetAccountToken(accountToken);
                // A cancelled wait leaves the store's purchase going: once paid it is delivered through PurchaseCompleted.
                using (cancellationToken.Register(() => buying.Done.TrySetCanceled()))
                {
                    // Cancelled before the store's dialog opens: the dialog never opens for a call that reported cancelled.
                    cancellationToken.ThrowIfCancellationRequested();
                    buying.SignIn = signIn;
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

        /// <summary>A real-money item's price to show this player, rather than its Price in the currency's smallest unit. On a phone, as the store shows it, in their currency and with their taxes: null when the store did not find the item's product, and refused for an item with no product id for this store. On a web checkout, the item's own price.</summary>
        public FlockStorePrice GetPrice(string shopItemId)
        {
            ThrowIfUnusable();
            if (_connection == null)
                return PriceOfTheItem(KnownItem(shopItemId));
            return _connection.PriceOf(ProductOf(shopItemId, out ShopItem _));
        }

        /// <summary>Asks the store for the one-time purchases this store account already owns and has Flock deliver each to the signed-in player; a purchase Flock delivered before grants nothing more. Purchases still unfinished, one Flock could not be reached for earlier in this launch included, are sent again and go through <see cref="PurchaseCompleted"/> or <see cref="PurchaseNotDelivered"/> as usual. One made for another Flock account is refused and listed in <see cref="FlockStoreRestore.NotDelivered"/>. Throws <see cref="FlockException"/> when the store cannot list the purchases (on Apple, also when the player does not sign in to the store); a store that never answers is waited for until <paramref name="cancellationToken"/> is cancelled. On a web checkout Flock already holds what the player bought: the player's checkouts still open are read again (each one paid goes through <see cref="PurchaseCompleted"/>), and both lists are empty. When the player signs out or changes while it runs, it ends with <see cref="OperationCanceledException"/> and sends none of the rest.</summary>
        public async Task<FlockStoreRestore> RestoreAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfUnusable();
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            if (_connection == null)
            {
                await _web.FindOpenCheckoutsAsync(cancellationToken);
                return new FlockStoreRestore(new List<ShopItem>(), new List<FlockUndeliveredPurchase>());
            }
            // The sign-in the restore is for: the store's purchases are never sent as a player who signed in meanwhile.
            int signIn = _client.SignInNumber;
            // The player asked: an unfinished purchase whose send this launch has ended goes again when the fetch hands it over.
            _sentThisLaunch.RemoveWhere(transaction => !_sendingNow.Contains(transaction));
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
                if (!_itemByProduct.TryGetValue(order.ProductId, out ShopItem item) && !_soldBefore.ContainsKey(order.ProductId))
                    continue;
                try
                {
                    item = item ?? await FlockClient.Instance.Shop.GetItemAsync(_soldBefore[order.ProductId], cancellationToken);
                    if (_client.SignInNumber != signIn)
                        throw new OperationCanceledException("The player signed out or changed while the store's purchases were restored, so the rest were not sent");
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

        /// <summary>Checks the store setup without buying anything: which real-money items the store did not find or have no product id for this store, and whether Flock's server can ask the store about a purchase with the game's keys (it is asked about a made-up purchase, which the store answers as unknown when the keys work). On a web checkout nothing can be asked without opening a checkout, so <see cref="FlockStoreSetupCheck.NotChecked"/> says how to check the gateway. Needs a signed-in player.</summary>
        public async Task<FlockStoreSetupCheck> CheckSetupAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfUnusable();
            if (!FlockClient.Instance.IsAuthenticated)
                throw new FlockAuthException("No player is signed in") { Hint = FlockErrorHints.SignInFirst };
            if (_connection == null)
                return new FlockStoreSetupCheck(new List<ShopItem>(), new List<ShopItem>(), false, null, WebGatewayNotChecked);
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

        // A shop item read when this connection was made, or a refusal saying why there is none.
        private ShopItem KnownItem(string shopItemId)
        {
            if (string.IsNullOrEmpty(shopItemId))
                throw new FlockValidationException("Shop Item ID is empty") { Hint = "Pass the shop item's Id (ShopItem.Id)." };
            if (!_itemsById.TryGetValue(shopItemId, out ShopItem item))
                throw new FlockValidationException("That shop item was not in the game's shops when the store connected") { Hint = "Pass a ShopItem.Id from this game's shops. An item added in the Flock dashboard since then is sold once this connection is disposed and ConnectAsync runs again; an item or shop switched off (Inactive) there is not sold." };
            return item;
        }

        // The store's product for a shop item this connection sells, or a refusal saying why there is none.
        private string ProductOf(string shopItemId, out ShopItem item)
        {
            item = KnownItem(shopItemId);
            if (!item.StoreProductIds.TryGetValue(PaymentProvider, out string productId) || !IsSoldHere(item))
                throw new FlockValidationException($"This shop item has no {PaymentProvider} product id") { Hint = $"Enter the item's {PaymentProvider} product id under Store product IDs on the shop item in the Flock dashboard, exactly as the store has it, then connect again." };
            return productId;
        }

        // A purchase no store issued: the store answers it as unknown when Flock's keys work, and refuses the keys otherwise.
        private StoreReceipt MadeUpReceipt(string productId)
            => PaymentProvider == PaymentProviders.AppleAppStore
                ? StoreReceipt.FromAppleAppStore("0")
                : StoreReceipt.FromGooglePlay(productId, "flock-setup-check");

        /// <summary>Stops listening to the store, the web checkouts and sign-ins; <c>Shop.PurchaseAsync</c> no longer buys real-money items through it, and one still waiting ends as cancelled. A web checkout paid afterwards is still delivered by Flock, without <see cref="PurchaseCompleted"/>: refresh the wallet and inventory after each sign-in, or read <c>Shop.GetPurchasesAsync(PurchaseResultStatuses.Paid)</c>.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_connected == this)
                _connected = null;
            if (_client != null && _client.Shop != null && _client.Shop.ConnectedStore == this)
                _client.Shop.ConnectedStore = null;
            _web?.Dispose();
            if (_connection != null)
            {
                _connection.OrderPaid -= HandlePaid;
                _connection.OrderDeferred -= HandleDeferred;
                _connection.OrderFailed -= HandleFailed;
                _connection.StopListening();
            }
            FlockEvents.OnAuthenticated -= HandleSignedIn;
            FlockEvents.OnLoggedOut -= EndTheWaitsOfAnEndedSignIn;
            FlockEvents.OnAuthExpired -= EndTheWaitsOfAnEndedSignIn;
            FlockEvents.OnShutdown -= EndTheWaitsAsFlockShutsDown;
            _waiting.Clear();
            foreach (Buying buying in new List<Buying>(_buying.Values))
                buying.Done.TrySetCanceled();
            _buying.Clear();
        }

        // Without Unity IAP 5 in the project, what to install; with it, its Flock connection (Flock.Shop.UnityIap) was left out of the build.
        private static FlockValidationException NoStoreLibrary(string paymentProvider)
        {
            const string NeverAWebPage = " A phone's store rules forbid paying for digital goods elsewhere, so Flock never opens a web checkout instead.";
#if FLOCK_UNITY_IAP_5
            return new FlockValidationException($"'{paymentProvider}' is reached through Flock's Unity IAP 5 connection, which this build left out, so nothing was connected")
            {
                Hint = "Keep the assembly Flock.Shop.UnityIap in the build: a link.xml that strips it (or every assembly it does not name) drops it, and so does FLOCK_NO_SHOP in the Scripting Define Symbols." + NeverAWebPage
            };
#else
            return new FlockValidationException($"'{paymentProvider}' is reached through Unity IAP 5, which this build does not have, so nothing was connected")
            {
                Hint = "Install In-App Purchasing 5 (com.unity.purchasing 5.x) from the Package Manager." + NeverAWebPage + " A game on Unity IAP 4 sends its store purchases with Shop.RedeemStoreReceiptAsync, as the SDK's Unity IAP 4 recipe does."
            };
#endif
        }

        private static bool IsAStore(string paymentProvider)
            => paymentProvider == PaymentProviders.GooglePlay || paymentProvider == PaymentProviders.AppleAppStore;

        private const string WebGatewayNotChecked = "The web gateway: Flock's server says whether one is connected only as a checkout opens. Buy a real-money item with the gateway's test keys connected in the Flock dashboard and its test card.";

        // Currencies Flock's server counts in thousandths or in whole units; every other one in hundredths.
        private static readonly HashSet<string> ThreeDigitCurrencies = new HashSet<string> { "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND" };
        private static readonly HashSet<string> WholeUnitCurrencies = new HashSet<string> { "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };

        // A web checkout charges the item's own price, kept in the currency's smallest unit.
        private static FlockStorePrice PriceOfTheItem(ShopItem item)
        {
            if (!item.IsRealMoney)
                throw new FlockValidationException($"'{item.Name}' is priced in a game currency, so it has no real-money price") { Hint = "Show a game-currency item's Price and Currency as they are; GetPrice is for real-money items." };
            string currency = (item.Currency ?? string.Empty).ToUpperInvariant();
            int digits = ThreeDigitCurrencies.Contains(currency) ? 3 : WholeUnitCurrencies.Contains(currency) ? 0 : 2;
            decimal amount = item.Price;
            for (int i = 0; i < digits; i++)
                amount /= 10m;
            return new FlockStorePrice(currency + " " + amount.ToString("N" + digits, CultureInfo.CurrentCulture), amount, currency);
        }

        private async Task<List<ShopItem>> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            List<ShopItem> catalog = new List<ShopItem>();
            int page = 1;
            while (true)
            {
                PaginatedResponse<Shop> shops = await FlockClient.Instance.Shop.GetAllAsync(page, 100, cancellationToken);
                if (shops?.Items == null || shops.Items.Length == 0)
                    return catalog;
                foreach (Shop shop in shops.Items)
                {
                    if (shop?.ShopItems == null)
                        continue;
                    catalog.AddRange(shop.ShopItems);
                    foreach (ShopItem item in shop.ShopItems)
                        if (item?.Id != null)
                            _shopOfItem[item.Id] = shop.Id;
                }
                if (page * 100 >= shops.Total)
                    return catalog;
                page++;
            }
        }

        // The items this connection sells and prices.
        private void KnowItems(List<ShopItem> catalog)
        {
            foreach (ShopItem item in catalog)
                if (item != null && !string.IsNullOrEmpty(item.Id))
                    _itemsById[item.Id] = item;
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

        // A product as kept between launches: the item it was sold for, and its kind of store product.
        internal sealed class SoldProduct
        {
            public string ShopItemId { get; set; }
            public bool Consumable { get; set; }
        }

        private const string SoldProductsScope = FlockSnapshotStore.StateScope + "/store";
        private const string NotForSaleCode = "shop.item_not_for_sale";

        // Keeps this launch's products with those sold before (per game and store), and hands back the ones no item sells now, so the store hands their purchases back.
        private List<FlockStoreProduct> RememberProducts()
        {
            List<FlockStoreProduct> soldBefore = new List<FlockStoreProduct>();
            FlockSnapshotStore disk = _client.SnapshotStore;
            if (disk == null)
                return soldBefore;
            string key = FlockGameKey.Named("products-" + PaymentProvider, _client.InitConfig.GameKey);
            if (!disk.TryRead(SoldProductsScope, key, out Dictionary<string, SoldProduct> sold) || sold == null)
                sold = new Dictionary<string, SoldProduct>();
            foreach (KeyValuePair<string, SoldProduct> product in sold)
            {
                if (_itemByProduct.ContainsKey(product.Key) || _productsSoldTwice.Contains(product.Key) || string.IsNullOrEmpty(product.Value?.ShopItemId))
                    continue;
                _soldBefore[product.Key] = product.Value.ShopItemId;
                soldBefore.Add(new FlockStoreProduct(product.Key, product.Value.Consumable));
            }
            foreach (KeyValuePair<string, ShopItem> product in _itemByProduct)
                sold[product.Key] = new SoldProduct { ShopItemId = product.Value.Id, Consumable = product.Value.Type != ShopItemTypes.Standard };
            disk.Write(SoldProductsScope, key, sold);
            return soldBefore;
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
            if (_connection == null)
            {
                _web = new FlockWebCheckout(_client, _options, RaiseCompleted);
                _web.Start();
                return;
            }
            FlockEvents.OnAuthenticated += HandleSignedIn;
            FlockEvents.OnLoggedOut += EndTheWaitsOfAnEndedSignIn;
            FlockEvents.OnAuthExpired += EndTheWaitsOfAnEndedSignIn;
            FlockEvents.OnShutdown += EndTheWaitsAsFlockShutsDown;
            if (FlockClient.Instance.IsAuthenticated)
                HandleSignedIn(null);
        }

        // A purchase call's wait belongs to the sign-in it opened the store's dialog under; the store's purchase is held and sent again at a later sign-in.
        private void EndTheWaitsOfAnEndedSignIn()
        {
            foreach (Buying buying in new List<Buying>(_buying.Values))
            {
                if (buying.SignIn.HasValue && buying.SignIn != _client.SignInNumber)
                    buying.Done.TrySetException(new OperationCanceledException("The player signed out or changed while the store's purchase was waited for; once paid, it is delivered to the player who bought it at a later sign-in of theirs, or through RestoreAsync"));
            }
        }

        // Flock shutting down ends every purchase call's wait, as Dispose does; this connection no longer acts, and the store hands a purchase paid later back to the next one.
        private void EndTheWaitsAsFlockShutsDown()
        {
            foreach (Buying buying in new List<Buying>(_buying.Values))
                buying.Done.TrySetCanceled();
        }

        // Purchases that waited for a sign-in go now, and the store is asked for any it still holds (Google hands them back only when asked).
        private void HandleSignedIn(FlockAuthInfo signIn)
        {
            if (_disposed)
                return;
            EndTheWaitsOfAnEndedSignIn();
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
            if (item == null && !_itemByProduct.TryGetValue(order.ProductId, out item) && !_productsSoldTwice.Contains(order.ProductId) && !_soldBefore.ContainsKey(order.ProductId))
                return; // Not a Flock item's product: the game finishes its own purchases.
            if (!FlockClient.Instance.IsAuthenticated)
            {
                Hold(order);
                return;
            }
            if (!_sentThisLaunch.Add(order.TransactionId))
                return;
            if (item == null && _soldBefore.TryGetValue(order.ProductId, out string soldItemId))
            {
                SendForAnItemNoLongerSold(order, soldItemId);
                return;
            }
            if (item == null)
            {
                RaiseNotDelivered(null, order.ProductId, SoldTwice(order.ProductId, " The purchase stays unfinished in the store and is matched at the next launch."));
                return;
            }
            Send(order, item, buying);
        }

        private async void Send(FlockStoreOrder order, ShopItem item, Buying buying)
        {
            _sendingNow.Add(order.TransactionId);
            PurchaseResult delivery;
            try
            {
                delivery = await FlockClient.Instance.Shop.RedeemStoreReceiptAsync(item.Id, ReceiptOf(order));
            }
            catch (FlockException refused)
            {
                _sendingNow.Remove(order.TransactionId);
                buying?.Done.TrySetException(refused);
                RaiseNotDelivered(item, order.ProductId, refused);
                return;
            }
            catch (OperationCanceledException)
            {
                // The player signed out or changed while it was sent: it stays unfinished for the next launch.
                _sendingNow.Remove(order.TransactionId);
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
            _sendingNow.Remove(order.TransactionId);
            buying?.Done.TrySetResult(delivery);
            RaiseCompleted(item, delivery);
        }

        private void RaiseCompleted(ShopItem item, PurchaseResult delivery)
            => FlockEvents.InvokeEach(PurchaseCompleted, item, delivery, $"{nameof(FlockStore)}.{nameof(PurchaseCompleted)}");

        // Paid for an item the shops no longer list: its own read still finds a switched-off item, and Flock delivers what was paid for or says why not.
        private async void SendForAnItemNoLongerSold(FlockStoreOrder order, string shopItemId)
        {
            _sendingNow.Add(order.TransactionId);
            ShopItem item;
            try
            {
                item = await FlockClient.Instance.Shop.GetItemAsync(shopItemId);
            }
            catch (FlockException notRead)
            {
                _sendingNow.Remove(order.TransactionId);
                RaiseNotDelivered(null, order.ProductId, notRead);
                return;
            }
            Send(order, item, null);
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
                case FlockStoreFailureReasons.StoreUnavailable: return "Nothing was charged. Check the device is online and that purchases are allowed on it; on Google Play the payment method may also have been declined, so the player can choose another in the store. Then try again.";
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
