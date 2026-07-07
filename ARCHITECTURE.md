# Flock Unity SDK — Code Map

Orientation map of the folders and classes. For **API usage and examples**, see [README.md](README.md) — this file only says *what each piece is*, not how to call it.

```
Runtime/      runtime SDK            (asmdef Flock.Runtime)
├─ Providers/   feature APIs (auth, player, config, game, shop, command, asset)
│  └─ Analytics/  analytics sender (+ no-op stub)
├─ Http/        transport, retry, provider base
├─ Auth/        JWT decode + per-platform secure token storage
├─ Analytics/   play-session tracking + event spool
├─ Models/      serializable wire DTOs
├─ Config/      FlockConfig asset + init params
├─ Exceptions/ Interfaces/ Logging/ Constants/ Docs/
Editor/       editor tooling           (asmdef Flock.Editor)
└─ Codegen/     schema → typed C# generation
PackageBuilder/Tests/Editor/   EditMode tests (asmdef Flock.Tests.Editor)
```

## Runtime/ (root)
- **FlockClient** — central singleton + entry point; created once via `Create()`, owns providers/tokens/config.
- **FlockBootstrap** — drop-in MonoBehaviour that calls `Create()` for you.
- **FlockAutoInitializer** — opt-in zero-touch init before the first scene (no component).
- **FlockBehaviour** — internal hidden MonoBehaviour; main-thread dispatch + app pause/quit hooks.
- **FlockEvents** — static hub for lifecycle events (authenticated, session ended, restored).
- **FlockEventModels** — event enums/payloads: `FlockAuthMethod`, `FlockAuthInfo`, `FlockSessionEndReason`, `FlockSessionEndedArgs`.
- **FlockSdkVersion** — SDK version string. · **FlockUtil** — on-disk token/file paths.

## Runtime/Providers
- **FlockAuthProvider** — login / register / token refresh + revoke / session restore / password reset / email verification / name preflight.
- **PlayerProvider** — player templates + player data (incl. by-name).
- **FlockConfigProvider** — game configs & patches (incl. by-name).
- **FlockGameProvider** — game + game-version lookups (incl. by-name).
- **FlockShopProvider** — shops, items, purchase, inventory (incl. by-name); `PurchaseStatus`/`TransactionType` enums.
- **FlockCommandProvider** — retry-safe game commands (funds, achievements, player-data writes).
- **FlockAssetProvider** / **FlockAssetCache** — asset fetch + local file cache.
- **FlockSnapshotStore** — on-disk snapshot cache backing offline reads.
- **Analytics/FlockAnalyticsProvider** — sends sessions/events/transactions. · **NullAnalyticsProvider** — no-op when `FLOCK_NO_ANALYTICS`.

## Runtime/Http
- **FlockHttpClient** — static GET/POST/… facade; maps status→exception, parses error code.
- **FlockEndpoints** — every relative API path the SDK calls (consts + parameterized builders); no raw path literals at call sites.
- **FlockProviderBase** — base class for providers; shared fetch + snapshot + validate helpers.
- **IFlockHttpAdapter** — per-platform transport seam; `FlockHttpRequest`/`FlockHttpResponse`/`FlockHttpResult` normalize it.
- **SystemNetHttpAdapter** (non-WebGL) / **UnityWebRequestHttpAdapter** (WebGL) — transport impls.
- **RetryPolicy** / **RetryHandler** — transient-failure backoff honoring `Retry-After`.

