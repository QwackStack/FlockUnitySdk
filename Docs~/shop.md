# Shop

[← Back to README](../README.md)

## Every situation, in short

| You want to | Call |
|---|---|
| List the shops and their items | `Shop.GetAllAsync()`, `Shop.GetByNameAsync(name)`, then `shop.ShopItems` |
| Show the player's balances | `Player.GetWalletAsync()`, then `wallet.BalanceOf("Gold")` |
| Grey out what they cannot pay for | `wallet.CanAfford(item)` (false for a real-money item) |
| Show what they own | `Shop.GetOwnedItemCountsAsync()`, or `Shop.OwnsAsync(item.Id)` |
| Show a real-money item's price | `store.GetPrice(item.Id).Text`, the store's price in the player's currency |
| Buy any item | `Shop.PurchaseAsync(item.Id)`: the wallet pays for a game currency, the store for real money |
| Tell "not enough currency" | catch `FlockException` with `ErrorCode == FlockErrorCode.ShopInsufficientFunds` |
| Tell "no longer for sale" (switched off in the dashboard) | catch `FlockException` with `ErrorCode == FlockErrorCode.ShopItemNotForSale`, then read the shop again |
| Tell "the store did not take the payment" | catch `FlockStoreException`, and read its `Reason` (`Cancelled`, `PaymentDeclined`, `AlreadyOwned`...) |
| Handle "no answer came" | catch `FlockException` with `OutcomeUnknown`, then read the wallet and inventory again |
| Handle "waiting for the payment" | `bought.IsPaid` is false; `store.PurchaseCompleted` reports it once paid |
| Use a consumable the player holds | `Shop.ConsumeOneAsync(item.Id)` |
| Give back one-time items on a new phone | `store.RestoreAsync()` |
| Check the store setup while you build | `store.CheckSetupAsync()` |
| Redeem a purchase from your own store code | `Shop.RedeemStoreReceiptAsync(item.Id, receipt)` |

Each is shown below; the `Samples/Shop` folder has all of them on one screen (`FlockShopSample`).

```csharp
var shops = await FlockClient.Instance.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await FlockClient.Instance.Shop.GetByIdAsync("shop-id");
var item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
var items = await FlockClient.Instance.Shop.GetItemsByShopAsync("shop-id");
// One call buys any item for the signed-in player: from the wallet when it costs a game currency, through the store
// (FlockStore, below) when it costs real money. It takes the shop item's Id (ShopItem.Id, from the shop's ShopItems): not the
// item's name, and not the shop's id. Catch FlockException and check e.ErrorCode for specific reasons (e.g.
// FlockErrorCode.ShopInsufficientFunds, ShopWalletNotFound); e.Message says what went wrong and, on a "Fix:" line, how to fix it.
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
long gold = purchase.Balances?.BalanceOf("Gold") ?? 0;   // Balances is null when no currency moved

// Use one of a shop item the player holds: Flock finds an unused entry and uses it up.
ConsumeResult consumed = await FlockClient.Instance.Shop.ConsumeOneAsync("shop-item-id");
foreach (ShopItemReward reward in consumed.Granted)
    Debug.Log($"+{reward.Amount} {reward.Code}");
// Or a known entry, by its inventory id.
ConsumeResult consumedEntry = await FlockClient.Instance.Shop.ConsumeAsync(row.Id);

// What an item will grant, before buying it.
ShopItem item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
foreach (ShopItemReward reward in item.Rewards)
    Debug.Log($"advertises +{reward.Amount} {reward.Code}");
```

`Granted` and `Rewards` are never null — an item that grants nothing yields an empty list. `reward.Type`
is a string compared against `ShopItemRewardTypes.Currency` rather than an enum: rewards are stored as
typed entries so new kinds can appear without a schema migration, and an unknown value must not break
deserialization.

A wallet purchase is paid at once, so its `Status` reads `paid` (`IsPaid` is true). A real-money item goes through the store
connected with `FlockStore.ConnectAsync`; with none connected the call refuses it before anything is sent and says to connect
one. `Status` is one of `PurchaseResultStatuses` (`pending`, `paid`, `failed`, `expired`, `refunded`), a string for the same
reason as `reward.Type`, and reads `paid` when the server leaves it out. A purchase or consume that reports a wallet also
writes it into the cached player data, so the next `GetMyDataByTemplateAsync` reads the new balance rather than the one from
before, and hands it over as `Balances`.

