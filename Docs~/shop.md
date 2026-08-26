# Shop

[← Back to README](../README.md)

```csharp
var shops = await FlockClient.Instance.Shop.GetAllAsync(page: 1, limit: 10);
var shop = await FlockClient.Instance.Shop.GetByIdAsync("shop-id");
var item = await FlockClient.Instance.Shop.GetItemAsync("shop-item-id");
var items = await FlockClient.Instance.Shop.GetItemsByShopAsync("shop-id");
// Same retry contract as AddGameFunds (money mutation) — ambiguous failures throw; catch them.
// On throw, a Failed analytics event is recorded automatically. Catch FlockException and check
// e.Code for specific reasons (e.g. FlockErrorCode.ShopInsufficientFunds, ShopWalletNotFound).
// playerId is optional — omit it to use the signed-in player (CurrentPlayerId).
var inventory = await FlockClient.Instance.Shop.PurchaseAsync("shop-item-id");
var playerItems = await FlockClient.Instance.Shop.GetPlayerInventoryAsync();
```

> **`GetPlayerInventoryAsync` does not currently work — this is a backend fault, not an SDK one.**
> `GET /v1/player_inventory/player/{id}` returns **HTTP 500 unconditionally** (verified against a live
> backend 2026-08-22, with and without query parameters — the server raises before reading anything the
> SDK sends). The call is wired correctly and will start working the moment the route is fixed; until
> then it throws `FlockNetworkException` with `StatusCode == 500`. Read a purchase's returned
> `PlayerInventory` instead, or track owned items in player data. Inventory is never offline-cached, so
> there is no stale copy to fall back on either.

See also: [Codegen](codegen.md) for typed shop accessors and the `FlockShopItemId` / `FlockFundId` enums, and [Player Data & Game Commands](player-data.md) for `AddGameFundsAsync`.
