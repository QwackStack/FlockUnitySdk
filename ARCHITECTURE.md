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
- **FlockClient** — central singleton + entry point; created once via `Create()`, owns providers/tokens/config. Public seams for a separate Qwacks service (no `InternalsVisibleTo`): `GetGameHeaders()` (a copy of the key + version headers, never the bearer), `RetryPolicy` (a copy of the init retry settings) and `ServerSessionId` (the server's id for the live analytics session, null until registered; never the local id).
- **FlockBootstrap** — drop-in MonoBehaviour that calls `Create()` for you.
- **FlockAutoInitializer** — opt-in zero-touch init before the first scene (no component).
- **FlockBehaviour** — internal hidden MonoBehaviour; main-thread dispatch + app pause/quit hooks.
- **FlockEvents** — static hub for lifecycle events (authenticated, session ended, restored).
- **FlockEventModels** — event enums/payloads: `FlockAuthMethod`, `FlockAuthInfo`, `FlockSessionEndReason`, `FlockSessionEndedArgs`.
- **FlockSdkVersion** — SDK version string. · **FlockUtil** — on-disk token/file paths.

## Runtime/Providers
- **FlockAuthProvider** — login / register / token refresh + revoke / session restore / password reset / email verification / name preflight / **account linking** (link email, device and the five OAuth providers; unlink by `FlockCredentialProvider`; list linked accounts). Linking routes return the model at the **root**, never cache, never queue offline, and raise `FlockEvents.OnAccountLinked`/`OnAccountUnlinked`. A linked email opens the `ResetPasswordAsync` gate for the session (`_hasEmailCredential`, re-derived from every accounts payload, cleared on logout, never persisted).
- **PlayerProvider** — player templates + player data (incl. by-name).
- **FlockConfigProvider** — game configs & patches (incl. by-name).
- **FlockGameProvider** — game + game-version lookups (incl. by-name).
- **FlockShopProvider** — shops, items, purchase, consume, inventory (incl. by-name); `PurchaseStatus`/`TransactionType` enums. `PurchaseAsync` returns `PurchaseResult` (inventory + granted rewards + wallet); `ConsumeAsync` grants an owned item's rewards and is money-safe like a purchase.
- **FlockLeaderboardProvider** — read-only standings / my-rank / around-me, addressed by board name (resolve memoized per session); no submit path by design.
- **FlockCommandProvider** — retry-safe game commands (funds, achievements, player-data writes).
- **FlockAssetProvider** / **FlockAssetCache** — asset fetch + local file cache.
- **FlockNotificationProvider** — player inbox (list / unread count / summary / mark read), server-side scheduling of a dashboard template *by name*, the read-only template catalog behind that resolution, and push device-token register/unregister; `GetScheduledAsync` reads the player's schedules from the server (spanning installs and devices) and `CancelAllScheduledAsync` works off that list, falling back to the local pending-schedule list — kept as an offline-only fallback — when the server read fails. The two template reads are API-key scoped, not bearer scoped — they work signed out and cache per game, unlike every other read here; the name→ID memo is what keeps scheduling to one lookup per session. Raises `FlockEvents.OnUnreadCountChanged` and `OnNotificationReceived`; the latter is fetch-derived (no realtime channel, no poller) off a player-scoped `created_at` watermark that seeds silently on the first fetch and survives `ClearCache()` alongside the pending-schedule list. Raises `FlockEvents.OnUnreadCountChanged`; no background polling. Device-platform auto-detect throws on desktop/console/Editor rather than guessing a value the push backend doesn't accept. `RegisterThisDeviceAsync` fetches the APNs token itself on iOS when `com.unity.mobile.notifications` is installed — an **optional** dependency wired through `versionDefines` in `Flock.Runtime.asmdef`, so the package stays single-dependency; Android tokens come from Firebase and remain the consumer's job.
- **FlockSnapshotStore** — on-disk snapshot cache backing offline reads.
- **Analytics/FlockAnalyticsProvider** — sends sessions/events/transactions. · **NullAnalyticsProvider** — no-op when `FLOCK_NO_ANALYTICS`.

## Runtime/Http
- **FlockHttpClient** — static GET/POST/… facade; maps status→exception in one place for every call, parses the coded `detail` (object *or* FastAPI's field-error array) into `Code`/`ServerMessage`, attaches the matching `Hint`. `…Async<T>` reads the body and fails on an empty one; the overloads without a type argument are for routes with nothing to read (a 2xx with no body or a JSON body is a success, a 204 included; a body that is not JSON, such as a captive portal's page, still fails) and carry the six no-schema analytics/log/session-end calls.
- **FlockEndpoints** — every relative API path the SDK calls (consts + parameterized builders); no raw path literals at call sites.
- **FlockProviderBase** — base class for providers; shared fetch + snapshot + validate helpers. `ExecuteAsync<T>` runs a call through retry + token refresh; `ExecuteWithoutResultAsync` does the same for a call that returns nothing.
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
- **Leaderboard** / **Standings** / **StandingEntry** / **PlayerRank** + the `FlockLeaderboard*` enums and the `FlockLeaderboardWindow` key struct — board config (with `IsHigherBetter` / `FormatScore`) and its read shapes.

## Runtime/ (support)
- **Config/FlockConfigAsset** — the `FlockConfig.asset` ScriptableObject (api key, version, baked id). · **Config/FlockInitConfig** — runtime init params.
- **Exceptions/** — **FlockException** base (`Body`, `StatusCode`, `Code`/`ErrorCode`, `ServerMessage`, `Hint`, `Operation`; `Message` is composed from them) + **Network/Auth/Validation/Serialization** subclasses by failure kind; **FlockErrorCode** enum = typed view of the backend `detail.code` contract (+ `FlockErrorCodes.Parse`); **FlockErrorHints** = code→next-step table behind `Hint` (`ForAuth` disambiguates by credential); every code has a hint except `Unknown`, locked by a test. The enum is diffed against `Tooling~/WireErrorCodes.txt` (maintainer-only snapshot of the wire contract) by the `error-codes` job in `.github/workflows/consistency.yml`.
- **Interfaces/** — provider contracts (`IFlockClient`, `IConfigProvider`, `IPlayerService`, `IAssetProvider`, `IAnalyticProvider`) + `SchemaTag`. Schema/template/config raw getters are `internal` — reachable only through the generated accessors (codegen-only by design).
- **Logging/** — **IFlockLogger** + **UnityFlockLogger** / **NullFlockLogger**.
- **Constants/FlockConstant** — shared constants. · **Docs/FlockSdkGuide** — in-editor Getting-Started text.

## Editor/
- **QwacksEditorWindow** — main editor window (**Flock > Settings**); the config asset is the source of truth.
- **FlockConfigLocator** — single source for "which FlockConfig asset".
- **FlockVersionResolver** — bakes Game-Version name→id at edit time so runtime init needs no network.
- **FlockPlayModeGuard** / **FlockBuildGuard** — block Play / build when the SDK is unset or schemas drifted.
- **FlockModelPreservation** — `IUnityLinkerProcessor`: on every player build writes `Library/Flock/link.xml` keeping `Flock.Runtime` whole and each `Flock.Generated.*` namespace in whichever player assembly holds it, so IL2CPP Medium/High stripping cannot remove what Newtonsoft reaches by reflection. A `link.xml` inside a UPM package is not read by the linker (measured); a runtime-only `.unitypackage` (Package Builder, Editor unticked) ships a static one instead.
- **FlockCodeGenValidator** — warns when the baked version id drifts from generated schemas; `GetGeneratedGameVersionId()` returning null is the "codegen never ran" signal.
- **FlockCodegenCompileHint** / **FlockCodegenHintClassifier** — watch compilation and point at Codegen > Sync when an unresolved member looks like a generated accessor (classifier is pure/testable). Recompiles only — a cold start compiles before `[InitializeOnLoad]`; the Codegen tab's Status card covers that.
- **FlockSetupChecklist** / **FlockSetupClassifier** (+ `FlockSetupItem`/`FlockSetupState`/`FlockSetupFacts`/verdict enums) — pure, testable setup-readiness logic.
- **FlockFirstRunBootstrap** — opens the window on first import. · **FlockSdkGuideEditor** — inspector for the guide.
- **FlockProviderManifest** — maps providers ↔ `FLOCK_NO_*` defines for event-subset builds.
- **FlockPackageBuilder** — assembles the distributable package.
- **FlockPlaytestInstaller** — the Playtesting tab's install, update and remove for the Protokite Playtest package: downloads `ProtokitePlaytest-<version>.unitypackage` from the GitHub release matching `FlockSdkVersion.Current` (a blocking, cancellable download, so a script reload cannot drop it) and imports it; an update deletes the old `Assets/` copy only once the new one has downloaded, so a dropped file cannot linger; every download result but success counts as a failure (a failed disk write answers 200). Finds an installed copy from its assembly definition, wherever it is. Reads the version through `InternalsVisibleTo("Flock.Editor")`.
- **FlockPlaytestPackageBuilder** — maintainer tooling (**Qwacks Dev > Build Protokite Playtest Package**, or `-executeMethod ...BuildFromCommandLine -playtestOut <folder>`): stages the playtest under `Assets/ProtokitePlaytest/` with GUIDs made from their paths and exports it. Excluded from core's own `.unitypackage`.

## Editor/Codegen/
Writes typed accessors to `Assets/Flock/Generated/` (Flock-owned, wiped each sync).
- **FlockCodegenMenu** / **FlockCodegenCli** — menu + headless CI entry points.
- **SchemaFetcher** / **SchemaHasher** / **FlockSchemaSnapshot** — pull schemas + content-hash for drift detection.
- **TypeMap** — backend→C# type mapping. · **CodeGenNamingHelpers** — safe identifier names.
- **\*Emitter** (GameConfig, ConfigAccessor, PlayerAccessor, PlayerTemplate, SchemaProperty, Command, Shop) — generate the typed C#.
- **ManifestEmitter** — emits `SchemasManifest` (GameVersionId + hash). · `EmitResult`/`CodegenResult` — codegen DTOs.

## PackageBuilder/Tests/Editor/
EditMode tests (run via Unity Test Runner only): **CodeGenNamingHelpersTests**, **FlockBuildGuardTests**, **RetryHandlerTests**, **SchemaHasherTests**, **TypeMapTests**, **FlockErrorPipelineTests** (exception/`FlockErrorCode` mapping; has an `[Explicit]` live-backend test), **FlockErrorMessageTests** (composed `Message`, hints, FastAPI field errors), **FlockErrorHintCoverageTests** (every `FlockErrorCode` has a hint or is explicitly allowlisted), **FlockCodegenHintTests** (compile-error classification over real Roslyn text), **FlockConfigResolutionTests** (patch-else-config resolution), **FlockEmptySuccessTests** (a 2xx with no body on a route with nothing to read), **FlockModelPreservationTests** (the build's link.xml), **FlockPlaytestInstallerTests** (release URL, version match, which downloads are imported).

## ProtokitePlaytest~/ — the Protokite Playtest package

A second package, `com.protokite.playtest`, in a `~` folder so Unity never imports it as part of core; it ships from the same
tag at core's version. **It declares no package dependency on `com.flock.sdk`** (a studio that imported Flock from the
`.unitypackage` has no such package) and reaches Flock through the `Flock.Runtime` assembly. Core's runtime never names it
(`Tooling~/check-playtest-package.sh`, run by `consistency.yml` and `release.yml`, also checks the version match and a `.meta`
beside every file). Development reaches it through a junction, `Qwacks/Libraries/Unity/packages/com.protokite.playtest`:
**Unity cannot link a script to its class when the path contains a `~`**, so a ScriptableObject created there is saved with no
script.
- **ProtokitePlaytestSettings** — the `ScriptableObject` at `Assets/Resources/ProtokitePlaytestSettings.asset`: playtesting off,
  Protokite API URL `https://api-protokite.qwacks.com` (production) by default.
- **ProtokitePlaytest** — the entry point. `Status` is worked out on every read from the settings, the running `FlockClient`
  and the config fetched **under that client** (a config fetched under a client that has since shut down reads as none,
  before any refresh). `Refresh()` follows Flock by comparing the running client instance (Flock clears event subscriptions
  on shutdown, so a restart is invisible to events), returns at once while neither the client nor the config state has changed, fetches once per client, forgets and cancels on a change (a counter
  ignores late answers, the cancel stops retries), re-subscribes to `FlockEvents.OnSessionStarted` per client and fetches
  again there only after `PlaytestConfigUnavailable`. Failures are judged by HTTP status alone: core counts a 403 as an
  auth failure, but Protokite never sends one. Each status change is logged once.
- **ProtokitePlaytest (session half, `ProtokitePlaytestSession.cs`)** — one Protokite session per launch, started from
  `Refresh()` once the config is loaded and `FlockClient.ServerSessionId` is set (read each frame, never from an event),
  never retried (a start may have created one), never started twice in a launch whatever Flock does. The start and the end
  run on the thread pool so `HandleGameQuitting()` (on `Application.quitting`) can wait for them without the main thread,
  bounded at 3 s, with a cancel that stops the end's retries once it gives up. URL, headers and retry settings are copied at
  the start, so the end goes out after Flock has shut down. A start answered after its launch ended (quit, or a new Play
  with domain reload off) is ended at once, unless quitting already ended it. A 400 is a closed playtest:
  `PlaytestNoLongerCollecting` for the launch.
- **ProtokitePlaytestIdentity** — the device id file (a lower-case GUID under `persistentDataPath/ProtokitePlaytest/`),
  written through a temporary file of its own and moved into place, read back after, never replaced when unreadable; stray
  temporary files over a minute old are swept. `SetSteamId` refuses an id with whitespace rather than trim it. Tests point
  it elsewhere with `DeviceIdFilePathForTesting`, and a fixture checks the game's own file is untouched.
- **ProtokitePlaytestDriver** — a hidden `DontDestroyOnLoad` object started `BeforeSceneLoad` only when playtesting is on; calls
  `Refresh()` every frame and `Stop()` when destroyed.
- **ProtokiteClient** (internal) — `GET /game/sdk/playtest-config` through core's `FlockHttpClient` and a `RetryHandler` built
  from `FlockClient.RetryPolicy`; headers from `FlockClient.GetGameHeaders()` (key + version, never the bearer). The answer is
  enveloped; **`ProtokitePlaytestConfig` is read by hand from `JObject`** so IL2CPP stripping has no model of ours to strip.
  A missing or non-boolean feature is off; a null form or a form without an id is none; a question without an id is dropped.
- **ProtokitePlaytestSettingsMenu** — **Protokite > Playtest > Settings**; creates the asset, and refuses to save one Unity
  cannot link to its script.
- Tests: **ProtokitePlaytestStatusTests**, **ProtokitePlaytestSessionTests** (held starts and ends for the quit and late-answer
  cases), **ProtokitePlaytestConfigTests** (EditMode, fake transport; a held-answer adapter
  for late replies), **ProtokitePlaytestDriverTests** (PlayMode, the real driver). A live `[Explicit]` check lives in
  FlockUnityProject: `ProtokitePlaytestLiveConfigTests`.

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

- **Retry-safe session registration** — every `StartSession` creates a new server-side session row, and only the server knows its id. If the app quits while that request is in flight, the client loses the id and re-registers on next launch — orphaning the first row, which stays open forever (the SDK warns when it detects this). Fix: let the client supply a `client_session_id` so a retried registration returns the existing row instead of creating a duplicate.
- **Idempotency keys for money mutations** — `AddGameFunds` and shop `Purchase` mutate server state (currency, inventory) and carry no idempotency key, so the backend can't tell a genuine repeat from a network retry. To avoid double-crediting/double-charging, the SDK only auto-retries these two on failures the server **provably didn't process** (HTTP 408/429); ambiguous failures (client timeout, dropped connection, 5xx) surface to the caller to catch and decide. The robust fix is a client-supplied `idempotency_key` the backend dedupes on, after which full auto-retry can return. The idempotent commands — `UpdatePlayerData`, `UpdatePlayerDataField`, `UnlockAchievement` — are unaffected.