## Runtime/Auth
- **JwtTokenParser** / **JwtTokenClaims** — decode token + read claims (expiry, player id).
- **TokenStoreFactory** — picks the secure store per platform at compile time.
- **TokenStore/** — **ITokenStore** + `StoredTokens`, with **Android/Ios/Mac/Windows/WebGl/Other** secure-storage impls.

## Runtime/Analytics
- **FlockSession** — tracks the current play session (start/end/ids).
- **FlockSessionSnapshot** — persisted session state for quit/crash recovery.
- **FlockTerminationTracker** — next-launch dirty-exit detection: tombstone marker in PlayerPrefs, lifecycle-only classifier, emits `app_termination` via the event pipeline. · **FlockTerminationMarker** — the persisted tombstone model.
- **FlockEventCache** / **IEventCache** — queues events for batch + offline send.
- **FlockAnalyticsConfig** — batch/flush tunables. · **FlockDeviceInfo** — device/platform metadata.

## Runtime/Models
Plain serializable DTOs mirroring backend wire shapes — auth, analytics, shop, game-config, player-data, log, ban requests/responses. Structural ones worth knowing:
- **GenericResponse\<T>** — standard `{error,response,result}` envelope. · **CodedErrorResponse**/**CodedErrorDetail** — `{detail:{code,message}}` error envelope.
- **PaginatedResponse\<T>** — paged list wrapper.
- **TypedSchema** / **DataField** (+ extensions & JSON converters) — dynamic typed config/player-data values.
- **GameConfigSchema** / **GamePatchSchema** / **GameSchema** / **GameVersionSchema** / **PlayerTemplateSchema** — core domain schemas.

