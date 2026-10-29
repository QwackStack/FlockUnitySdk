using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock.Exceptions;
using UnityEngine.Purchasing;

namespace Flock.Providers
{
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

    // Unity IAP 5's StoreController, through its order API: an order is finished by itself, never by its product.
    internal sealed class FlockUnityIapConnection : IFlockStoreConnection
    {
        private readonly StoreController _store = UnityIAPServices.StoreController();

        public event Action<FlockStoreOrder> OrderPaid;
        public event Action<FlockStoreOrder> OrderDeferred;
        public event Action<FlockStoreFailure> OrderFailed;

        public FlockUnityIapConnection()
        {
            _store.OnPurchasePending += HandlePending;
            _store.OnPurchaseDeferred += HandleDeferred;
            _store.OnPurchaseFailed += HandleFailed;
            _store.OnPurchasesFetched += HandleFetched;
            _store.OnStoreConnected += HandleConnected;
            _store.OnStoreDisconnected += HandleDisconnected;
            _store.OnPurchasesFetchFailed += HandleFetchFailed;
        }

        public Task ConnectAsync() => _store.Connect();

        public async Task RegisterProductsAsync(IReadOnlyList<FlockStoreProduct> products, CancellationToken cancellationToken)
        {
            if (products.Count == 0)
                return;
            List<ProductDefinition> definitions = new List<ProductDefinition>(products.Count);
            foreach (FlockStoreProduct product in products)
                definitions.Add(new ProductDefinition(product.ProductId, product.Consumable ? ProductType.Consumable : ProductType.NonConsumable));

            TaskCompletionSource<bool> answered = new TaskCompletionSource<bool>();
            Action<List<Product>> found = list => answered.TrySetResult(true);
            Action<ProductFetchFailed> missing = failure =>
            {
                FlockClient.Instance.Logger.LogWarning($"The store could not find {failure.FailedFetchProducts.Count} of Flock's products: {failure.FailureReason}");
                answered.TrySetResult(false);
            };
            _store.OnProductsFetched += found;
            _store.OnProductsFetchFailed += missing;
            try
            {
                // The store retries a fetch it cannot make; the caller's token ends the wait.
                using (cancellationToken.Register(() => answered.TrySetCanceled()))
                {
                    _store.FetchProducts(definitions);
                    await answered.Task;
                }
            }
            finally
            {
                _store.OnProductsFetched -= found;
                _store.OnProductsFetchFailed -= missing;
            }
        }

        // The editor's fake store has neither extension, so there the token is not set.
        public void SetAccountToken(string accountToken)
        {
            if (Guid.TryParse(accountToken, out Guid appleToken))
                _store.AppleStoreExtendedService?.SetAppAccountToken(appleToken);
            _store.GooglePlayStoreExtendedService?.SetObfuscatedAccountId(accountToken);
        }

        public void Buy(string productId) => _store.PurchaseProduct(productId);

        public void Finish(FlockStoreOrder order) => _store.ConfirmPurchase((PendingOrder)order.Handle);

        public void FetchUnfinishedPurchases() => _store.FetchPurchases();

        public FlockStorePrice PriceOf(string productId)
        {
            ProductMetadata metadata = _store.GetProductById(productId)?.metadata;
            if (metadata == null || string.IsNullOrEmpty(metadata.localizedPriceString))
                return null;
            return new FlockStorePrice(metadata.localizedPriceString, metadata.localizedPrice, metadata.isoCurrencyCode);
        }

