# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.20.0]

### Added
- **`PurchaseStatus.Failed`.** `PurchaseAsync` now fires a `Failed` analytics transaction event before re-throwing on any purchase error — the `Started → Purchased / Failed` triangle is complete. Catch `FlockException` and inspect `ErrorCode` for the reason (e.g. `FlockErrorCode.ShopInsufficientFunds`, `ShopWalletNotFound`).
- **`GetMyInventoryAsync()` generated extension.** Codegen always emits a zero-arg `GetMyInventoryAsync(page, limit)` extension on `FlockShopProvider` that uses the signed-in player's id — no need to pass `CurrentPlayerId` manually.
- **`FlockShopItemId` XML doc comments.** Each generated enum member now carries `/// <summary>price currency — shop</summary>` (e.g. `100 Coins — Starter Pack`), visible on hover in the IDE.

### Changed
- `GetPlayerInventoryAsync` `playerId` is now optional (`= null`), defaulting to `CurrentPlayerId` — consistent with `PurchaseAsync`.
- Analytics `Amount` guard relaxed from `> 0` to `>= 0` so free (0-price) items no longer silently break `Started` / `Purchased` transaction recording.
- Removed stale "Tracking transactions is Not Supported" debug log from `RecordTransactionAsync`.
- Codegen (`ShopEmitter`): internal variable names corrected from `currencyIds`/`currencyId` to `currencyNames`/`currencyName`; `FlockFundId` members map currency names (not ids — the id endpoint is OAuth2/admin-only).

### Documentation
- README shop section: `GetPlayerInventoryAsync()` no longer passes `CurrentPlayerId` explicitly; added note that a `Failed` analytics event fires automatically on throw.

### Known issues / Backend backlog
- **IAP receipt validation absent.** No `/v1/shop/validate-receipt` endpoint — real-money Apple/Google purchases cannot be server-verified. SDK tracks `payment_provider` + `external_transaction_id` fields on analytics for when the endpoint lands.
- **`currency_id` analytics FK.** `POST /v1/analytics/transactions` rejects a null `currency_id` at the DB level despite the schema marking it optional; the SDK only has the currency name. Fix is backend-side (resolve name → id). Transaction analytics are swallowed via try/catch so purchases succeed.
- **Item stacks/quantities backend-blocked.** `PlayerInventorySchema` has no `quantity` field; one purchase = one record.
- **`MarkItemUsedAsync` not implementable.** `used_at` exists on the model but there is no PUT/PATCH inventory endpoint.
- Bundles, subscriptions, localized pricing, fraud/chargeback hooks, drop tables, and player-to-player trading are absent from the backend spec.

## [1.19.0]

### Added
- **Coded error contract.** Server 4xx/5xx responses now carry a machine-readable `{ "detail": { "code", "message" } }` envelope, surfaced on `FlockException.Code` (raw string, e.g. `player.email_already_registered`) and `FlockException.ErrorCode` (typed `FlockErrorCode`). The code is parsed once in the HTTP layer and stamped on every thrown `FlockException` (auth / validation / network). The new `FlockErrorCode` enum covers the current `/v1` codes with an `Unknown` fallback for an absent code or one this SDK version predates — `switch` on `ErrorCode` for handled cases, read `Code` for logging / forward-compat. Adds `FlockErrorCodes.Parse` and the `CodedErrorResponse` / `CodedErrorDetail` models.
- `client.Shop.GetByNameAsync(name)` — fetch a shop by name via `GET /v1/shop/by-name/{name}`; snapshot-cached and name-keyed like the other shop reads.
- EditMode tests: `FlockErrorPipelineTests` drives every `FlockException` type through a fake `IFlockHttpAdapter` and asserts the parsed `ErrorCode` (plus `FlockErrorCodes.Parse` unit checks); `FlockConfigResolutionTests` asserts patch-wins and no-patch → config-fallback through a URL-routing fake adapter.
- `ARCHITECTURE.md` — a contributor-facing code map plus "Backend backlog / known constraints", moved out of the README.

### Changed
- **`RegisterWith*` duplicate-skip is now code-based.** `IsAlreadyRegisteredError` matches the backend's coded `player.*_already_registered` errors via `FlockErrorCode` instead of substring-matching the message / body. (Tradeoff: drops tolerance for older backends that returned uncoded plain-text errors — intended for this release.)
- **Config values now resolve "patch, else config".** The generated `client.Config.Get<Name>Async()` accessors return the current game version's patch data, falling back to the config's own data when no patch exists (previously returned `default` / null). `ConfigAccessorEmitter` now emits a one-line `=> GetByConfigIdAsync<T>(SourceId, ct)` — re-sync to regenerate.
- **Breaking: config / schema / template reads are codegen-only.** The raw getters are now `internal`: `client.Config.GetAllAsync` / `GetByIdAsync` / `GetBySchemaAsync` / `GetGameConfigs*` / `GetPlayerFeaturesAsync`, all of `client.Schema.*`, and `client.Player.GetTemplates*` / `GetTemplateByIdAsync` / `GetTemplateByNameAsync` / `GetTemplateByTagAsync` / `GetTemplatePlayerDataAsync` are no longer public. Use the generated `Get<Config>Async()` / `Get<Template>Async()` accessors instead. Player **data** reads (`GetDataByIdAsync`, `GetAllDataAsync`, `GetBanAsync`) stay public.
- Internal: the `FlockEvents` internal raisers were renamed `Raise*` → `Invoke*` (no public surface change).

### Removed
- **Breaking**: the `ISchemaProvider` interface (the public `SchemaTag` enum stays). `IConfigProvider` is trimmed to `GetByConfigIdAsync<T>` and `IPlayerService` to the data / ban getters, matching the now-internal raw getters above.

### Documentation
- README — "Services" rewritten around the codegen accessors (raw getters described as internal); the registration note updated to the coded-error behavior and the duplicate-name caveat; the Offline-caching section condensed (full refresh table now in `ARCHITECTURE.md`); the "Backend backlog" section moved to `ARCHITECTURE.md`.

