# Codegen

[← Back to README](../README.md)

Run **Sync Schemas** from the Codegen tab in **Flock > Settings** to fetch your game's player templates and game configs from the backend and generate typed C# accessors. Output goes to `Assets/Flock/Generated/` by default; change the path on the FlockConfig asset if you want it elsewhere. Treat the folder as Flock-owned — sync wipes the `Templates/`, `Commands/`, `Configs/`, and `Catalog/` subdirectories on each run, and **Delete Generated Code** clears the whole tree.

**For designers:** each sync also writes `Generated/Catalog/FlockContentCatalog.asset` — a read-only ScriptableObject you can select in the Project view to browse every shop (items, prices, currency), game config (fields and current values), and player template (fields) in the Inspector, no code or dashboard login needed. It's regenerated on every sync, so it always mirrors the backend. (Editor-only; it's never referenced at runtime and is stripped from player builds. CI/headless syncs skip it.)

What gets generated, given a player template named `PlayerProgress` and a game config named `Gameplay`:

```csharp
// Templates — Flock.Generated.Templates.PlayerProgressTemplate
// No ID needed: resolves the current player's PlayerData via Client.CurrentPlayerId.
// First call paginates and caches all of the player's PlayerData; subsequent
// Get*Async calls (any template) hit the in-memory cache. Use Player.ClearCache()
// after a known external mutation, or when switching player.
PlayerProgressTemplate progress = await FlockClient.Instance.Player.GetPlayerProgressAsync();
int level = progress.Level;
int xp = progress.Xp;

// Each generated class carries its source identity and its typed schema:
string id = PlayerProgressTemplate.SourceId;
string name = PlayerProgressTemplate.SourceName;
IReadOnlyList<TypedSchema> schema = PlayerProgressTemplate.Schema;

// Commands — UpdateAsync is an instance method on the template. Mutate the populated
// POCO and call .UpdateAsync() on it directly — no extra using needed.
progress.Level = 5;
progress.Xp = 1200;
PlayerData updated = await progress.UpdateAsync();

// Configs — Flock.Generated.Configs.GameplayConfig
GameplayConfig gameplay = await FlockClient.Instance.Config.GetGameplayAsync();
float baseMoveSpeed = gameplay.BaseMoveSpeed;
// SourceId / SourceName / Schema / SourceTag are exposed on the generated class:
SchemaTag tag = GameplayConfig.SourceTag;

// Shops — Flock.Generated.Shops. Typed shop accessor + enum-keyed Purchase / AddGameFunds.
Shop starter = await FlockClient.Instance.Shop.GetStarterPackShopAsync();
// Purchase / AddGameFunds take generated enums of the available ids; the UUID is resolved inside:
PlayerInventory bought = await FlockClient.Instance.Shop.PurchaseAsync(FlockShopItemId.GemPack);
PlayerData funded = await FlockClient.Instance.Commands.AddGameFundsAsync(FlockFundId._100, 500);

// Achievements — Flock.Generated.Achievements. Enum-keyed unlock; the raw name is resolved inside.
PlayerData unlocked = await FlockClient.Instance.Commands.UnlockAchievementAsync(FlockAchievementId.FirstWin);
// Achievement details (from your 'achievement'-tagged game config) — looked up by the same enum:
var firstWin = await FlockClient.Instance.Config.GetAchievementDetailsAsync(FlockAchievementId.FirstWin);
```

`UpdateAsync` is an instance method on each generated template type, so it's always available on the object — no extra `using`, and it shows in IntelliSense wherever you hold the instance. It validates `PlayerDataId` (set automatically by the matching `Get*Async`), turns the populated POCO back into a flattened DataField list via `{Template}.Schema.ToDataFieldList(this)`, and routes through `FlockCommandProvider.UpdatePlayerDataAsync`. After a write you typically want fresh reads — call `client.Player.ClearCache()` before the next `Get*Async` if you need to bypass the per-player snapshot cache.

Generated config classes have read-only properties (`{ get; private set; }`) — configs are game-wide and shouldn't be mutated client-side; mutations are admin-only on the backend.

Generated shops live in `Flock.Generated.Shops`: a `Get<Shop>ShopAsync()` accessor per shop (returns the live `Shop`), plus `FlockShopItemId` / `FlockFundId` enums of the available ids and matching `PurchaseAsync(FlockShopItemId)` / `AddGameFundsAsync(FlockFundId)` extension methods. The enum-typed methods are **generated extensions**, so they only appear after a sync. `FlockFundId` lists every shop currency. Members are the currency **id** (e.g. `_100` — a leading `_` is added only when an id starts with a digit, since C# identifiers can't) because currency *names* live only on the dashboard's admin `/currency` endpoint, which the SDK's API key can't reach. `AddGameFundsAsync` sends the currency id and resolves `player_data_id` from the player's **currency wallet** — the row for the player template tagged `currency`. Codegen bakes that template's id and calls `AddGameFundsAsync(currency, amount, currencyTemplateId)`, which resolves the wallet directly. There's also a public `AddGameFundsAsync(currency, amount)` overload that resolves the `currency`-tagged template at runtime instead (same tag mechanism as `UnlockAchievement`) — both are usable. Shop **data** (prices, status) is fetched live — only identity (ids, currencies) is generated, so prices never go stale in code.

Generated achievements live in `Flock.Generated.Achievements`: a `FlockAchievementId` enum whose members are the fields of the player template tagged `achievement` (PascalCased; each carries a `/// <summary>raw_name</summary>` doc), plus a `UnlockAchievementAsync(FlockAchievementId)` extension on `FlockCommandProvider` that maps the enum back to the raw `achievement_name` and resolves the achievements row for you. The enum overload is **additive** — the raw-string `UnlockAchievementAsync(string)` stays public, exactly like the shop `FlockFundId` methods — but prefer the typed one: a name that doesn't match the server is then caught at compile time instead of at runtime.

If you keep achievement details (display name, description, reward, etc.) in a **game config tagged `achievement`** — one config holding a list of entries, each with a `name` field — codegen wires it to the same enum: the entry's `name` is generated as `FlockAchievementId` (via a generated `JsonConverter`) instead of a raw string, and a `GetAchievementDetailsAsync(FlockAchievementId)` extension on `FlockConfigProvider` fetches that config and returns the matching entry. So you award and look up details with one enum. (The canonical `/achievement` resource is dashboard/OAuth2-only and isn't reachable by codegen's API key, which is why details live in a game config.) An entry whose `name` isn't a generated member throws on deserialize — re-sync after adding achievements.

The generated player/shop/achievement command extensions are guarded by `#if !FLOCK_NO_PLAYER`; if you strip the player provider from an SDK build, add `FLOCK_NO_PLAYER` to your project's scripting defines too so the generated code and the runtime agree.

Method names come from the template name on the backend (PascalCase). Field names follow each `TypedSchema.field_name`, also PascalCased.

Re-run sync whenever:
- You change the schema of a template in the dashboard
- You change **Game Version** on the FlockConfig asset (the editor warns, and the build guard fails, if the generated `SchemasManifest.GameVersionId` doesn't match the baked version ID)
- You add or remove templates

Type mapping and headless CI (`FlockCodegenCli` Sync/Verify, drift detection) are documented in [ARCHITECTURE.md](../ARCHITECTURE.md).
