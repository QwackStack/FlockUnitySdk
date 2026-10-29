# Shop

[← Back to README](../README.md)

```csharp
var shops = await FlockClient.Instance.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await FlockClient.Instance.Shop.GetByIdAsync("shop-id");
var item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
var items = await FlockClient.Instance.Shop.GetItemsByShopAsync("shop-id");
// Sent again, with the key every attempt carries, after an answer that never came: the server answers a repeat with the first
// purchase. Started, Purchased and Failed transactions are recorded as it goes, never waited on. Catch FlockException and check
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
Flock > Settings): price it in a game currency, or sell it through a store and redeem the store's purchase (see "Store
purchases" below). `Status` is still one of `PurchaseResultStatuses` (`pending`, `paid`, `failed`, `expired`, `refunded`), a
string for the same reason as `reward.Type`, and reads `paid` when the server leaves it out. A purchase or consume that
reports a wallet also writes it into the cached player data, so the next `GetMyDataByTemplateAsync` reads the new balance
rather than the one from before.

Every attempt of a `PurchaseAsync` call carries the same key (`idempotency_key`), so after an answer that never came (a
dropped connection, a timeout, a server error) the purchase is sent again under the client's retry settings, and the server
answers the repeat with the first purchase: the player pays once. A new call is a new purchase. When no attempt is answered the
call throws, and the purchase may have been made: read the wallet and inventory before offering it again. The server must read
the key (Flock's servers from 2026-10-26 on); an older one ignores it. `ConsumeAsync` carries no key, so an ambiguous failure
throws rather than being re-sent, and only provably unprocessed failures (408/429) retry. Catch `FlockException` around both.
Neither call is queued while the network is down: each throws `FlockNetworkException` (a purchase of an item not read yet
fails on reading it first) and is not sent again when the network comes back.

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

## A shop screen

Three reads give a shop screen what it shows next to each item: the player's balances, what they own, and whether they can pay.

```csharp
FlockWallet wallet = await FlockClient.Instance.Player.GetWalletAsync();
IReadOnlyDictionary<string, int> owned = await FlockClient.Instance.Shop.GetOwnedItemCountsAsync();

foreach (ShopItem item in shop.ShopItems)
{
    owned.TryGetValue(item.Id, out int count);
    bool alreadyHeld = item.Type == ShopItemTypes.Standard && count > 0;  // a one-time item bought and not used up
    bool payable = item.IsRealMoney || wallet.CanAfford(item);              // grey out a wallet item the player cannot pay for
}

