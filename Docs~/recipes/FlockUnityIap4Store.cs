// Unity IAP 4.x with Flock: every purchase is held unfinished until Flock has delivered it, then confirmed.
// Copy this file into a game that uses Unity IAP 4.x; Unity IAP 5 games use FlockStore instead.
// Compiled against Unity IAP 4.15.1 by Qwacks/Libraries/Unity/payment-store-spike/check_iap4_recipe.py.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Extension;

public class FlockUnityIap4Store : IDetailedStoreListener
{
    // The game's shop items, by this store's product id (ShopItem.StoreProductIds).
    private readonly Dictionary<string, string> _shopItemIdByProduct = new Dictionary<string, string>();
    private IStoreController _controller;
    private string _appleAccountToken;

    // Reads the game's shops, registers each item that has a product id for this store, and starts Unity IAP.
    // Call it once a player is signed in, so the store's purchases carry that player's account token.
    public async Task InitializeAsync()
    {
        string store = Application.platform == RuntimePlatform.Android ? PaymentProviders.GooglePlay : PaymentProviders.AppleAppStore;
        string accountToken = await FlockClient.Instance.Shop.GetStoreAccountTokenAsync();
        ConfigurationBuilder builder = ConfigurationBuilder.Instance(StandardPurchasingModule.Instance());
        PaginatedResponse<Shop> shops = await FlockClient.Instance.Shop.GetAllAsync();
        foreach (Shop shop in shops.Items)
            foreach (ShopItem item in shop.ShopItems)
                if (item.StoreProductIds.TryGetValue(store, out string productId))
                {
                    _shopItemIdByProduct[productId] = item.Id;
                    builder.AddProduct(productId, item.Type == ShopItemTypes.Standard ? ProductType.NonConsumable : ProductType.Consumable);
                }
        builder.Configure<IGooglePlayConfiguration>().SetObfuscatedAccountId(accountToken);
        _appleAccountToken = accountToken;
        UnityPurchasing.Initialize(this, builder);
    }

    public void OnInitialized(IStoreController controller, IExtensionProvider extensions)
    {
        _controller = controller;
        extensions.GetExtension<IAppleExtensions>().SetApplicationUsername(_appleAccountToken);
    }

    public void Buy(string productId) => _controller.InitiatePurchase(productId);

    // Pending keeps the purchase unfinished; Unity IAP hands it back here at the next launch if Flock did not deliver it.
    public PurchaseProcessingResult ProcessPurchase(PurchaseEventArgs purchaseEvent)
    {
        _ = RedeemThenConfirmAsync(purchaseEvent.purchasedProduct);
        return PurchaseProcessingResult.Pending;
    }

    private async Task RedeemThenConfirmAsync(Product product)
    {
        string productId = product.definition.storeSpecificId;
        if (!_shopItemIdByProduct.TryGetValue(productId, out string shopItemId))
            return; // Not a Flock item: the game finishes it its own way.
        StoreReceipt receipt = Application.platform == RuntimePlatform.Android
            ? StoreReceipt.FromGooglePlay(productId, product.transactionID)
            : StoreReceipt.FromAppleAppStore(product.transactionID);
        try
        {
            PurchaseResult delivered = await FlockClient.Instance.Shop.RedeemStoreReceiptAsync(shopItemId, receipt);
            _controller.ConfirmPendingPurchase(product);
            Debug.Log($"Flock delivered {shopItemId} ({delivered.PurchaseId})");
        }
        catch (Exception notDelivered)
        {
            // Left unfinished: the store hands it back at the next launch. A FlockException's ErrorCode and Hint say why.
            Debug.LogWarning($"Flock did not deliver {shopItemId}: {notDelivered.Message}");
        }
    }

    public void OnInitializeFailed(InitializationFailureReason error) => Debug.LogWarning($"Unity IAP did not start: {error}");
    public void OnInitializeFailed(InitializationFailureReason error, string message) => Debug.LogWarning($"Unity IAP did not start: {error} {message}");
    public void OnPurchaseFailed(Product product, PurchaseFailureReason failureReason) => Debug.LogWarning($"The store did not take the payment: {failureReason}");
    public void OnPurchaseFailed(Product product, PurchaseFailureDescription failureDescription) => Debug.LogWarning($"The store did not take the payment: {failureDescription.message}");
}