### Known issues / Backend backlog
- **Duplicate display name still isn't coded.** A unique-constraint collision on the player `name` currently comes back as an unhandled `500` (raw traceback), so it is *not* swallowed by `IsAlreadyRegisteredError` and surfaces as a thrown `FlockException` with `ErrorCode == Unknown`. A provisional `FlockErrorCode.PlayerNameAlreadyTaken` is reserved for when the backend returns a coded `400` (also an info-disclosure fix). Until then, pass `null` for `name` on `RegisterWith*` and collect the display name on a separate post-registration screen. See [ARCHITECTURE.md](ARCHITECTURE.md).

## [1.18.0]

### Changed
- **Money mutations no longer auto-retry ambiguous failures.** `client.Commands.AddGameFundsAsync` and `client.Shop.PurchaseAsync` are non-idempotent and carry no idempotency key, so on a lost response a blind retry could double-credit / double-charge. They now retry only failures the server *provably didn't process* — HTTP `408` / `429`, honoring `Retry-After` — and surface ambiguous failures (client timeout, dropped connection, `5xx`) to the caller, so wrap these calls in `try/catch`. Reads and the idempotent commands (`UpdatePlayerData`, `UpdatePlayerDataField`, `UnlockAchievement`) are unchanged.
- **Generated `UpdateAsync` is now an instance method** on each template type instead of an extension method, so it's available on the object with no extra `using Flock.Generated.Templates;`. The call site is unchanged (`await progress.UpdateAsync()`) and existing code keeps compiling; re-sync to regenerate. (Generated file renamed `FlockTemplateCommands.g.cs`.)
- `client.Shop.PurchaseAsync` — `playerId` is now optional and defaults to the signed-in player (`CurrentPlayerId`); existing two-arg calls still compile.

### Added
- **Shop codegen.** `Sync Schemas` now generates `Flock.Generated.Shops`: a typed `Get<Shop>ShopAsync()` accessor per shop, plus `FlockShopItemId` / `FlockFundId` enums of the available ids and generated `PurchaseAsync(FlockShopItemId)` / `AddGameFundsAsync(FlockFundId)` extension methods. `FlockFundId` members are the currency id (e.g. `_100`; currency names live only on an OAuth2 admin endpoint, unreachable with the SDK API key); the generated `AddGameFunds` sends the currency id and resolves `player_data_id` from the player's currency wallet (the row for the player template tagged `currency`) — codegen bakes that template's id so the row resolves directly, skipping a runtime template scan. The enum-typed methods exist only after a sync; the raw string methods remain. Shop changes are covered by the content-hash drift check.
- **`client.Commands.AddGameFundsAsync` and `UnlockAchievementAsync(achievementName)` no longer take `player_data_id`** — they resolve the current player's row from the player-template **tag** (`currency` / `achievement`). `AddGameFunds` has two public overloads: `(currency, amount)` resolves the `currency`-tagged template at runtime (`client.Player.GetTemplateByTagAsync`), and `(currency, amount, currencyTemplateId)` takes a known template id (codegen passes the baked id). (Breaking: the prior `(playerDataId, …)` signatures are removed.)
- EditMode tests (`RetryHandlerTests`) for the retry decision: idempotent ops retry transient failures; non-idempotent ops surface ambiguous failures and `5xx` but still retry `408` / `429`; permanent `4xx` is never retried.

### Fixed
- Generated command accessor XML doc no longer drops a word ("Send updated  of {Type}" → "Send the updated {Type}").

## [1.17.0]