try
{
    PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);
}
catch (FlockException e) when (e.ErrorCode == FlockErrorCode.ShopInsufficientFunds)
{
    // Not enough of the item's currency: nothing was bought.
}
catch (FlockNetworkException e)
{
    // No answer came: read the wallet and inventory again to see whether it went through.
}
```

- **`GetWalletAsync`** reads the player data of the template tagged `currency`: `GameCurrencies` and `RealCurrencies` by code,
  `BalanceOf(code)` (0 when not held) and `Holds(code)`. It is read through the player-data cache, which purchases, store
  redemptions and fund grants keep current. A player with no wallet row (registered before the currency template existed, or
  under another Game Version) throws `FlockValidationException` saying so. `FlockWallet.From(purchase.Wallet)` reads the wallet
  a purchase returned.
- **`CanAfford(item)`** is true for an item paid from the wallet whose currency the wallet holds at least `Price` of. It reads
  false for a real-money item, which is bought through a store. Codes match exactly, as the server matches them.
- **`GetOwnedItemCountsAsync`** counts the unused inventory entries of each item, from every page; used and refunded entries
  do not count. `OwnsAsync(shopItemId)` is the same read for one item. The inventory is never cached, so read it once for a
  screen, and again after a purchase.

The `Samples/Shop` folder has a one-screen shop built on these calls (`FlockShopSample`), with store items when Unity IAP 5 is
installed.

## Store purchases

When the App Store or Google Play has charged the player, hand Flock the store's purchase and it delivers the item: the
server asks the store itself, checks the product is the item's `StoreProductIds` entry for that store, and grants the item
to the signed-in player.

```csharp
// Apple: StoreKit's transaction id. Google Play: the product id and the purchase token.
StoreReceipt receipt = StoreReceipt.FromGooglePlay(productId, purchaseToken);
try
{
    PurchaseResult purchase = await FlockClient.Instance.Shop.RedeemStoreReceiptAsync(item.Id, receipt);
    // Delivered: now finish the purchase in the store library (consume or acknowledge it).
}
catch (FlockException e)
{
    // Not delivered: leave the store's purchase unfinished, so the store hands it back at the next launch.
    // e.ErrorCode says why; PaymentProviderStoreUnavailable and PaymentProviderReceiptPending mean "send it again later".
}
```

- **Finish the store's purchase only once this call returns.** A refusal leaves it unfinished, and the store hands it back
  to the game at the next launch to be sent again; Google refunds a purchase nobody finished after three days.
- **It is safe to send again.** One store purchase makes one Flock purchase, so a repeat (a lost answer, a second launch)
  returns what the first delivered, `Granted` included, and grants nothing more. The SDK sends it again by itself after an
  answer that never came or a store that could not be asked, under the client's retry settings; keys the server cannot read
  are not sent again. A wallet purchase is sent again too, with its key; a consume never is.
- **It acts for the signed-in player** and throws `FlockAuthException` when nobody is signed in. If the player signs out or
  changes while it is being sent again, the next attempt is not sent and the call ends with `OperationCanceledException`.
- **It is never queued offline.** The store's unfinished purchase is the queue: send it again when the store hands it back.
- The ids are sent exactly as given; one with a space before or after it is refused rather than trimmed.
- `Status` reads `paid` for a delivered purchase. A receipt already redeemed and since refunded reads `refunded`: nothing
  more is delivered, and the store's purchase can be finished all the same. The wallet it reports is written into the
  cached player data, as a purchase's is.
- **Revenue is recorded by the server**, once per store purchase, at the item's `Price` and `Currency`; this call records no
  analytics transaction of its own (unlike `PurchaseAsync`, whose wallet purchases are never counted as revenue).

With Unity IAP 5 installed, `FlockStorePurchases` (below) makes this call for you.

## Buying with Unity IAP 5

With Unity IAP 5 in the project (`com.unity.purchasing` 5.x), `FlockStorePurchases` does a whole store purchase: the store
charges the player, Flock checks it with the store and delivers the item, and only then is the store's purchase finished.
It is compiled only when Unity IAP 5 is installed, so a game without it carries none of it.

```csharp
// Once, after FlockClient.Create. On a phone the store is found by itself.
FlockStorePurchases store = await FlockStorePurchases.ConnectAsync();
store.PurchaseDelivered += purchase => Debug.Log($"Delivered later: {purchase.Item.Name}");
store.PurchaseNeedsAttention += problem => Debug.LogWarning($"{problem.ProductId}: {problem.Error.Message}");  // a message ends with its fix

FlockStorePurchase purchase = await store.BuyAsync(item.Id);
switch (purchase.Outcome)
{
    case FlockStorePurchaseOutcome.Delivered: /* purchase.Delivery.Granted, .Wallet */ break;
    case FlockStorePurchaseOutcome.Pending: /* waiting for the payment or a parent's approval */ break;
    case FlockStorePurchaseOutcome.Cancelled: break;
    case FlockStorePurchaseOutcome.Failed:
        // StoreFailureReason is one of FlockStoreFailureReasons; StoreFailure is the store's own words, for the log.
        if (purchase.StoreFailureReason == FlockStoreFailureReasons.AlreadyOwned) { /* offer Restore */ }
        break;
}

