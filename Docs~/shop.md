# Shop

[← Back to README](../README.md)

## Every situation, in short

| You want to | Call |
|---|---|
| List the shops and their items | `Shop.GetAllAsync()`, `Shop.GetByNameAsync(name)`, then `shop.ShopItems` |
| Show the player's balances | `Player.GetWalletAsync()`, then `wallet.BalanceOf("Gold")` |
| Grey out what they cannot pay for | `wallet.CanAfford(item)` (false for a real-money item) |
| Show what they own | `Shop.GetOwnedItemCountsAsync()`, or `Shop.OwnsAsync(item.Id)` |
| Show a real-money item's price | `store.GetPrice(item.Id)?.Text`: on a phone the store's price in the player's currency (null when the store did not find the product), else the item's own |
| Buy any item | `Shop.PurchaseAsync(item.Id)`: the wallet pays for a game currency, `FlockStore` for real money |
| Tell "not enough currency" | catch `FlockException` with `ErrorCode == FlockErrorCode.ShopInsufficientFunds` |
| Tell "no longer for sale" (switched off in the dashboard) | catch `FlockException` with `ErrorCode == FlockErrorCode.ShopItemNotForSale`, then read the shop again |
| Tell "the payment was not taken" | catch `FlockStoreException`, and read its `Reason` (`Cancelled`, `PaymentDeclined`, `Expired`, `AlreadyOwned`...) |
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
// One call buys any item for the signed-in player: from the wallet when it costs a game currency, through FlockStore
// (below: the store on a phone, a web checkout elsewhere) when it costs real money. It takes the shop item's Id (ShopItem.Id, from the shop's ShopItems): not the
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

A wallet purchase is paid at once, so its `Status` reads `paid` (`IsPaid` is true). A real-money item goes through `FlockStore`,
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
naming no cause, or an answer that could not be read, and on one whose later attempt was refused after an earlier one got no
answer. It is false for a refusal of a call that got every answer (such as not enough currency) and for every read; a wallet
purchase refused for not enough currency, a currency not held or an item not for sale is a known outcome even after an attempt
with no answer, since the server checks the purchase's key before refusing. A call cancelled through its own token ends with
`OperationCanceledException` unless an earlier attempt got no answer: then it ends with that attempt's `FlockException`,
`OutcomeUnknown` set, so a cancel never reads as "nothing bought" when an attempt may have bought it. A cancelled call may still
have been made, so the SDK drops its cached player data either way. When it is set, the SDK drops its cached copy of the
player's data, so the next wallet read asks the server rather than answering with the balance from before the call.

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

