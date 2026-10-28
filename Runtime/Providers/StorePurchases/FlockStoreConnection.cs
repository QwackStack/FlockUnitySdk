using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    // A purchase the store charged for and holds unfinished; Handle is the store library's own order.
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

    // What FlockStorePurchases needs of a store library; Unity IAP 5 is the one a game uses.
    internal interface IFlockStoreConnection
    {
        event Action<FlockStoreOrder> OrderPaid;
        event Action<FlockStoreOrder> OrderDeferred;

        // The product, the store's reason, and whether the player cancelled.
        event Action<string, string, bool> OrderFailed;

        Task ConnectAsync();
        Task RegisterProductsAsync(IReadOnlyList<FlockStoreProduct> products, CancellationToken cancellationToken);
        void SetAccountToken(string accountToken);
        void Buy(string productId);
        void Finish(FlockStoreOrder order);
        void FetchUnfinishedPurchases();
        void StopListening();
    }

    // Unity IAP 5's StoreController, through its order API: an order is finished by itself, never by its product.
    internal sealed class FlockUnityIapConnection : IFlockStoreConnection
    {
        private readonly StoreController _store = UnityIAPServices.StoreController();

        public event Action<FlockStoreOrder> OrderPaid;
        public event Action<FlockStoreOrder> OrderDeferred;
        public event Action<string, string, bool> OrderFailed;

        public FlockUnityIapConnection()
        {
            _store.OnPurchasePending += HandlePending;
            _store.OnPurchaseDeferred += HandleDeferred;
            _store.OnPurchaseFailed += HandleFailed;
            _store.OnPurchasesFetched += HandleFetched;
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

        public void StopListening()
        {
            _store.OnPurchasePending -= HandlePending;
            _store.OnPurchaseDeferred -= HandleDeferred;
            _store.OnPurchaseFailed -= HandleFailed;
            _store.OnPurchasesFetched -= HandleFetched;
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
            => OrderFailed?.Invoke(ProductIdOf(order), $"{order.FailureReason}: {order.Details}", order.FailureReason == PurchaseFailureReason.UserCancelled);

        private static string ProductIdOf(Order order)
        {
            IReadOnlyList<CartItem> items = order.CartOrdered?.Items();
            return items != null && items.Count > 0 ? items[0].Product?.definition?.storeSpecificId : null;
        }
    }
}