Every attempt of a wallet purchase carries the same key (`idempotency_key`), so after an answer that never came (a dropped
connection, a timeout, a server error) the purchase is sent again under the client's retry settings, and the server answers the
repeat with the first purchase: the player pays once. A new call is a new purchase. When no attempt is answered the call throws
with **`OutcomeUnknown`** set: the purchase may have been made, so read the wallet and inventory before offering it again. The
server must read the key (Flock's servers from 2026-10-26 on); an older one ignores it. A consume carries no key, so an ambiguous
failure throws rather than being re-sent (only provably unprocessed failures, 408/429, retry), with `OutcomeUnknown` set the same
way. Neither call is queued while the network is down: each throws `FlockNetworkException` (a purchase of an item not read yet
fails on reading it first) and is not sent again when the network comes back.

```csharp
try
{
    await FlockClient.Instance.Shop.PurchaseAsync(item.Id);
}
catch (FlockException e) when (e.OutcomeUnknown)
{
    // No answer said whether it went through: read the wallet and inventory before offering it again.
}
```

`OutcomeUnknown` is set on a purchase, a consume, a fund grant and a store redemption that failed with no answer, a server error
naming no cause, or an answer that could not be read. It is false for a refusal (such as not enough currency) and for every
read. A call cancelled through its own token after it was sent may also have been made. When it is set, the SDK drops its cached copy of the player's data,
so the next wallet read asks the server rather than answering with the balance from before the call.

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
    // Paid through a store, never from the wallet: PurchaseAsync buys it through the connected FlockStore.
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

**A standard item is one-time only in a store.** The store sells it once to a store account; the wallet sells it again on every
purchase, so before offering a wallet one, check the player does not hold it already (`GetOwnedItemCountsAsync`).

## A shop screen

Three reads give a shop screen what it shows next to each item: the player's balances, what they own, and whether they can pay.

```csharp
FlockWallet wallet = await FlockClient.Instance.Player.GetWalletAsync();
IReadOnlyDictionary<string, int> owned = await FlockClient.Instance.Shop.GetOwnedItemCountsAsync();

foreach (ShopItem item in shop.ShopItems)
{
    owned.TryGetValue(item.Id, out int count);
    bool alreadyHeld = item.Type == ShopItemTypes.Standard && count > 0;  // the wallet would sell it again, so the game checks
    bool payable = item.IsRealMoney || wallet.CanAfford(item);              // grey out a wallet item the player cannot pay for
}

try
{
    PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);
    // bought.IsPaid is false only for a store purchase still waiting for its payment.
}
catch (FlockException e) when (e.ErrorCode == FlockErrorCode.ShopInsufficientFunds)
{
    // Not enough of the item's currency: nothing was bought.
}
catch (FlockStoreException e)   // with Unity IAP 5 installed
{
    // The store did not take the payment (e.Reason: cancelled, payment declined, already owned...): nothing was charged.
}
catch (FlockException e) when (e.OutcomeUnknown)
{
    // No answer came: read the wallet and inventory again to see whether it went through.
}
```

- **`GetWalletAsync`** reads the player data of the template tagged `currency`: `GameCurrencies` and `RealCurrencies` by code,
  `BalanceOf(code)` (0 when not held) and `Holds(code)`. It is read through the player-data cache, which purchases, store
  redemptions and fund grants keep current. A player with no wallet row (registered before the currency template existed, or
  under another Game Version) throws `FlockValidationException` saying so. A purchase's or consume's `Balances` is the same
  reading of the wallet it returned.
- **`CanAfford(item)`** is true for an item paid from the wallet whose currency the wallet holds at least `Price` of. It reads
  false for a real-money item, which is bought through a store. Codes match exactly, as the server matches them.
- **`GetOwnedItemCountsAsync`** counts the unused inventory entries of each item, from every page; used and refunded entries
  do not count. `OwnsAsync(shopItemId)` is the same read for one item. The inventory is never cached, so read it once for a
  screen, and again after a purchase.
- **`ConsumeOneAsync(shopItemId)`** uses the first unused entry of that item. One used meanwhile on another device is passed over
  for the next; any other failure stops, so one call never uses two. It is refused, before anything is used, when the player holds
  none.

The `Samples/Shop` folder has a one-screen shop built on these calls (`FlockShopSample`), with store items when Unity IAP 5 is
installed.

## Store purchases

When the App Store or Google Play has charged the player, hand Flock the store's purchase and it delivers the item: the
server asks the store itself, checks the product is the item's `StoreProductIds` entry for that store, and grants the item
to the signed-in player. With Unity IAP 5, `FlockStore` (below) makes this call for you; this is for a game with store code of
its own.

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
- **Revenue is recorded by the server**, once per store purchase, at the item's `Price` and `Currency`; the SDK records no
  analytics transaction for a store purchase (a wallet purchase's, which it does record, is never counted as revenue).

## Buying with Unity IAP 5

With Unity IAP 5 in the project (`com.unity.purchasing` 5.x), connect `FlockStore` once at start; from then on
`Shop.PurchaseAsync` buys real-money items through it: the store charges the player, Flock checks it with the store and delivers
the item, and only then is the store's purchase finished. It is compiled only when Unity IAP 5 is installed, so a game without
it carries none of it.

```csharp
// Once, after FlockClient.Create. On a phone the store is found by itself.
FlockStore store = await FlockStore.ConnectAsync();
// Every real-money purchase Flock delivers, the ones PurchaseAsync answers included: refresh the wallet and inventory here.
store.PurchaseCompleted += (item, result) => RefreshTheShopScreen();
store.PurchaseNotDelivered += purchase => Debug.LogWarning($"{purchase.ProductId}: {purchase.Error.Message}");  // a message ends with its fix

PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);   // the same call as for a wallet item

// The price the store shows this player, in their currency with their taxes; null when the store did not find the product.
FlockStorePrice price = store.GetPrice(item.Id);
FlockStoreRestore restore = await store.RestoreAsync();          // one-time items this store account already owns
FlockStoreSetupCheck check = await store.CheckSetupAsync();      // what is missing from the setup, without buying anything
```

- **`ConnectAsync`** reads the game's shops and registers each item that has a product id for this store: a currency pack or
  consumable as a consumable product, a standard item as a one-time product (other kinds are not sold here). Connect once and
  keep the result: a second `ConnectAsync` while it is connected is refused, so `Dispose` it before connecting again (an item
  added in the Flock dashboard later is sold from the next connection). A connection made before the Flock client was created
  again no longer acts; connect again under the new client. Once disposed, `PurchaseAsync` refuses real-money items again.
- **A real-money `PurchaseAsync`** needs a signed-in player. It sets that player's account token on the store first
  (`Shop.GetStoreAccountTokenAsync`), so a purchase cannot be redeemed by anyone else, then buys. A store that does not take the
  payment throws **`FlockStoreException`**: nothing was charged, and its `Reason` says why, one of `FlockStoreFailureReasons`
  (`Cancelled`, `PaymentDeclined`, `AlreadyOwned`, `AlreadyPending`, `ProductUnavailable`, `StoreUnavailable`,
  `NotSignedInToTheStore`, `Unknown`; a string, for the same reason as `reward.Type`), with the store's own words in its message.
  When Flock refuses a purchase the player paid for, it throws `FlockException` (its `ErrorCode` and `Hint` say why), the purchase
  stays unfinished, and `PurchaseNotDelivered` reports it.
- **An item switched off in the dashboard** (Inactive, the item or its shop) is not shown to players and not sold. Players'
  shop reads leave it out; a wallet purchase of it is refused with `ShopItemNotForSale`, and so is a real-money one, checked with
  the server just before the store's dialog, so a game still showing an older copy of the shop never takes the payment. Either
  refusal drops the SDK's cached shops, so reading them again shows it gone. A purchase the player already paid for is still
  delivered: the store connection keeps the products it sold (with the offline cache on, per game), so an unfinished purchase of
  an item the shops no longer list comes back and is sent to Flock at the next launch, and restoring still delivers it. One whose
  item was deleted is reported through `PurchaseNotDelivered`.
- **A pending purchase** (a slow way to pay, or "Ask to Buy") answers with `Status` `pending` (`IsPaid` false); once paid it is
  reported by `PurchaseCompleted`. If the call's cancellation token fires, the purchase carries on and is reported the same way.
- **Purchases left unfinished** (a crash, a lost connection, a refusal) come back from the store at the next launch. Once a
  player is signed in, each is sent to Flock once a launch: delivered ones raise `PurchaseCompleted`, refused ones
  `PurchaseNotDelivered` and stay unfinished for the next launch. Store purchases of products no Flock item sells are left to
  the game. A product two items share is sold only through `PurchaseAsync`, which names the item.
- **In the editor** Unity IAP's fake store answers (define `IAP_FAKE_STORE_DEFAULT` to buy without its dialog). Name the store to
  test with, `ConnectAsync(PaymentProviders.GooglePlay)`; the account token is not set there, and Flock refuses the fake
  store's purchases, so try a real purchase in the store's sandbox on a device.
- **`GetPrice`** reads the store's product: `Text` to show, `Amount` and `CurrencyCode`. Show it rather than the item's
  Flock `Price`. It throws for an item with no product id for this store.
- **`RestoreAsync`** asks the store for the one-time purchases this store account owns (on a new phone, or after a reinstall)
  and has Flock deliver each to the signed-in player; `Restored` lists their items. A purchase Flock delivered before grants
  nothing more; one Flock refuses (bought for another player) is listed in `NotDelivered`, and the store's purchases are not
  finished again. Apple asks the player to sign in to the store for it, so start it from a button. It throws `FlockException`
  when the store cannot list the purchases, and on Apple when the player does not sign in to the store.
- **`CheckSetupAsync`** is for the developer: `NotFoundInTheStore`, `MissingProductId`, and `ServerCanCheckPurchases`, measured
  by asking the server to redeem a made-up purchase, which the store answers as unknown when the game's keys work (`ServerProblem`
  holds the refusal when they do not); `IsReady` is true when nothing needs fixing. Nothing is bought and nothing is recorded. In
  the editor the fake store finds every product, so only the keys are checked there.
- The events are raised on the main thread; `Dispose` stops listening.
- **Unity IAP 4.x:** copy `Docs~/recipes/FlockUnityIap4Store.cs` into the game. It holds each purchase until
  `RedeemStoreReceiptAsync` has delivered it, then confirms it.

See also: [Codegen](codegen.md) for typed shop accessors and the `FlockShopItemId` / `FlockFundId` enums, and [Player Data & Game Commands](player-data.md) for `AddGameFundsAsync`.
