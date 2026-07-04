# Player Data & Game Commands

[← Back to README](../README.md)

```csharp
// Player data (read-only, create/update via game commands)
var data = await FlockClient.Instance.Player.GetDataByIdAsync("player-data-id");
var all = await FlockClient.Instance.Player.GetAllDataAsync(page: 1, limit: 10);
var byPlayer = await FlockClient.Instance.Player.GetAllDataAsync(playerId: "player-id");

// Reading player data — preferred path: the codegen'd accessors. The Codegen tab's Sync Schemas
// emits one Get<TemplateName>Async() extension per template returning a generated typed
// class; numeric conversion is handled by the deserializer.
var currency = await FlockClient.Instance.Player.GetCurrencyAsync(); // generated

// Raw fields (no codegen, ad-hoc fields, or schema drift): JSON integers arrive boxed
// as long and decimals as double, so read through GetValue<T>() instead of casting
// Value directly — (int)field.Value throws on a boxed long.
int score = playerData.Data.Find(f => f.FieldName == "score").GetValue<int>();

// Game commands — server-side operations. Each runs its own typed command
// and returns the updated PlayerData.
PlayerData updated = await FlockClient.Instance.Commands.UpdatePlayerDataAsync(
    "player-data-id",
    new List<DataField> { /* flattened typed fields */ });

PlayerData updated = await FlockClient.Instance.Commands.UpdatePlayerDataFieldAsync(
    "player-data-id", "score", 9999);

// Money mutations (AddGameFunds + shop Purchase) only retry failures the server provably didn't process (408/429); ambiguous failures (timeout/5xx) throw rather than risk a double-credit, so wrap them in try/catch.
PlayerData updated = await FlockClient.Instance.Commands.AddGameFundsAsync("gold", 500);
// No player-data id — the SDK resolves your currency wallet (the player template tagged "currency").
// With codegen you can pass a typed FlockFundId of your currency ids instead of the raw string:
// PlayerData updated = await FlockClient.Instance.Commands.AddGameFundsAsync(FlockFundId._100, 500);

// The achievements row is resolved for you too — no player-data id. Prefer the typed
// FlockAchievementId overload (generated); a raw-string overload also exists.
PlayerData updated = await FlockClient.Instance.Commands.UnlockAchievementAsync(FlockAchievementId.FirstWin);

// Player ban — returns active ban data keyed by feature, or null if not banned
var ban = await FlockClient.Instance.Player.GetBanAsync(FlockClient.Instance.CurrentPlayerId);
```

See also: [Codegen](codegen.md) for typed template accessors and `UpdateAsync` on generated classes, and [Shop](shop.md) for the same money-safety retry contract on purchases.