ShopItem picked = shop.ShopItems[0];  // the item the player picked
try
{
    PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(picked.Id);
    // bought.IsPaid is false only for a store purchase still waiting for its payment.
}
catch (FlockException e) when (e.ErrorCode == FlockErrorCode.ShopInsufficientFunds)
{
    // Not enough of the item's currency: nothing was bought.
}
catch (FlockStoreException e)   // a real-money item, through the store or a web checkout
{
    // The payment was not taken (e.Reason: cancelled, payment declined, expired, already owned...): nothing was charged.
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

The `Samples/Shop` folder has a one-screen shop built on these calls (`FlockShopSample`), with real-money items through the
store on a phone and through a web checkout on other build targets.

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
  changes while it is being sent again, the next attempt is not sent and the call ends with `OperationCanceledException`; after an
  attempt that got no answer, it ends with that attempt's `FlockException` instead, `OutcomeUnknown` set.
- **It is never queued offline.** The store's unfinished purchase is the queue: send it again when the store hands it back.
- The ids are sent exactly as given; one with a space before or after it is refused rather than trimmed.
- `Status` reads `paid` for a delivered purchase. A receipt already redeemed and since refunded reads `refunded`: nothing
  more is delivered, and the store's purchase can be finished all the same. The wallet it reports is written into the
  cached player data, as a purchase's is.
- **Revenue is recorded by the server**, once per store purchase, at the item's `Price` and `Currency`; the SDK records no
  analytics transaction for a store purchase (a wallet purchase's, which it does record, is never counted as revenue).

## Buying with real money: FlockStore

Connect `FlockStore` once at start; from then on `Shop.PurchaseAsync` buys real-money items through it, with the same calls on
every platform. On Android and iOS it is the store, through Unity IAP 5 (`com.unity.purchasing` 5.x): the store charges the
player, Flock checks it with the store and delivers the item, and only then is the store's purchase finished. On every other
build target it is a web checkout through the game's gateway ([below](#through-a-web-checkout)). Without Unity IAP 5 a phone's
store is refused, with what to install; a phone's player is never sent to a web page instead, as the stores' rules forbid it for
digital goods. On Android, set **Minimum API Level** to 23 or higher (Player Settings > Other Settings): Unity IAP 5's Google
Play Billing library needs it, and a Unity 2021.3 project starts at 22, so its build stops at Gradle's manifest merge.

```csharp
// Once, after FlockClient.Create: the store on a phone, a web checkout on other build targets.
FlockStore store = await FlockStore.ConnectAsync();
// Every real-money purchase Flock delivers, the ones PurchaseAsync answers included: refresh the wallet and inventory here.
store.PurchaseCompleted += (item, result) => RefreshTheShopScreen();
store.PurchaseNotDelivered += purchase => Debug.LogWarning($"{purchase.ProductId}: {purchase.Error.Message}");  // a message ends with its fix

PurchaseResult bought = await FlockClient.Instance.Shop.PurchaseAsync(item.Id);   // the same call as for a wallet item

// The price to show: on a phone the store's, in the player's currency with their taxes (null when the store did not find
// the product); through a web checkout the item's own.
FlockStorePrice price = store.GetPrice(item.Id);
FlockStoreRestore restore = await store.RestoreAsync();          // on a phone, one-time items this store account already owns
FlockStoreSetupCheck check = await store.CheckSetupAsync();      // what is missing from the setup, without buying anything
```

- **`ConnectAsync`** pays this build target's own way unless `FlockStoreOptions.PaymentProvider` names another: a Mac App Store
  build names `PaymentProviders.AppleAppStore`, and a game with several active gateways names `Stripe`, `Tap` or `Moyasar`.
  `IsWebCheckout` and `PaymentProvider` say which it connected. It reads the game's shops; a store also registers each item that
  has a product id for it: a currency pack or consumable as a consumable product, a standard item as a one-time product (other
  kinds are not sold here). Connect once and
  keep the result: a second `ConnectAsync` while it is connected is refused, so `Dispose` it before connecting again (an item
  added in the Flock dashboard later is sold from the next connection). A connection made before the Flock client was created
  again no longer acts; connect again under the new client. Once disposed, `PurchaseAsync` refuses real-money items again.
- **A real-money `PurchaseAsync`** needs a signed-in player. It sets that player's account token on the store first
  (`Shop.GetStoreAccountTokenAsync`), so a purchase cannot be redeemed by anyone else, then buys. A store that does not take the
  payment throws **`FlockStoreException`**: nothing was charged, and its `Reason` says why, one of `FlockStoreFailureReasons`
  (`Cancelled`, `PaymentDeclined`, `AlreadyOwned`, `AlreadyPending`, `ProductUnavailable`, `StoreUnavailable`,
  `NotSignedInToTheStore`, `Expired`, `Unknown`; a string, for the same reason as `reward.Type`), with the store's own words in its message.
  Google Play reports a payment method it could not charge the same way as purchases being unavailable, so a declined card
  there reads `StoreUnavailable`; `PaymentDeclined` comes from the App Store and a web checkout, `Expired` from a web checkout
  left unpaid.
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
  reported by `PurchaseCompleted`. If the call's cancellation token fires, the purchase carries on and is reported the same way. The
  call ends, cancelled, when the player signs out or changes while the store's dialog is open; the store's purchase, once paid, is
  delivered to the player who bought it at a later sign-in of theirs, or through `RestoreAsync` (sent while another player is signed
  in, it is refused for that player and not sent again in that launch). A player who signs out or changes before the dialog opens
  buys nothing.
- **Purchases left unfinished** (a crash, a lost connection, a refusal) come back from the store at the next launch. Once a
  player is signed in, each is sent to Flock once a launch: delivered ones raise `PurchaseCompleted`, refused ones
  `PurchaseNotDelivered` and stay unfinished for the next launch. Store purchases of products no Flock item sells are left to
  the game. A product two items share is sold only through `PurchaseAsync`, which names the item.
- **In the editor** the build target picks, and Unity IAP's fake store answers for a store (define `IAP_FAKE_STORE_DEFAULT` to
  buy without its dialog): switch the build target to Android or iOS, or name the store,
  `ConnectAsync(new FlockStoreOptions { PaymentProvider = PaymentProviders.GooglePlay })`. The account token is not set there,
  and Flock refuses the fake store's purchases, so try a real purchase in the store's sandbox on a device.
- **`GetPrice`** gives `Text` to show, `Amount` and `CurrencyCode`: on a phone the store's product, else the item's own price
  (its `Price` is in the currency's smallest unit, so 799 SAR reads `SAR 7.99`, in the device's number format). Show it rather
  than the item's `Price`. On a phone it is null when the store did not find the item's product, and it throws for an item with
  no product id for this store; it throws for a game-currency item everywhere.
- **`RestoreAsync`** and **`CheckSetupAsync`** need a signed-in player and throw `FlockAuthException` before one is.
- **`RestoreAsync`** asks the store for the one-time purchases this store account owns (on a new phone, or after a reinstall)
  and has Flock deliver each to the signed-in player; `Restored` lists their items. A purchase Flock delivered before grants
  nothing more; one Flock refuses (bought for another player) is listed in `NotDelivered`, and the store's purchases are not
  finished again. A purchase still unfinished, one Flock could not be reached for earlier in this launch included, is sent
  again and arrives through `PurchaseCompleted` (or `PurchaseNotDelivered`), not in `Restored`. Apple asks the player to sign
  in to the store for it, so start it from a button. It throws `FlockException`
  when the store cannot list the purchases, and on Apple when the player does not sign in to the store. Through a web checkout
  Flock already holds what the player bought: it reads the player's checkouts still open again, and both lists are empty.
- **`CheckSetupAsync`** is for the developer: `NotFoundInTheStore`, `MissingProductId`, and `ServerCanCheckPurchases`, measured
  by asking the server to redeem a made-up purchase, which the store answers as unknown when the game's keys work (`ServerProblem`
  holds the refusal when they do not); `IsReady` is true when nothing needs fixing. Nothing is bought and nothing is recorded. In
  the editor the fake store finds every product, so only the keys are checked there. Through a web checkout only a checkout shows
  whether a gateway is connected, so `NotChecked` says so and `IsReady` stays false: buy an item with the gateway's test card.