// The price the store shows this player, in their currency with their taxes; null when the store did not find the product.
FlockStorePrice price = store.GetStorePrice(item.Id);
FlockStoreRestore restore = await store.RestoreAsync();          // one-time items this store account already owns
FlockStoreSetupCheck check = await store.CheckSetupAsync();      // what is missing from the setup, without buying anything
```

- **`ConnectAsync`** reads the game's shops and registers each item that has a product id for this store: a currency pack or
  consumable as a consumable product, a standard item as a one-time product (other kinds are not sold here). Connect once and
  keep the result: a second `ConnectAsync` while it is connected is refused, so `Dispose` it before connecting again (an item
  added in the Flock dashboard later is sold from the next connection). A connection made before the Flock client was created
  again no longer acts; connect again under the new client.
- **`BuyAsync`** needs a signed-in player. It sets that player's account token on the store first (`Shop.GetStoreAccountTokenAsync`),
  so a purchase cannot be redeemed by anyone else, then buys. When Flock refuses a purchase the player paid for, it throws
  `FlockException` (its `ErrorCode` and `Hint` say why) and the purchase stays unfinished.
- **A pending purchase** (a slow way to pay, or "Ask to Buy") ends the call as `Pending`; once paid it is delivered through
  `PurchaseDelivered`. If the call's cancellation token fires, the purchase carries on and is reported the same way.
- **Purchases left unfinished** (a crash, a lost connection, a refusal) come back from the store at the next launch. Once a
  player is signed in, each is sent to Flock once a launch: delivered ones raise `PurchaseDelivered`, refused ones
  `PurchaseNeedsAttention` and stay unfinished for the next launch. Store purchases of products no Flock item sells are left to
  the game. A product two items share is sold only through `BuyAsync`, which names the item.
- **In the editor** Unity IAP's fake store answers (define `IAP_FAKE_STORE_DEFAULT` to buy without its dialog). Name the store to
  test with, `ConnectAsync(PaymentProviders.GooglePlay)`; the account token is not set there, and Flock refuses the fake
  store's purchases, so try a real purchase in the store's sandbox on a device.
- **`StoreFailureReason`** says why the store did not take the payment: `Cancelled` is its own outcome, and a `Failed`
  purchase reads `PaymentDeclined`, `AlreadyOwned`, `AlreadyPending`, `ProductUnavailable`, `StoreUnavailable`,
  `NotSignedInToTheStore` or `Unknown`. It is a string, for the same reason as `reward.Type`.
- **`GetStorePrice`** reads the store's product: `Text` to show, `Amount` and `CurrencyCode`. Show it rather than the item's
  Flock `Price`. It throws for an item with no product id for this store, as `BuyAsync` does.
- **`RestoreAsync`** asks the store for the one-time purchases this store account owns (on a new phone, or after a reinstall)
  and has Flock deliver each to the signed-in player. A purchase Flock delivered before is answered with what it granted then,
  and grants nothing more; one Flock refuses (bought for another player) is listed in `Problems`, and the store's purchases are
  not finished again. Apple asks the player to sign in to the store for it, so start it from a button. It throws
  `FlockException` when the store cannot list the purchases, and on Apple when the player does not sign in to the store.
- **`CheckSetupAsync`** is for the developer: `ProductsTheStoreDidNotFind`, `ItemsWithoutAProductId`, and
  `FlockCanAskTheStore`, measured by asking the server to redeem a made-up purchase, which the store answers as unknown when
  the game's keys work (`StoreProblem` holds the refusal when they do not). Nothing is bought and nothing is recorded. In the
  editor the fake store finds every product, so only the keys are checked there.
- The events are raised on the main thread; `Dispose` stops listening.
- **Unity IAP 4.x:** copy `Docs~/recipes/FlockUnityIap4Store.cs` into the game. It holds each purchase until
  `RedeemStoreReceiptAsync` has delivered it, then confirms it.

See also: [Codegen](codegen.md) for typed shop accessors and the `FlockShopItemId` / `FlockStoreItemId` / `FlockFundId` enums, and [Player Data & Game Commands](player-data.md) for `AddGameFundsAsync`.