        public async Task<IReadOnlyList<FlockStoreOrder>> FetchOwnedPurchasesAsync(CancellationToken cancellationToken)
        {
            // Apple hands restored purchases back only once asked; Google lists every owned purchase on any fetch, and a store with
            // no restore of its own (the editor's) answers that it has none: only Apple's answer can be a failure (a sign-in refused).
            TaskCompletionSource<string> restored = new TaskCompletionSource<string>();
            _store.RestoreTransactions((done, message) => restored.TrySetResult(done || _store.AppleStoreExtendedService == null ? null : message ?? "no reason given"));
            TaskCompletionSource<IReadOnlyList<FlockStoreOrder>> fetched = new TaskCompletionSource<IReadOnlyList<FlockStoreOrder>>();
            Action<Orders> listed = orders =>
            {
                List<FlockStoreOrder> owned = new List<FlockStoreOrder>();
                foreach (ConfirmedOrder order in orders.ConfirmedOrders)
                    owned.Add(new FlockStoreOrder(ProductIdOf(order), order.Info?.TransactionID, order));
                fetched.TrySetResult(owned);
            };
            Action<PurchasesFetchFailureDescription> failed = failure =>
                fetched.TrySetException(new FlockException($"The store could not list this account's purchases: {failure.failureReason} {failure.message}")
                {
                    Hint = "Check the device is online and signed in to the store, then restore again."
                });
            using (cancellationToken.Register(() => { restored.TrySetCanceled(); fetched.TrySetCanceled(); }))
            {
                string notRestored = await restored.Task;
                if (notRestored != null)
                    throw new FlockException($"The store did not restore this account's purchases: {notRestored}") { Hint = "Sign in to the store when it asks, then restore again." };
                _store.OnPurchasesFetched += listed;
                _store.OnPurchasesFetchFailed += failed;
                try
                {
                    _store.FetchPurchases();
                    return await fetched.Task;
                }
                finally
                {
                    _store.OnPurchasesFetched -= listed;
                    _store.OnPurchasesFetchFailed -= failed;
                }
            }
        }

        public void StopListening()
        {
            _store.OnPurchasePending -= HandlePending;
            _store.OnPurchaseDeferred -= HandleDeferred;
            _store.OnPurchaseFailed -= HandleFailed;
            _store.OnPurchasesFetched -= HandleFetched;
            _store.OnStoreConnected -= HandleConnected;
            _store.OnStoreDisconnected -= HandleDisconnected;
            _store.OnPurchasesFetchFailed -= HandleFetchFailed;
        }

        // The connect is awaited on its own task; listened to so Unity IAP does not warn that nobody listens.
        private static void HandleConnected() { }

        private static void HandleDisconnected(StoreConnectionFailureDescription failure)
        {
            if (FlockClient.IsInitialized)
                FlockClient.Instance.Logger.LogWarning($"The store disconnected ({failure.message}): purchases fail until it connects again. Check the device is online and signed in to the store.");
        }

        private static void HandleFetchFailed(PurchasesFetchFailureDescription failure)
        {
            if (FlockClient.IsInitialized)
                FlockClient.Instance.Logger.LogWarning($"The store could not list its unfinished purchases ({failure.failureReason}: {failure.message}); they are asked for again at the next sign-in, and the store hands them back at the next launch.");
        }

        private void HandlePending(PendingOrder order) => OrderPaid?.Invoke(new FlockStoreOrder(ProductIdOf(order), order.Info.TransactionID, order));

        // Read from the fetch itself, so the game's own Unity IAP settings stay as the game set them.
        private void HandleFetched(Orders orders)
        {
            foreach (PendingOrder order in orders.PendingOrders)
                HandlePending(order);
        }

        private void HandleDeferred(DeferredOrder order) => OrderDeferred?.Invoke(new FlockStoreOrder(ProductIdOf(order), order.Info?.TransactionID, order));

        private void HandleFailed(FailedOrder order)
            => OrderFailed?.Invoke(new FlockStoreFailure(ProductIdOf(order), ReasonOf(order.FailureReason), $"{order.FailureReason}: {order.Details}"));

        internal static string ReasonOf(PurchaseFailureReason reason)
        {
            switch (reason)
            {
                case PurchaseFailureReason.UserCancelled: return FlockStoreFailureReasons.Cancelled;
                case PurchaseFailureReason.DuplicateTransaction: return FlockStoreFailureReasons.AlreadyOwned;
                case PurchaseFailureReason.ExistingPurchasePending: return FlockStoreFailureReasons.AlreadyPending;
                case PurchaseFailureReason.PaymentDeclined: return FlockStoreFailureReasons.PaymentDeclined;
                case PurchaseFailureReason.ProductUnavailable: return FlockStoreFailureReasons.ProductUnavailable;
                case PurchaseFailureReason.PurchasingUnavailable:
                case PurchaseFailureReason.StoreNotConnected: return FlockStoreFailureReasons.StoreUnavailable;
                // Unity IAP 5.4 added UserNotAuthenticated, so it is read by name and 5.3 still compiles.
                default: return reason.ToString() == "UserNotAuthenticated" ? FlockStoreFailureReasons.NotSignedInToTheStore : FlockStoreFailureReasons.Unknown;
            }
        }

        private static string ProductIdOf(Order order)
        {
            IReadOnlyList<CartItem> items = order.CartOrdered?.Items();
            return items != null && items.Count > 0 ? items[0].Product?.definition?.storeSpecificId : null;
        }
    }
}