- The events are raised on the main thread; `Dispose` stops listening.
- **Unity IAP 4.x:** copy `Docs~/recipes/FlockUnityIap4Store.cs` into the game. It holds each purchase until
  `RedeemStoreReceiptAsync` has delivered it, then confirms it.

### Through a web checkout

On a build target with no phone store (Windows, macOS, Linux, WebGL), `FlockStore` pays through the game's gateway: Stripe, Tap
or Moyasar, connected under Monetization > Payments in the Flock dashboard. The server uses the game's one active gateway; with
several active, name one in `FlockStoreOptions.PaymentProvider`.

- **A real-money `PurchaseAsync`** opens a checkout, opens its payment page in the browser, and reads the purchase every 3 seconds
  until it ends. Paid: it returns with `Granted`, and the SDK's cached player data is dropped, so read the wallet and inventory
  again. Declined, or given up: `FlockStoreException` with `PaymentDeclined`. Left unpaid for the hour a checkout stays open:
  `Expired`. A checkout is never opened again after an answer that never came.
- **`FlockStoreOptions`**: `ReturnUrl` and `CancelUrl` are where the browser lands after paying and after giving up (your own
  pages); `OpenPaymentPage` opens the page yourself; `CheckEvery` sets how often the purchase is read.
- **WebGL:** a browser opens a new tab only for a short time after the player's click. The page opens once the checkout answers,
  about 1.5 s after the click in our tests, which a Chromium browser (Chrome, Edge) allowed; Safari allows about a second and may
  block it. For Safari, set `OpenPaymentPage` to keep the address and show a **Pay now** button whose click calls
  `Application.OpenURL` with it.
- **The gateway's setup** (Monetization > Payments): Stripe needs a webhook at
  `https://api-flock.qwacks.com/v1/shop/webhook/<game id>/stripe` (events `checkout.session.completed`,
  `checkout.session.expired`, `payment_intent.payment_failed`, `charge.refunded`, `charge.dispute.closed`), or a payment waits
  for the server's own check (within about 15 minutes); Moyasar wants one at the same address ending `/moyasar` (its secret token is the Webhook secret);
  Tap is given its address with each checkout. A refund or dispute made in Stripe's or Moyasar's own dashboard does not reach Flock
  yet: the purchase stays paid. A Moyasar player whose card is declined can try another on the same page; the purchase stays open
  until paid or expired. Stripe refuses a price below its smallest charge in the account's own currency
  (`PaymentProviderCheckoutFailed`, "amount_too_small"); Moyasar charges in SAR only (a USD price converted, others refused).
- **A wait the game cancels** leaves the checkout read, and `PurchaseCompleted` reports it once paid. A checkout still open when
  the game closed is found again at the player's next sign-in and reported the same way. One paid while the game was closed is
  delivered by the server all the same, and shows in the wallet and inventory; it raises no `PurchaseCompleted`.
- **Signing out** ends the waits; the checkouts are found again at that player's next sign-in. `PurchaseNotDelivered` is never
  raised: the server delivers a paid checkout itself.

See also: [Codegen](codegen.md) for typed shop accessors and the `FlockShopItemId` / `FlockFundId` enums, and [Player Data & Game Commands](player-data.md) for `AddGameFundsAsync`.