## Runtime/ (support)
- **Config/FlockConfigAsset** — the `FlockConfig.asset` ScriptableObject (api key, version, baked id). · **Config/FlockInitConfig** — runtime init params.
- **Exceptions/** — **FlockException** base (`Body`, `StatusCode`, `Code`/`ErrorCode`) + **Network/Auth/Validation/Serialization** subclasses by failure kind; **FlockErrorCode** enum = typed view of the backend `detail.code` contract (+ `FlockErrorCodes.Parse`).
- **Interfaces/** — provider contracts (`IFlockClient`, `IConfigProvider`, `IPlayerService`, `IAssetProvider`, `IAnalyticProvider`) + `SchemaTag`. Schema/template/config raw getters are `internal` — reachable only through the generated accessors (codegen-only by design).
- **Logging/** — **IFlockLogger** + **UnityFlockLogger** / **NullFlockLogger**.
- **Constants/FlockConstant** — shared constants. · **Docs/FlockSdkGuide** — in-editor Getting-Started text.

## Editor/
- **QwacksEditorWindow** — main editor window (**Flock > Settings**); the config asset is the source of truth.
- **FlockConfigLocator** — single source for "which FlockConfig asset".
- **FlockVersionResolver** — bakes Game-Version name→id at edit time so runtime init needs no network.
- **FlockPlayModeGuard** / **FlockBuildGuard** — block Play / build when the SDK is unset or schemas drifted.
- **FlockCodeGenValidator** — warns when the baked version id drifts from generated schemas.
- **FlockSetupChecklist** / **FlockSetupClassifier** (+ `FlockSetupItem`/`FlockSetupState`/`FlockSetupFacts`/verdict enums) — pure, testable setup-readiness logic.
- **FlockFirstRunBootstrap** — opens the window on first import. · **FlockSdkGuideEditor** — inspector for the guide.
- **FlockProviderManifest** — maps providers ↔ `FLOCK_NO_*` defines for event-subset builds.
- **FlockPackageBuilder** — assembles the distributable package.

## Editor/Codegen/
Writes typed accessors to `Assets/Flock/Generated/` (Flock-owned, wiped each sync).
- **FlockCodegenMenu** / **FlockCodegenCli** — menu + headless CI entry points.
- **SchemaFetcher** / **SchemaHasher** / **FlockSchemaSnapshot** — pull schemas + content-hash for drift detection.
- **TypeMap** — backend→C# type mapping. · **CodeGenNamingHelpers** — safe identifier names.
- **\*Emitter** (GameConfig, ConfigAccessor, PlayerAccessor, PlayerTemplate, SchemaProperty, Command, Shop) — generate the typed C#.
- **ManifestEmitter** — emits `SchemasManifest` (GameVersionId + hash). · `EmitResult`/`CodegenResult` — codegen DTOs.

## PackageBuilder/Tests/Editor/
EditMode tests (run via Unity Test Runner only): **CodeGenNamingHelpersTests**, **FlockBuildGuardTests**, **RetryHandlerTests**, **SchemaHasherTests**, **TypeMapTests**, **FlockErrorPipelineTests** (exception/`FlockErrorCode` mapping; has an `[Explicit]` live-backend test), **FlockConfigResolutionTests** (patch-else-config resolution).

## Offline caching

Reads are snapshotted to `persistentDataPath/Flock/snapshots/` and served when the server is unreachable (after one online session); the server is always fetched first, no TTLs. Settings on `FlockInitConfig` / the FlockConfig asset: `EnableOfflineCache` (default `true`; set `false` on WebGL) and `OfflineCacheDirectory`. Each provider's `ClearCache()` drops its in-memory and disk snapshots.

| Data | Refreshes |
|---|---|
| Configs, schemas, shop catalog, game info, asset metadata, player templates, player features | Once per launch (first access); new content appears next launch. |
| Player data | At launch, and after every game command (the command's response updates the cache). |
| Ban status, inventory, purchases | Never cached — always live. |

## Codegen — type mapping & CI

Primitive type mapping lives in `Editor/Codegen/TypeMap.cs` (`integer`→`int`, `string`→`string`, `datetime`/`date`/`timestamp`→`System.DateTime`, …). Composite types are walked structurally by `SchemaPropertyEmitter`: `object` → nested partial class, `list`/`array` → `List<T>`, `dict` → `Dictionary<string, T>`, resolved recursively.

Headless CI via `Flock.Editor.Codegen.FlockCodegenCli` (no editor UI):
- **`Sync`** — regenerates the typed accessors from the backend schema, then exits.
- **`Verify`** — writes nothing; exits non-zero when committed generated code is stale vs the backend. Catches a changed Game Version *and* field/type/tag edits within the same version (the manifest bakes a content hash that `Verify` re-fetches and compares). Use as a PR gate.

```bash
Unity -batchmode -projectPath . -executeMethod Flock.Editor.Codegen.FlockCodegenCli.Sync   -logFile -
Unity -batchmode -projectPath . -executeMethod Flock.Editor.Codegen.FlockCodegenCli.Verify -logFile -
```

Exit codes: `0` ok / no drift · `1` could not run · `2` drift (`Verify` only). **Do not pass `-quit`** — each method exits the editor itself once the backend round-trip completes; `-quit` would tear it down before codegen finishes. Both need valid Flock credentials and network.

## Backend backlog / known constraints

Behaviors constrained by the current backend; none block normal usage — each surfaces as a console warning with a workaround in place.

- **`name_already_taken` code + name-availability check** — Structured coded errors have landed for the already-registered family (`player.email_already_registered`, `player.device_already_registered`, `player.{google,apple,steam}_account_already_registered`), and the SDK now matches them via `FlockException.ErrorCode` / `FlockErrorCode` instead of string-matching; `RegisterWith*` still swallow those and return `null`. Still open: a **name** collision has no code — the backend surfaces it as an unhandled `500`, so name-collision error UX stays unreliable (a provisional `FlockErrorCode.PlayerNameAlreadyTaken` is reserved for when the backend ships `player.name_already_taken`). A dedicated name-availability check would also let callers validate as the user types.
- **Retry-safe session registration** — every `StartSession` creates a new server-side session row, and only the server knows its id. If the app quits while that request is in flight, the client loses the id and re-registers on next launch — orphaning the first row, which stays open forever (the SDK warns when it detects this). Fix: let the client supply a `client_session_id` so a retried registration returns the existing row instead of creating a duplicate.
- **Idempotency keys for money mutations** — `AddGameFunds` and shop `Purchase` mutate server state (currency, inventory) and carry no idempotency key, so the backend can't tell a genuine repeat from a network retry. To avoid double-crediting/double-charging, the SDK only auto-retries these two on failures the server **provably didn't process** (HTTP 408/429); ambiguous failures (client timeout, dropped connection, 5xx) surface to the caller to catch and decide. The robust fix is a client-supplied `idempotency_key` the backend dedupes on, after which full auto-retry can return. The idempotent commands — `UpdatePlayerData`, `UpdatePlayerDataField`, `UnlockAchievement` — are unaffected.
