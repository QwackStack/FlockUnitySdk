# Shop

[← Back to README](../README.md)

```csharp
var shops = await FlockClient.Instance.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await FlockClient.Instance.Shop.GetByIdAsync("shop-id");
var item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
var items = await FlockClient.Instance.Shop.GetItemsByShopAsync("shop-id");
// Same retry contract as AddGameFunds (money mutation) — ambiguous failures throw; catch them.
// Started, Purchased and Failed transactions are recorded as it goes, never waited on. Catch FlockException and check
// e.ErrorCode for specific reasons (e.g. FlockErrorCode.ShopInsufficientFunds, ShopWalletNotFound); e.Message says what
// went wrong and, on a "Fix:" line, how to fix it.
// It takes the shop item's Id (ShopItem.Id, from the shop's ShopItems): not the item's name, and not the shop's id.
// playerId is optional — omit it to use the signed-in player (CurrentPlayerId).
PurchaseResult purchase = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);
var playerItems = await FlockClient.Instance.Shop.GetPlayerInventoryAsync();
```

## Rewards

Some shop items grant something the moment they are bought; others land in the inventory and grant when
consumed. Both paths report what was handed out, so you can show the player their reward without a
follow-up read.

```csharp
PurchaseResult purchase = await FlockClient.Instance.Shop.PurchaseAsync("shop-item-id");
foreach (ShopItemReward reward in purchase.Granted)
    Debug.Log($"+{reward.Amount} {reward.Code}");

// The inventory row moved under .Inventory; the wallet after the grant rides along when currency moved.
PlayerInventory row = purchase.Inventory;
PlayerData wallet = purchase.Wallet;

// Consume an owned item to collect its rewards.
ConsumeResult consumed = await FlockClient.Instance.Shop.ConsumeAsync(row.Id);
foreach (ShopItemReward reward in consumed.Granted)
    Debug.Log($"+{reward.Amount} {reward.Code}");

// What an item will grant, before buying it.
ShopItem item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
foreach (ShopItemReward reward in item.Rewards)
    Debug.Log($"advertises +{reward.Amount} {reward.Code}");
```

`Granted` and `Rewards` are never null — an item that grants nothing yields an empty list. `reward.Type`
is a string compared against `ShopItemRewardTypes.Currency` rather than an enum: rewards are stored as
typed entries so new kinds can appear without a schema migration, and an unknown value must not break
deserialization.

`PurchaseAsync` spends only game currencies from the player's wallet, so a purchase it makes is paid at once and its
`Status` reads `paid` (`IsPaid` is true). An item priced in a currency the game's currency config lists as real money is
refused with `FlockErrorCode.ShopRealCurrencyNeedsCheckout` (the server reads that list for the Game Version picked in
Flock > Settings): price it in a game currency, or leave it out of what the game sells this way. `Status` is still one of `PurchaseResultStatuses` (`pending`, `paid`, `failed`, `expired`, `refunded`), a
string for the same reason as `reward.Type`, and reads `paid` when the server leaves it out. A purchase or consume that
reports a wallet also writes it into the cached player data, so the next `GetMyDataByTemplateAsync` reads the new balance
rather than the one from before.

`ConsumeAsync` moves currency, so it carries the same money safety as a purchase — an ambiguous failure
throws rather than being re-sent, and only provably-unprocessed failures (408/429) retry. Catch
`FlockException` around it. Neither call is queued while the network is down: each is sent at most once (a
purchase of an item not read yet fails on reading it first), throws `FlockNetworkException`, and is not sent again
when the network comes back.

> Inventory is deliberately **never offline-cached** — it changes on every purchase, so the SDK always
> reads it fresh and there is no stale copy to fall back on when the network is down. Reading it offline
> throws `FlockNetworkException`.

## Real-money items

Each item says whether it costs real money and what it is called in each store. The server sends both with the catalog,
from the game's currency config and the product ids set in the Flock dashboard; like the rest of the catalog, they are kept
for the session (and offline), so a change made in the dashboard shows after `Shop.ClearCache()` or on the next launch with
a connection.

```csharp
ShopItem item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
if (item.IsRealMoney)
{
    // Paid through a store or a web checkout, never from the wallet: PurchaseAsync refuses it.
    if (item.StoreProductIds.TryGetValue(PaymentProviders.GooglePlay, out string productId))
        Debug.Log($"Sold on Google Play as {productId}");
}
```

`IsRealMoney` is true when the item's currency is one the currency config lists as real money; a server that does not
send it reads false. For such an item `Price` is in the currency's smallest unit (199 is USD 1.99), but a phone shows the
player the store's own price for the product, in their currency and with their taxes, so show that one there.
`StoreProductIds` is keyed by store name (`PaymentProviders.AppleAppStore`, `PaymentProviders.GooglePlay`), holds each id
exactly as set in the dashboard, and is never null: an item with no product id reads an empty map. `Type` is one of
`ShopItemTypes`: in a store, a `CurrencyPack` or `Consumable` item is a consumable product and a `Standard` item a one-time
product. Store names and item kinds are strings rather than enums, for the same reason as `reward.Type`.

See also: [Codegen](codegen.md) for typed shop accessors and the `FlockShopItemId` / `FlockFundId` enums, and [Player Data & Game Commands](player-data.md) for `AddGameFundsAsync`.