### Added
- **Headless codegen for CI.** `Flock.Editor.Codegen.FlockCodegenCli.Sync` and `.Verify` run codegen from the command line (`-batchmode -executeMethod …`, without `-quit`). `Verify` writes nothing and exits non-zero when the committed generated code is stale versus the backend — usable as a PR gate. Exit codes: `0` ok / no drift, `1` could not run, `2` drift.
- **Schema content hash.** The generated `SchemasManifest` now bakes a `ContentHash` of the schema content (each template/config's id, name, tag, and field tree). `Verify` re-fetches and compares it, so field/type/tag edits *within* the same Game Version are detected — drift the Game Version ID check alone misses.
- EditMode tests (`Flock.Tests.Editor`) for the codegen pure logic: `SchemaHasher`, `CodeGenNamingHelpers`, `TypeMap`, and `FlockBuildGuard.GetBuildBlockReason`.

### Changed
- **Codegen sync is now fail-closed.** A failed schema fetch (offline, bad key, server error) throws instead of returning an empty snapshot, so the emitters no longer wipe `Templates/` / `Commands/` / `Configs/` and overwrite the manifest with empty stubs on a transient failure. Legitimately empty results still generate normally.

### Fixed
- In-product references to the editor window now use its real menu path, **Qwacks > Flock** (previously "Qwacks > Editor", which is not a menu), and codegen instructions point to its **Codegen** tab (previously "Flock > Sync Schemas", a menu that never existed) — across the README, runtime error messages, tooltips, the in-editor guide, and the Quick Start sample.

## [1.16.0] - 2026-06-19

### Added
- `client.Authentication.LoginWithFacebookAsync(facebookId)` and `LoginWithDiscordAsync(discordId)` — Facebook and Discord sign-in via the generic `POST /v1/player/login` route (the backend validates the provider id). Login only; see Known issues.
- `FlockAuthMethod.Facebook` and `FlockAuthMethod.Discord` — new auth-method enum values, surfaced on `FlockAuthInfo` through `FlockEvents.OnAuthenticated`.

### Documentation
- README — Facebook/Discord added to the auth provider list, the usage examples (login-only), and the `OnAuthenticated` event description.
- In-editor SDK Guide — provider list updated to include Facebook/Discord.

### Known issues / Backend backlog
- **Facebook/Discord are login-only.** There is no `register/facebook|discord` route, and the generic `/v1/player/register` accepts only email/password/name — so unlike Google/Apple/Steam these two have no registration method. Pending backend confirmation of whether first-time login auto-creates the player; if it does not, a register route is needed.

## [1.15.0] - 2026-06-18

### Added
- **WebGL HTTP support.** SDK HTTP now runs through an `IFlockHttpAdapter` seam selected per platform — `UnityWebRequest` on WebGL builds (where `System.Net.Http.HttpClient` has no transport), `HttpClient` everywhere else. The `FlockHttpClient` facade and all providers are unchanged. A custom transport can be injected via `FlockHttpClient.Configure(IFlockHttpAdapter)` (e.g. to mock HTTP in tests).
- `FlockInitConfig.HttpTimeout` (default 30s) — per-request timeout for API calls; the underlying client previously defaulted to 100s. Mirrored on the config asset and editor (Advanced > HTTP Retry Policy).
- `FlockInitConfig.AssetDownloadTimeout` (default off) and `FlockInitConfig.AssetDownloadRetryCount` (default 3) — opt-in per-download timeout and a download-specific retry count, independent of the API `RetryPolicy`. Modeled on Unity Addressables' `Timeout` / `RetryCount`. Mirrored on the config asset and editor (Asset Cache).
- `FlockSerializationException` — thrown when a 2xx response can't be turned into the expected type (malformed JSON or empty body). Non-retryable.
- `FlockException.Body` (raw server response body) and `FlockException.StatusCode` (moved to the base type, so auth/validation errors carry it too); `FlockNetworkException.RetryAfter`.
- Server `Retry-After` (delta-seconds or HTTP-date) is now honored on retry, bounded by `RetryPolicy.MaxDelay`.
- Asset downloads now retry transient failures through `RetryHandler` (backoff + jitter + permanent-4xx skip), re-issuing a fresh `UnityWebRequest` per attempt.

### Changed
- Error messages are stabilized and status-coded (e.g. `HTTP request failed (HTTP 500)`); the raw server body moved off the message onto `FlockException.Body` so error trackers bucket by type instead of payload. `FlockException.ToString()` appends `Body`, so console logs still show the server's reason.
- Malformed/empty 2xx responses now throw the non-retryable `FlockSerializationException` instead of a retried `FlockNetworkException`, and no longer trigger the offline snapshot fallback (which stays gated on `internetReachability` — the network is up in this case).
- Asset-download failures now carry `StatusCode` + `Body` and a stable message.

### Fixed
- Request cancellation now propagates as `OperationCanceledException` instead of being logged as a failed retry and surfaced as a `FlockNetworkException` (fixed in `RetryHandler` and `FlockProviderBase`).
- `IsAlreadyRegisteredError` now matches the server's "already registered" detail on `FlockException.Body` as well as the message, restoring the duplicate-registration skip after the body was moved off the message.
- `RetryHandler`'s jitter RNG is now thread-safe for concurrent retries (e.g. parallel asset downloads).
- `HttpRequestMessage` / `HttpResponseMessage` are now disposed per request.

### Documentation
- README "Platform notes" — WebGL note corrected: SDK HTTP works on WebGL via `UnityWebRequest`; only the asset/offline disk caches need disabling.

## [1.14.0] - 2026-06-17

### Added
- **Auto-Initialize On Load** (on by default): with `FlockConfig` set up, the SDK initializes itself at startup from `Assets/Resources/FlockConfig.asset` — no `FlockBootstrap` or `Create()` call — and restores a saved session in the background. Turn it off in Advanced Settings > Tools to drive init yourself (e.g. defer past a splash/EULA via `FlockBootstrap` or a manual `Create()`).
- **Lifecycle event replay.** `FlockEvents.OnInitialized` and `OnInitializationFailed` now replay to handlers that subscribe after init, so they fire reliably under auto-init (which initializes before scene scripts can subscribe).
- `FlockClient.InitializationError` exposes the last init failure (null after success), so a failed auto-init — which logs instead of throwing — is observable alongside `IsInitialized`.

### Changed — BREAKING
- **Synchronous init.** `FlockClient.CreateAsync` is replaced by synchronous `FlockClient.Create(config)`. The Game Version ID is now resolved at **edit time** (Qwacks > Editor) and baked into `FlockConfig`; runtime init makes no server call and works offline, including first launch. `FlockBootstrap.InitializeAsync()` is replaced by synchronous `Initialize()`; persisted-session restore runs in the background and reports via `FlockEvents.OnSessionRestored` (plus the `FlockClient.IsRestoringSession` flag), with no dependency on `FlockBootstrap`.
- A build guard fails the player build if the Game Version ID is unresolved (empty) **or has drifted from the generated schemas** (toggle in Advanced Settings > Tools).

### Removed
- Runtime Game Version name→ID resolution (`ResolveGameVersionAsync`) and its bootstrap-scope version snapshot. The codegen drift check now runs editor-side.

## [1.13.0] - 2026-06-16

### Added
- Editor Play-mode setup guard: entering Play with Flock not set up (missing/invalid `FlockConfig`, or a `FlockBootstrap` with no/invalid config) now shows a fixable dialog instead of failing silently at runtime. Per-project toggle via **Play-Mode Setup Guard** in Qwacks > Editor. Editor-only; no build impact.
- Quick-Start sample (`Samples/QuickStart/`): a single IMGUI script — with a `FlockBootstrap` in the scene it logs in with the device id, shows the player, fires a test analytics event, and reads player data. Bundled in the package and the `.unitypackage`.
- Setup checklist: the **Qwacks > Editor** Configuration tab now opens with a one-look **Setup** panel (FlockConfig asset · credentials · connection · scene bootstrap · schemas), each with a one-click fix. Consolidates the previously-scattered status signals; the connection check is cached per session and invalidated when credentials change.
- Qwacks > Editor: optional/tuning settings (debug logs, analytics, asset cache, HTTP retry, tools) moved to a new **Advanced** tab; the Configuration tab now focuses on the Setup checklist + credentials.

## [1.12.0] - 2026-06-12

### Added
- `FlockEvents` — static hub exposing 11 public SDK lifecycle events: `OnInitialized`, `OnInitializationFailed`, `OnShutdown`, `OnAuthenticated`, `OnTokenRefreshed`, `OnAuthExpired`, `OnLoggedOut`, `OnSessionStarted`, `OnSessionEnded`, `OnSessionPaused`, `OnSessionResumed`. Subscribe anytime (the hub never throws, unlike `FlockClient.Instance` pre-init); events are raised on the Unity main thread; a throwing subscriber is logged and never breaks the SDK or other subscribers. All subscriptions are cleared automatically on `Shutdown()` and on play-session start with domain reload disabled. Every raise is debug-logged with its subscriber count when `EnableDebugLogs` is on.
- Event payload types: `FlockAuthInfo` (`PlayerId` + `FlockAuthMethod`: Email/Device/Google/Apple/Steam/SessionRestore) and `FlockSessionEndedArgs` (`FlockSessionSnapshot` + `FlockSessionEndReason`: Logout/Timeout/Quit/Restarted/Manual). Sessions recovered from a crashed previous launch do not raise `OnSessionEnded`.
- `IAnalyticProvider.StartSessionAsync` / `EndSessionAsync` — manual session control is now on the public interface (`StartSessionAsync` was previously private on the concrete provider), making `AutoStartSession = false` actually usable and the existing README session examples compile. For game-defined session boundaries (foreground idle, kiosk user switching, consent toggles) — not needed on quit/logout, which end the session automatically. Manual end raises `OnSessionEnded` with reason `Manual`.

### Changed
- `FlockClient.OnSessionExpired` still works unchanged; its doc now points at `FlockEvents.OnAuthExpired` (same moment, clearer name — the old name collided with the analytics session concept).
- Internal: `FlockSession.End`/`Reset` now require an explicit end reason at every call site (no public API impact).

### Documentation
- README — "Events" subsection under Analytics: the full event table (lifecycle/auth/session), subscription contract, and an OnEnable/OnDisable example.

## [1.11.0] - 2026-06-10

### Added
- Offline caching layer: read-API responses are snapshotted to disk (`persistentDataPath/Flock/snapshots/{gameVersionId}/`) and served when the device is offline or the server is transiently unreachable. Online calls are unchanged — the server is always fetched first, and there are no TTL/freshness settings by design.
- `FlockInitConfig.EnableOfflineCache` (default `true`; set `false` on WebGL) and `FlockInitConfig.OfflineCacheDirectory`, mirrored on the config asset under "Offline Cache".
- Offline SDK init: `FlockClient.CreateAsync` snapshots the GameVersion name→id resolve and uses the last-known id when the network is unavailable, instead of failing after retry backoff. A first-ever run still requires network once. Authoritative 4xx responses (e.g. deleted version name) still fail init.
- Asset metadata index (memory + disk, merged on write): previously downloaded assets load fully offline, and `DownloadAsync` no longer pays a metadata round trip for known assets. `Asset.GetByNameAsync` resolves from the index after the first fetch instead of re-downloading the full list per call.
- Once-per-run caching for the `Schema`, `Game`, and `Shop` providers (`Config` and `Player` templates already had it), each with a `ClearCache()` that also deletes its disk snapshots. Schema shares the config snapshot scope — same endpoints, stored once.
- Command write-through: every game command applies its server-returned `PlayerData` row to the player cache (`PlayerProvider.ApplyServerPlayerData`), so reads after writes are current without manual `ClearCache()` or a refetch.
- `FlockNetworkException.IsPermanentStatus(int?)` — single shared transient-vs-permanent HTTP status rule (no status / 5xx / 408 / 429 are transient; other 4xx are authoritative).

### Changed
- `Shop.PurchaseAsync` reads the shop item from the cache after warmup (4 → 3 round trips). The purchase POST itself is never cached or queued; ban status, inventory, and transactions remain uncached and always live.
- `RetryHandler` and `FlockEventCache` now call the shared status rule instead of private duplicates (behavior unchanged).



## [1.10.0] - 2026-06-09

### Added
- `AssetSchema.ExtensionType` (string, nullable) and `AssetSchema.SizeBytes` (long?, nullable) — populated from the matching OpenAPI fields on `GET /v1/asset` / `GET /v1/asset/{id}` responses. Lets consumers inspect file type and size without downloading.
- `client.Asset.IsCached(string assetId, DateTime updatedAt)` and `client.Asset.IsCached(AssetSchema asset)` — predicate that returns `true` when a cache entry for the given asset + `UpdatedAt` exists on disk. Reports literal on-disk state and does NOT consult `EnableAssetCache`. Side effect: bumps the cached file's `LastWriteTimeUtc` on hit (matches the existing LRU lookup behavior).
- `client.Asset.PreloadAsync(string assetId, ...)` and `PreloadAsync(AssetSchema asset, ...)` — warms the disk cache without decoding into a Unity type. Internally routes through `DownloadAsync<byte[]>` but returns `Task` so the bytes don't leak through the API surface. Cache-hit short-circuits, so calling twice for an unchanged asset is cheap.

### Changed
- `AudioClip` downloads now resolve their Unity `AudioType` from `AssetSchema.ExtensionType` (`mp3` → `MPEG`, `wav` → `WAV`, `ogg` → `OGGVORBIS`, `aif`/`aiff` → `AIFF`) instead of always passing `AudioType.UNKNOWN`. Falls back to `UNKNOWN` when `ExtensionType` is null or unrecognized. Improves audio decode reliability on WebGL and mobile where `UNKNOWN` is brittle.
- Asset download now does a preflight cache-cap check using `asset.SizeBytes`: when `EnableAssetCache=true`, `AssetCacheMaxSizeMB > 0`, and `asset.SizeBytes > MaxSizeBytes`, caching is disabled for that specific download with a warning. The asset still downloads — only the cache write is skipped. Prevents the previous LRU-evict-every-other-asset thrash when one oversized asset alone exceeded the cap.

### Documentation
- README — short note above the asset examples framing Flock assets as "files on a CDN with metadata," not Unity bundles, and pointing prefab/scene/material use cases at Addressables. Helps new consumers avoid trying to use the SDK for content it isn't designed for.
- README — usage examples for `PreloadAsync` and `IsCached`.


## [1.9.0] - 2026-06-03

### Added
- `FlockAnalyticsConfig.EventBufferFlushIntervalSeconds` (default `10f`) — interval for the periodic analytics flush. The disk-backed event cache is now the single send path; entries drain on this interval plus session pause / session end / online-event triggers.
- `FlockClient.ApiVersion` const and `FlockClient.GetVersionedApiUrl()` (also on `IFlockClient`) — single source of truth for the `/v1` segment. Bump `ApiVersion` once when the backend cuts a new major API version (mirror in the Unreal SDK for parity).
- `client.Player.GetBanAsync(playerId)` — moved from `client.Ban.GetPlayerBanAsync(playerId)`. Endpoint (`GET /v1/player-ban`) unchanged.
- General `GameHub` changes for Editor, analytics logic, and `FlockClient`.
- `Flock.Models.TypedSchema` and `Flock.Models.DataField` — shared model types for the backend's new flattened typed-schema shape (one item per schema field with `Type` / `FieldName` / `TypeName`, recursively nested via `Schema` for objects/lists/dicts).
- `IList<DataField>.ToFlatObject()` extension — rebuilds a `JObject` from a flattened DataField list so generated `Get*Async` template accessors can deserialize the payload into a strongly-typed POCO via `.ToObject<T>()`.
- `IReadOnlyList<TypedSchema>.ToDataFieldList(object poco)` extension — inverse of `ToFlatObject`, walks the schema + a JObject view of a populated POCO to produce the flattened wire shape. Powers the generated command write path.
- Generated player template classes now expose `public static IReadOnlyList<TypedSchema> Schema { get; }`, initialized at codegen time from the template's typed schema. No runtime JSON parsing.
- Generated command accessors — one `UpdateAsync` extension method per template, declared on the template type itself so it lights up in IntelliSense on the instance: `await test.UpdateAsync()`. The method validates `template.PlayerDataId`, builds the flattened DataField list via `{Template}.Schema.ToDataFieldList(template)`, and routes through `FlockCommandProvider.UpdatePlayerDataAsync`.
- `client.Commands.UnlockAchievementAsync(playerDataId, achievementName)` — wraps the new `POST /v1/game_command/unlock_achievement` typed endpoint, returns the updated `PlayerData`.

### Changed
- **Behavior**: `TrackEventAsync` and the log-event tracking path no longer attempt a live send — every call enqueues to disk and returns. Drain happens via the new flush triggers, so server-side visibility lags by up to `EventBufferFlushIntervalSeconds` after a tracked event. Quit and end-session paths do a best-effort 2s flush before completing.
- `FlockSession.RecoverCrashedSession` → `RecoverOrphanedSession`. Recovered sessions are no longer flagged as crashes (see Removed).
- **Breaking**: `PlayerTemplateSchema.Schema` is now `List<TypedSchema>` (was `Dictionary<string, object>`), matching the OpenAPI flattened typed-schema shape on `GET /v1/player_template*`.
- **Breaking**: `PlayerTemplateSchema.Data` is now `List<DataField>` (was `Dictionary<string, object>`).
- **Breaking**: `PlayerData.Data` is now `List<DataField>` (was `Dictionary<string, object>`).
- **Breaking**: `GameConfigSchema.Schema` is now `List<TypedSchema>` (was `Dictionary<string, object>`); `GameConfigSchema.Data` and `GamePatchSchema.Data` are now `List<DataField>`. `GetDataAs<T>` routes through `Data.ToFlatObject().ToObject<T>()` to preserve the existing typed-deserialization contract.
- GameConfig codegen is back on the same walker player templates use — `GameConfigEmitter` emits typed `*Config` partial classes with `SourceId` / `SourceName` / `SourceTag` constants and a static `IReadOnlyList<TypedSchema> Schema { get; }` initialized at codegen time. `ConfigAccessorEmitter` re-emits `client.Config.Get{Name}Async()` extensions.
- **Breaking**: `FlockCommandProvider` posts to typed per-command endpoints (`/v1/game_command/update_player_data`, `/update_player_data_key`, `/add_game_funds`, `/unlock_achievement`) instead of going through `/v1/game_command/execute` with a `game_command_id` payload. All four methods drop the leading `gameCommandId` parameter and return `Task<PlayerData>` instead of `Task<List<GameCommandExecutionResult>>`. `UpdatePlayerDataAsync` also takes `List<DataField> data` instead of `Dictionary<string, object> data`.
- Codegen — `SchemaPropertyEmitter` walks the flattened `IList<TypedSchema>` shape recursively. `object` fields emit a nested partial class, `list`/`array` fields emit `List<T>`, `dict` fields emit `Dictionary<string, T>`, all resolved through the same walker. `TypeMap.MapTypeString` was renamed to `MapPrimitiveTypeString` and trimmed to primitive types only — composites are handled structurally by the walker.
- Codegen — generated `.g.cs` files use `using` directives (`System`, `System.Collections.Generic`, `Flock.Models`, `Newtonsoft.Json`, `Newtonsoft.Json.Linq`) instead of `global::`-qualified types in the body.
- Internal: `Editor/Codegen/Naming.cs` renamed to `Editor/Codegen/CodeGenNamingHelpers.cs`.

### Removed
- **Breaking**: `FlockBanProvider`, `client.Ban`, and the `FLOCK_NO_BAN` compile flag — folded into `PlayerProvider` (covered by `FLOCK_NO_PLAYER`). Migration: `client.Ban.GetPlayerBanAsync(id)` → `client.Player.GetBanAsync(id)`.
- `FlockSessionSnapshot.WasCrash` — session analytics no longer asserts crash status. A real crash reporter is out of scope for this layer.
- **Breaking**: `PlayerTemplateTag` enum. The `tag` field on `PlayerTemplateSchema` is `string` on the wire; the enum (used only by request-side models the SDK doesn't currently expose) will return when create/update endpoints are added.
- Dead internal models `PlayerDataRequest` and `UpdatePlayerDataRequest` — neither had callers.
- `FlockCommandProvider.ExecuteCommandAsync`, the `GameCommandExecutionRequest` / `GameCommandExecutionResult` models, and the `ICommandPayload` interface — the generic `/v1/game_command/execute` indirection is no longer in OpenAPI and is replaced by per-command typed endpoints.
- `Editor/Codegen/CommandLookup.cs` — placeholder command IDs are obsolete now that the SDK calls each command endpoint by name. Drop the file (and its `.meta`) from your project.
- The `Update{Template}FieldAsync(template, key, value)` extensions are no longer emitted — the simpler `Update{Template}Async(template)` method covers the typed-write use case end-to-end. Single-key writes remain available on `FlockCommandProvider.UpdatePlayerDataFieldAsync` directly.

### Known issues / Backend backlog
- **Registration error codes are unstructured.** `POST /v1/player/register*` failures come back as plain text with no error-code field. The SDK uses a temporary string-match heuristic (`IsAlreadyRegisteredError`) that detects "already / registered / exists / in use / taken" and returns `null` from `RegisterWith*` instead of throwing. This conflates name collisions with credential collisions, and breaks the moment the backend changes its error wording. **Workaround until the backend ships structured codes (e.g. `NAME_TAKEN`, `EMAIL_REGISTERED`):** pass `null` for `name` on `RegisterWith*` and collect the display name on a separate post-registration screen where retry-on-collision UX is natural. See [README "Backend backlog"](README.md#backend-backlog).

## [1.8.0] - 2026-05-01

### Added
- Codegen — `Flock > Sync Schemas` (or the editor window's Codegen tab) fetches player templates and game configs from the backend and writes typed C# accessors. Output defaults to `Assets/Flock/Generated/`; configurable per project via `FlockConfigAsset.generatedCodePath`.
  - One class per player template under `Flock.Generated.Templates.*Template` with `[JsonProperty]` fields matching the schema
  - One class per game config under `Flock.Generated.Configs.*Config`
  - `FlockPlayerProviderExtensions.Get*Async()` — typed wrapper that resolves the current player's PlayerData for the template via `Client.CurrentPlayerId`. No `playerDataId` argument needed at the call site.
  - `FlockConfigProviderExtensions.Get*Async()` — typed wrapper over `client.Config.GetByIdAsync<T>` using the config's `SourceId`
  - `FlockCommandProviderExtensions.Update*Async(template)` and `Update*FieldAsync(template, key, value)` — execute backend `UpdatePlayerData` / `UpdatePlayerDataKey` commands with the typed payload (requires `Editor/Codegen/CommandLookup.cs` to be filled in — see Backend backlog in the README)
  - `Flock.Generated.SchemasManifest` — records the `GameVersionId` the code was generated for
- `CodeGenValidator` — runs at SDK init and warns when the generated `SchemasManifest.GameVersionId` does not match the configured game version (re-run sync to clear it). Replaces the previous `SchemasManifestProbe`.
- `Flock > Clean Generated` — wipes the generated folder. Also exposed as a button in the editor window.
- `FlockConfigAsset.generatedCodePath` — project-relative output folder for codegen (default `Assets/Flock/Generated`). Must start with `Assets/`.
- `FlockBootstrap` MonoBehaviour — drop-in scene component that calls `FlockClient.CreateAsync(asset.ToInitConfig())` for you. References a `FlockConfigAsset` by reference, never copies values, so the asset stays the single source of truth.
  - `initializeOnAwake` toggle — disable to call `bootstrap.InitializeAsync()` yourself (e.g. after a splash screen or EULA gate)
  - `dontDestroyOnLoad` toggle — survives scene loads when the GameObject is at the scene root
  - `OnInitialized` / `OnInitializationFailed` events
  - Static instance check destroys duplicates with a warning
- `Qwacks > Editor` — new editor window with Configuration and Codegen tabs. Renders `FlockConfigAsset` directly via `SerializedObject`, so edits save into the asset with no separate Save step. Includes Test Connection, Locate Asset, Add Flock Bootstrap to Scene, Sync Schemas, and Delete Generated Code actions.
- `client.Player.GetMyDataByTemplateAsync(templateId)` — resolves the current authenticated player's PlayerData for a given template via `Client.CurrentPlayerId`. Per-player snapshot cache + in-flight de-duplication so concurrent reads share a single round-trip. Generated `Get*Async` extensions delegate to this.
- Codegen — `SchemaPropertyEmitter` now generates typed list classes for JArray schema fields. `[{ "field": "type" }]` becomes `List<*Item>` with a generated nested class for the element shape; `["typename"]` becomes `List<csType>`. Empty / mixed-shape arrays are skipped with a warning.

### Changed
- `FlockBehaviour.OnPause` event renamed to `OnAppBackgrounded` to disambiguate from gameplay pause. The Unity callback name (`OnApplicationPause`) is unchanged — it's just the SDK-internal event that was renamed. Internal-only API; no public surface affected.
- Editor window is now a thin view of `FlockConfigAsset`. The previous `EditorPrefs` mirror (`Flock_ApiUrl`, `Flock_ApiKey`, `Flock_GameId`, `Flock_GameVersion`, `Flock_EnableDebugLogs`, `Flock_GeneratedCodePath`) and the manual Save / Reset buttons have been removed — there's only one place values live now.
- Codegen output is sorted by source ID for stable diffs across server reorderings.
- Internal: `AccessorEmitter` renamed to `ConfigAccessorEmitter` for symmetry with `PlayerAccessorEmitter`.

### Removed
- `Qwacks > Configuration` menu item — replaced by `Qwacks > Editor`. The asset path is unchanged (`Assets/Resources/FlockConfig.asset`); existing saved assets continue to work.
- `ConfigPatchMerger` — was unused. Game patches are returned as-is from the backend.

## [1.7.0] - 2026-04-26

### Added
- `FlockAssetProvider` — new provider exposed as `client.Asset`
- `client.Asset.GetAllAsync` — list all assets for the game via `GET /v1/asset`
- `client.Asset.GetByIdAsync` — fetch a single asset by ID via `GET /v1/asset/{asset_id}`
- `client.Asset.DownloadAsync<T>` — generic typed download helper with four overloads:
  - `(string assetId)` — fetches the schema then downloads
  - `(AssetSchema asset)` — skips the lookup when the caller already has the schema
  - `(IEnumerable<string> assetIds)` — batch download in parallel
  - `(IEnumerable<AssetSchema> assets)` — batch download in parallel from pre-fetched schemas
  - Supported `T`: `Texture2D`, `Sprite`, `AudioClip`, `string`, `byte[]`
- `client.Asset.GetByNameAsync` — client-side stopgap that fetches all assets and filters by name (O(N) until a backend `/v1/asset/by-name/{name}` endpoint exists)
- Disk cache for asset downloads under `Application.persistentDataPath/flock_assets/`, keyed by asset ID + `UpdatedAt`. Subsequent downloads of the same asset version are loaded from disk via `file://` URLs so all `T` types still go through the existing `UnityWebRequest` extractor.
- `FlockInitConfig.EnableAssetCache` — toggle the disk cache (default `true`)
- `FlockInitConfig.AssetCacheDirectory` — override the cache directory; defaults to `Application.persistentDataPath/flock_assets/` when null/empty
- `FlockInitConfig.AssetCacheMaxSizeMB` — cap total cache size in MB; oldest entries are evicted (LRU) when exceeded. Default `100` MB; set to `0` for unlimited
- `client.Asset.CacheDirectory` — resolved absolute path of the active cache directory
- `client.Asset.ClearCache` — wipe the on-disk cache
- Cache safety: atomic writes (`.tmp` + move), automatic deletion of older versions of the same asset on `UpdatedAt` change, and asset ID sanitization to prevent path traversal
- README "Platform notes" — documents that the disk cache should be disabled on WebGL builds (`Application.persistentDataPath` is IndexedDB-backed and doesn't support synchronous file writes)
- `AssetSchema` model with `S3DownloadUrl` for direct downloads
- `IAssetProvider` interface
- `client.Config.GetPlayerFeaturesAsync` — get the feature config for the game version a player was last logged into via `GET /v1/game_config/player/{player_id}/features`, with typed `<T>` overload
- `client.Game.GetGameVersionByNameAsync` — fetch a game version by name via `GET /v1/game_version/by-name/{name}`
- `client.RefreshTokenAsync` — explicit token refresh via `POST /v1/player/token/refresh`; the SDK already refreshes silently on 401, this exposes manual control
- `client.OnSessionExpired` event — fires when a refresh attempt fails so the game can show a re-login UI
- `FlockBanProvider` — exposed as `client.Ban`
- `client.Ban.GetPlayerBanAsync` — fetch the active ban (if any) for a player via `GET /v1/player-ban`
- `PlayerBan` and `FeatureBan` models
- `IFlockClient` now exposes `Ban` and `Asset`

### Changed
- `IConfigProvider` extended with `GetPlayerFeaturesAsync` (and typed overload)
- All v1 endpoints in the OpenAPI spec are now implemented in the SDK

## [1.6.0] - 2026-04-22

### Added
- `FlockAuthProvider` — dedicated provider for all authentication flows, exposed as `client.Authentication`
- `client.Authentication.LoginWithGoogleAsync` / `RegisterWithGoogleAsync` — Google auth via `POST /v1/player/login/google` and `/v1/player/register/google`
- `client.Authentication.LoginWithAppleAsync` / `RegisterWithAppleAsync` — Apple auth via `POST /v1/player/login/apple` and `/v1/player/register/apple`
- `client.Authentication.LoginWithSteamAsync` / `RegisterWithSteamAsync` — Steam auth via `POST /v1/player/login/steam` and `/v1/player/register/steam`
- `client.Authentication.Logout` — clears local authentication state
- Models: `PlayerGoogleLoginRequest`, `PlayerGoogleRegistrationRequest`, `PlayerAppleLoginRequest`, `PlayerAppleRegistrationRequest`, `PlayerSteamLoginRequest`, `PlayerSteamRegistrationRequest`

### Changed
- **Breaking**: `LoginWithEmailAsync`, `LoginWithDeviceAsync`, `RegisterWithEmailAsync`, `RegisterWithDeviceAsync` moved from `FlockClient` to `FlockAuthProvider` — call via `client.Authentication.X` instead of `client.X`
- Token state on `FlockClient` is now only settable through the internal `SetTokens` entry point used by `FlockAuthProvider`; raw tokens remain private

### Removed
- **Breaking**: `client.ClearTokens()` — use `client.Authentication.Logout()` instead. Removed from `IFlockClient`.

## [1.5.0] - 2026-04-20

### Added
- `GetGameConfigsAsync(SchemaTag)` and `GetGameConfigsByVersionAsync(SchemaTag)` on `FlockConfigProvider` — fetch game configs filtered by tag (`currency`, `gameplay`, etc.) via `GET /v1/game_config` and `GET /v1/game_config/version`
- Both methods have typed `<T>` overloads using `GetDataAs<T>()`
- `PlayerProvider` — centralized provider for all player data and player template operations, replaces `PlayerDataProvider`
- `client.Player.GetTemplatesAsync` — list all player templates for the game version
- `client.Player.GetTemplateByIdAsync` — get a single player template by ID
- `client.Player.GetTemplateByNameAsync` — get a single player template by name
- `client.Player.GetTemplatePlayerDataAsync` — get all player data records for a template
- `PlayerTemplateSchema` model with `GetDataAs<T>()` helper
- `PlayerTemplateTag` enum (`gameplay`, `currency`, `achievement`)
- `IAnalyticProvider` interface — decouples analytics callers from the concrete provider
- `NullAnalyticsProvider` — no-op implementation used when analytics is disabled, eliminates null checks on `client.Analytics`

### Changed
- `PlayerDataProvider` renamed to `PlayerProvider`, exposed on `FlockClient` as `client.Player` (was `client.PlayerData`)
- `IPlayerService` updated to include all player template methods
- `IFlockClient.Analytics` now typed as `IAnalyticProvider` instead of `FlockAnalyticsProvider`
- `FlockAnalyticsProvider` now implements `IAnalyticProvider`
- Analytics no longer requires a null check before use — when `Enabled: false`, `client.Analytics` returns a `NullAnalyticsProvider` that silently no-ops all calls

## [1.4.0] - 2026-04-01

### Added
- Analytics system (`FlockAnalyticsProvider`) with full v1 endpoint coverage
- `client.Analytics.StartSessionAsync` — start a player session
- `client.Analytics.EndSessionAsync` — end the current session
- `client.Analytics.TrackEventAsync` — track a single event
- `client.Analytics.TrackEventsAsync` — track events in batch
- `client.Analytics.RecordTransactionAsync` — record a purchase/transaction
- `client.Analytics.RecordScreenView` — manually record a screen view
- `FlockBehaviour` — DontDestroyOnLoad singleton for Unity lifecycle events
- `FlockSession` — session state with pause tracking, FPS sampling, heartbeat, crash recovery
- `FlockAnalyticsConfig` — configurable session timeout, heartbeat interval, bounce threshold, FPS tracking
- `FlockSessionSnapshot` — serializable session state for persistence and server calls
- `FlockDeviceInfo` — captures platform, OS, device model, screen, memory, SDK version
- `FlockSdkVersion` — SDK version constant sent with session start requests
- `PatchAsync` on `FlockHttpClient` for the session end endpoint
- Session crash recovery via PlayerPrefs on next launch
- Session timeout detection on app resume (configurable, default 30s)
- Automatic analytics transaction recording on `Shop.PurchaseAsync`
- Analytics config exposed on `FlockConfigAsset` ScriptableObject

### Changed
- Renamed `Services` folder to `Providers` (`FlockGameService` → `FlockGameProvider`, `PlayerDataService` → `PlayerDataProvider`)
- `FlockInitConfig` now accepts `FlockAnalyticsConfig`
- `ClearTokens` resets the active analytics session
- `IFlockClient` now exposes `Analytics`, `HasActiveSession`, `CurrentSessionId`

## [1.3.0] - 2026-03-14

### Added
- Shop system (`FlockShopProvider`) with full v1 endpoint coverage
- `client.Shop.GetAllAsync` — list shops (paginated)
- `client.Shop.GetByIdAsync` — get shop by ID
- `client.Shop.GetItemAsync` — get shop item by ID
- `client.Shop.GetItemsByShopAsync` — get items by shop (with optional patch_id filter)
- `client.Shop.PurchaseAsync` — execute shop transaction
- `client.Shop.GetPlayerInventoryAsync` — get player inventory (paginated)
- Models: `Shop`, `ShopItem`, `ShopData`

### Changed
- Moved `PurchaseShopItemAsync` and `GetPlayerInventoryAsync` from `FlockCommandProvider` to `FlockShopProvider`
- `PlayerDataService` is now read-only (removed `CreateAsync` and `UpdateAsync` — use game commands instead)
- `PlayerDataService` uses API key headers instead of bearer auth (matching spec)

## [1.2.0] - 2026-03-07

### Added
- Game commands (`FlockCommandProvider`) for server-side operations via `POST /v1/game_command/execute`
- `client.Commands.UpdatePlayerDataAsync` — update player data through a backend command
- `client.Commands.UpdatePlayerDataFieldAsync` — update a single field in player data
- `client.Commands.AddGameFundsAsync` — add currency funds to a player
- `ICommandPayload` internal interface for type-safe command inputs
- Models: `GameCommandExecutionResult`, `PlayerInventory`

## [1.1.0] - 2026-02-25

### Changed
- Restructured config system: `client.Config` now returns game configuration data from `/v1/game_patch` endpoints
- `client.Patches` replaced by `client.Schema` for config schema validation (`/v1/game_config` endpoints)
- `IConfigProvider` now wraps game patch endpoints (returns `GamePatchSchema`)
- Removed automatic patch merging from config provider (patches ARE the config)

### Added
- `FlockSchemaProvider` and `ISchemaProvider` for config schema validation endpoints

### Removed
- `FlockGamePatchProvider` (replaced by `FlockConfigProvider`)
- Auto-merge logic (`ApplyPatchToConfigAsync`, `ApplyPatchesToConfigsAsync`)
- Achievements (`FlockAchievementProvider`, `IAchievementProvider`, achievement models)
- Leaderboards (`FlockLeaderboardProvider`, `ILeaderboardProvider`, leaderboard models)

## [1.0.0] - 2025-02-13

### Added
- **Authentication**: Email login and registration (`LoginWithEmailAsync`, `RegisterWithEmailAsync`)
- **Authentication**: Device login and registration (`LoginWithDeviceAsync`, `RegisterWithDeviceAsync`)
- **Token Management**: JWT access token parsing with expiration checks
- **Game Configuration**: Fetch all configs, by ID, by schema ID (`FlockConfigProvider`)
- **Config Schema Validation**: Fetch schemas, by version, by ID (`FlockSchemaProvider`)
- **Game Info**: Fetch game and game version metadata (`FlockGameService`)
- **Player Data**: Create, read, update, list with pagination (`PlayerDataService`)
- **HTTP Layer**: `FlockHttpClient` with GET, POST, PUT and typed error handling
- **Retry Logic**: Exponential backoff with jitter via `RetryPolicy` and `RetryHandler`
- **Exception Hierarchy**: `FlockException`, `FlockAuthException`, `FlockNetworkException`, `FlockValidationException`
- **Editor Tools**: Configuration window (`Qwacks > Configuration`) and package builder (`Qwacks > Package Builder`)
- **Configuration**: `FlockConfigAsset` ScriptableObject and `FlockInitConfig` for code-based setup
- **Logging**: `IFlockLogger` interface with `UnityFlockLogger` and `NullFlockLogger` implementations
- **Interfaces**: `IFlockClient`, `IConfigProvider`, `IPlayerDataService`
- **Models**: Domain-specific model files for auth, game, config, and player data
- **Generic Response**: `GenericResponse<T>` envelope wrapping API results with error and response metadata
- **Paginated Response**: `PaginatedResponse<T>` for list endpoints
- **Sample**: `FlockExample.cs` demonstrating SDK usage
