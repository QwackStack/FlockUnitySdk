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
- **FlockBehaviour** — internal hidden MonoBehaviour; main-thread dispatch, the frame tick and app pause/quit/disable hooks (exception capture is `FlockExceptionCapture`; the analytics provider takes what it kept on the tick, at quit and again on disable, which at quit follows every script's `OnApplicationQuit`).
- **FlockEvents** — static hub for lifecycle events (authenticated, session ended, restored).
- **FlockEventModels** — event enums/payloads: `FlockAuthMethod`, `FlockAuthInfo`, `FlockSessionEndReason`, `FlockSessionEndedArgs`.
- **FlockSdkVersion** — SDK version string. · **FlockUtil** — on-disk token/file paths.
- **FlockTemporaryFiles** — saves a file through a temporary file of its own per write, moved over in one step; deletes only temporary files over a minute old (a fresh one may be another copy of the game writing). Every store that saves through a temporary file uses it.
- **FlockSavedFiles** — every change the SDK makes to its saved files (write, move, replace, delete, folders, lock files); after each, a WebGL player is asked to copy the files to browser storage (`Plugins/WebGL/FlockSavedFiles.jslib`, through Unity's own copy queue), since it keeps them in memory otherwise. A test fails any direct `File`/`Directory` change in `Runtime/` outside it.
- **FlockWaiting** — how the SDK waits: `ResumeOnCallersThread` for every `ConfigureAwait` (true on WebGL, which has no thread pool) and `DelayAsync` (a frame-by-frame wait on WebGL, which has no timers). A test fails any `ConfigureAwait(false)`, `Task.Delay`, `Task.Run` or `ThreadPool` in `Runtime/` outside it.
- **FlockLaunchFolder** — a folder one launch keeps its files in, held by `in-use.lock` opened with `FileShare.None`; another launch touches it only once it can open that lock itself (`ClaimEnded`), which the OS allows however the owner ended.

## Runtime/Providers
- **FlockAuthProvider** — login / register / token refresh + revoke / session restore / password reset / email verification / name preflight / **account linking** (link email, device and the five OAuth providers; unlink by `FlockCredentialProvider`; list linked accounts). Linking routes return the model at the **root**, never cache, never queue offline, and raise `FlockEvents.OnAccountLinked`/`OnAccountUnlinked`. A linked email opens the `ResetPasswordAsync` gate for the session (`_hasEmailCredential`, re-derived from every accounts payload, cleared on logout, never persisted).
- **PlayerProvider** — player templates + player data (incl. by-name).
- **FlockConfigProvider** — game configs & patches (incl. by-name).
- **FlockGameProvider** — game + game-version lookups (incl. by-name).
- **FlockShopProvider** — shops, items, purchase, consume, inventory (incl. by-name); `PurchaseStatus`/`TransactionType` enums. `PurchaseAsync` returns `PurchaseResult` (inventory + granted rewards + wallet); `ConsumeAsync` grants an owned item's rewards and is money-safe like a purchase.
- **FlockLeaderboardProvider** — read-only standings / my-rank / around-me, addressed by board name (resolve memoized per session); no submit path by design.
- **FlockCommandProvider** — retry-safe game commands (funds, achievements, player-data writes). A queueable write goes straight to the server only when no older write of the same player is queued; otherwise it queues behind them and starts a flush, so it can never be overtaken by their replay. A flush stops, touching no queue, when the signed-in player changes or the queue is reloaded under it.
- **FlockAssetProvider** / **FlockAssetCache** — asset fetch + local file cache. A download link storage refuses (401/403: signed for minutes, while records are kept all session and between launches) fetches that record again, keeps it, and tries once more.
- **FlockNotificationProvider** — player inbox (list / unread count / summary / mark read), server-side scheduling of a dashboard template *by name*, the read-only template catalog behind that resolution, and push device-token register/unregister; `GetScheduledAsync` reads the player's schedules from the server (spanning installs and devices) and `CancelAllScheduledAsync` works off that list, falling back to the local pending-schedule list — kept as an offline-only fallback — when the server read fails. The two template reads are API-key scoped, not bearer scoped — they work signed out and cache per game, unlike every other read here; the name→ID memo is what keeps scheduling to one lookup per session. Raises `FlockEvents.OnUnreadCountChanged` and `OnNotificationReceived`; the latter is fetch-derived (no realtime channel, no poller) off a player-scoped `created_at` watermark that seeds silently on the first fetch and survives `ClearCache()` alongside the pending-schedule list. Raises `FlockEvents.OnUnreadCountChanged`; no background polling. Device-platform auto-detect throws on desktop/console/Editor rather than guessing a value the push backend doesn't accept. `RegisterThisDeviceAsync` fetches the APNs token itself on iOS when `com.unity.mobile.notifications` is installed — an **optional** dependency wired through `versionDefines` in `Flock.Runtime.asmdef`, so the package stays single-dependency; Android tokens come from Firebase and remain the consumer's job.
- **FlockSnapshotStore** — on-disk snapshot cache backing offline reads.
- **Analytics/FlockAnalyticsProvider** — sends sessions, gameplay events (`TrackEvent`: refused past 200/100 characters, for an empty or reserved name, or without consent; queued on disk and sent only while a player is signed in, a pre-sign-in event credited to the next sign-in; sent once at once when the queue is off), diagnostics (`LogDiagnostic...`; the former `LogEvent`/`LogError`/`LogException` are `[Obsolete]` forwarders), the game's captured exceptions (taken on its frame tick, through the repeat rule, queued like diagnostics) and transactions. It records no gameplay event of its own: the heartbeat is local, and the crash report is a diagnostic. · **NullAnalyticsProvider** — no-op when analytics is switched off in settings (`Enabled`).

## Runtime/Http
- **FlockHttpClient** — static GET/POST/… facade; maps status→exception in one place for every call, parses the coded `detail` (object *or* FastAPI's field-error array) into `Code`/`ServerMessage`, attaches the matching `Hint`. `…Async<T>` reads the body and fails on an empty one; the overloads without a type argument are for routes with nothing to read (a 2xx with no body or a JSON body is a success, a 204 included; a body that is not JSON, such as a captive portal's page, still fails) and carry the six no-schema analytics/log/session-end calls.
- **FlockEndpoints** — every relative API path the SDK calls (consts + parameterized builders); no raw path literals at call sites.
- **FlockProviderBase** — base class for providers; shared fetch + snapshot + validate helpers. `ExecuteAsync<T>` runs a call through retry + token refresh, refreshing and retrying a 401 only while the sign-in it went out under (`FlockClient.SignInNumber`: moved by every login, restore and sign-out, never by a refresh) is still the current one; `ExecuteWithoutResultAsync` does the same for a call that returns nothing.
- **IFlockHttpAdapter** — per-platform transport seam; `FlockHttpRequest`/`FlockHttpResponse`/`FlockHttpResult` normalize it.
- **SystemNetHttpAdapter** (non-WebGL) / **UnityWebRequestHttpAdapter** (WebGL) — transport impls.
- **IFlockFileUploader** / **UnityWebRequestFileUploader** — the file upload (C-4, public): `FlockHttpClient.UploadFileAsync`
  PUTs a file from disk with `UploadHandlerFile`, only the Content-Type it is given, no whole-upload timeout but a 60 s stall
  timeout read off `uploadedBytes` on a `Stopwatch`, and answers a `FlockFileUploadOutcome` (`IsUploaded` = a 2xx); what
  Unity refuses by throwing (an address it cannot parse, a file held open to another program) comes back as an outcome too. Kept apart from
  `IFlockHttpAdapter` (public, a studio may implement it) so no adapter changes; `UseFileUploader` swaps it for tests. WebGL
  sends nothing. Main thread only (UnityWebRequest's rule).
- **RetryPolicy** / **RetryHandler** — transient-failure backoff honoring `Retry-After`; waits through `FlockWaiting.DelayAsync`.

## Runtime/Auth
- **JwtTokenParser** / **JwtTokenClaims** — decode token + read claims (expiry, player id).
- **TokenStoreFactory** — picks the secure store per platform at compile time.
- **TokenStore/** — **ITokenStore** + `StoredTokens`, with **Android/Ios/Mac/Windows/WebGl/Other** secure-storage impls.

## Runtime/Analytics
- **FlockSession** — tracks the current play session (start/end/ids); its live-session record is `session_state.json` in the launch's folder.
- **FlockAnalyticsLaunches** — each launch's analytics files in `persistentDataPath/Flock/analytics/launches/<time>-<hex>/` (crash marker, live-session record, the three queues), locked while it runs. At start it takes over every ended launch (queues moved in at once; records handed to the provider, which reports each once and deletes the folder) and, once, what a build before 1.48.0 left (the queues under `Flock/` and two PlayerPrefs records). Owned by `FlockClient`, let go at `Shutdown` and at the no-domain-reload static reset. Tests point every SDK elsewhere through `FolderForTesting`, set by each test assembly's SetUpFixture.
- **FlockSessionSnapshot** — persisted session state for quit/crash recovery.
- **FlockTerminationTracker** — dirty-exit detection: tombstone marker `termination_marker.json` in the launch's folder, kept from the provider's start (with consent) to a clean quit, and naming the session only while one runs; beats on the provider's frame tick, not the session's heartbeat. Read by whichever later launch takes that folder over; lifecycle-only classifier, reported as an `app_termination` diagnostic (type debug) through the log-event queue. · **FlockTerminationMarker** — the persisted tombstone model.
- **FlockExceptionCapture** — hears the game's exceptions on every thread from the analytics provider's start: `Application.logMessageReceivedThreaded` (exceptions only; the SDK's own `UnityFlockLogger.LogException` is skipped by a per-thread flag), `TaskScheduler.UnobservedTaskException`, and `AppDomain.UnhandledException` only while Unity's logging is off (otherwise the log hook already has it). Keeps at most 256 until the provider's frame tick takes them, counting the rest as lost, each message cut to 4,096 characters and each stack to 8,192; an empty logged stack is filled with the frames that logged it. The game's exception objects are read only through `FlockExceptionText`. · **FlockRepeatedExceptionCounter** — the repeat rule on the main thread: a same-fault key (hook, message with numbers and `0x` values collapsed, the first two frames below Unity's logging), a window per fault from its first report (repeats counted, a summary with `repeat_count` when it closes), and the launch's limit of 100 different faults with a held-back count.
- **FlockEventCache** / **IEventCache** — queues events for batch + offline send. One flush sends at a time: a flush trigger that finds one running leaves the queue to it; an awaited `FlushAsync` waits for it, then sends what is left.
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
- **Logging/** — **IFlockLogger** + **UnityFlockLogger** / **NullFlockLogger**; **FlockExceptionText** reads a game's exception without trusting its getters (a `Message`, `StackTrace` or `ToString` that throws gets a fallback), opens aggregates without `Flatten` (which reads `Message`), and cuts long text. Used by the capture hooks, `LogDiagnosticException(Exception)` and `FlockEvents`' subscriber log line.
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
- **FlockPackageBuilder** — assembles the distributable package (`ShowWindow`). No menu item in the SDK: a git install ships
  this file, so the maintainers' project (FlockUnityProject's `Assets/FlockTestRun/QwacksDevMenus.cs`) adds **Qwacks Dev >
  Package Builder**, and `FlockMaintainerToolingTests` fails if the SDK itself names a Qwacks Dev menu.
- **FlockPlaytestInstaller** — the Playtesting tab's install, update and remove for the Protokite Playtest package: downloads `ProtokitePlaytest-<version>.unitypackage` from the GitHub release matching `FlockSdkVersion.Current` (a blocking, cancellable download, so a script reload cannot drop it) and imports it; an update deletes the old `Assets/` copy only once the new one has downloaded, so a dropped file cannot linger; every download result but success counts as a failure (a failed disk write answers 200). Finds an installed copy from its assembly definition, wherever it is. Reads the version through `InternalsVisibleTo("Flock.Editor")`. Refuses to install into a Flock SDK exported without Analytics (`WhyPlaytestCannotBeInstalled`, whose refusal compiles only under `FLOCK_NO_ANALYTICS`: the playtest calls `FlockClient.Analytics`, and the define lives in Flock's own `csc.rsp`, where the playtest cannot see it). Names the two playtest menu items the tab
  opens (`SettingsMenuPath`, `SetupWindowMenuPath`); the playtest's own tests read both and check its menu has them.
- **FlockPlaytestPackageBuilder** — maintainer tooling (`BuildFromMenu`, called by the maintainers' project from **Qwacks Dev > Build Protokite Playtest Package** through `InternalsVisibleTo("FlockTestRun.Editor")`, or `-executeMethod ...BuildFromCommandLine -playtestOut <folder>`): stages the playtest's `Runtime`, `Editor` and `Samples` under `Assets/ProtokitePlaytest/` with GUIDs made from their paths and exports it. Excluded from core's own `.unitypackage`.

## Editor/Codegen/
Writes typed accessors to `Assets/Flock/Generated/`. Each sync replaces the files it generated and nothing else: `GeneratedFiles` is the one owner of what codegen may delete (a `.g.cs` with codegen's header, its `.meta`, and a folder that leaves empty).
- **FlockCodegenMenu** / **FlockCodegenCli** — menu + headless CI entry points.
- **SchemaFetcher** / **SchemaHasher** / **FlockSchemaSnapshot** — pull schemas + content-hash for drift detection.
- **TypeMap** — backend→C# type mapping. · **CodeGenNamingHelpers** — safe identifier names.
- **\*Emitter** (GameConfig, ConfigAccessor, PlayerAccessor, PlayerTemplate, SchemaProperty, Command, Shop) — generate the typed C#.
- **ManifestEmitter** — emits `SchemasManifest` (GameVersionId + hash). · `EmitResult`/`CodegenResult` — codegen DTOs.

## PackageBuilder/Tests/Editor/
EditMode tests (run via Unity Test Runner only): **CodeGenNamingHelpersTests**, **FlockBuildGuardTests**, **RetryHandlerTests**, **SchemaHasherTests**, **TypeMapTests**, **FlockErrorPipelineTests** (exception/`FlockErrorCode` mapping; has an `[Explicit]` live-backend test), **FlockErrorMessageTests** (composed `Message`, hints, FastAPI field errors), **FlockErrorHintCoverageTests** (every `FlockErrorCode` has a hint or is explicitly allowlisted), **FlockCodegenHintTests** (compile-error classification over real Roslyn text), **FlockConfigResolutionTests** (patch-else-config resolution), **FlockEmptySuccessTests** (a 2xx with no body on a route with nothing to read), **FlockModelPreservationTests** (the build's link.xml), **FlockPlaytestInstallerTests** (release URL, version match, which downloads are imported, a Flock SDK with Analytics takes it).

## ProtokitePlaytest~/ — the Protokite Playtest package

A second package, `com.protokite.playtest`, in a `~` folder so Unity never imports it as part of core; it ships from the same
tag at core's version. **It declares no package dependency on `com.flock.sdk`** (a studio that imported Flock from the
`.unitypackage` has no such package) and reaches Flock through the `Flock.Runtime` assembly. Core's runtime never names it
(`Tooling~/check-playtest-package.sh`, run by `consistency.yml` and `release.yml`, also checks the version match, a `.meta`
beside every file outside `~` folders, the version constant sessions report, and the video DLL with its licences and its
`.meta`'s platforms). Development reaches it through a junction, `Qwacks/Libraries/Unity/packages/com.protokite.playtest`:
**Unity cannot link a script to its class when the path contains a `~`**, so a ScriptableObject created there is saved with no
script.
- **ProtokitePlaytestSettings** — the `ScriptableObject` at `Assets/Resources/ProtokitePlaytestSettings.asset`: playtesting off,
  Protokite API URL `https://api-protokite.qwacks.com` (production) and Ask The Player For Playtest Consent on by default.
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
- **Player consent (`ProtokitePlaytestConsent.cs`, `ProtokitePlaytestConsentQuestion.cs`)** — the playtest's own question:
  `ProtokitePlaytestConsentChoice` and the inert rules (what each answer allows, the wire spellings read letter for letter, a
  feature this build does not know allowed only by the answer that allows everything); `ProtokitePlaytestConsentFile`
  (`persistentDataPath/ProtokitePlaytest/playtest_consent.json`, a temporary file per save, a failed save forgets the answer
  before). `EffectiveConsent()` is the one answer every part reads: the saved one, or with none `NotAnswered` in a build that
  asks and `VideoAndPlayData` in one that does not; `StatusFor` takes it (`WaitingForPlayerConsent`, `PlayerRefusedPlaytest`),
  `FeatureIsOnInTheLoadedConfig` gates video and heavy analytics on it, and the session start sends it in `extra_debug`.
  Taking the screen back forgets the run's saved session at once and deletes the recording (now, or once written, before the
  session check in `UploadThisLaunchsRecordingWhenReady`); `SaveSessionBesideRecording` never gives one a session. Earlier
  launches' uploads wait for an answer of nothing, and while nobody has answered in a build whose config is on its way or
  loaded. Tests point the file elsewhere with `ConsentFilePathForTesting` (the settings fixtures set it, asking off unless a
  test turns it on).
- **Panels (`Runtime/Panels/`)** — **ProtokitePlaytestPanel**: a full-screen UI Toolkit panel built from code (a `UIDocument`
  and a runtime `PanelSettings` with an empty theme, the built-in font given at the root, sorting order 30000), one view at a
  time so a second question can follow, the view itself focused so no button is; it keeps the cursor free while open, notes a
  game that locks it again, gives the game's cursor back when closed (not an ended launch's), and is drawn only while playing,
  not in batch mode, with graphics. **ProtokitePlaytestConsentQuestionView**: the words and the four buttons from one options
  table the tests read too; a press in the first half second is ignored, and a mouse press while the game keeps the cursor
  locked. **Forget This Machine's Answer** in the setup window (editor) forgets the saved answer. **ProtokitePlaytestFormView**: the
  feedback form built from the published form (text, many-line text, a 1-5 rating and options as buttons, a checkbox recorded
  unticked when drawn, unknown kinds as text), text boxes styled in code (with no theme their input box has no size), Escape
  stopped before a text field puts back its old text, select-all on click off, problems shown once Send is tried; Send, Close
  and **Upload your recording** have the consent question's two press guards. Buttons' actions are kept by name
  (`PressForTesting`) for tests of a view in no panel. **Open Feedback Form** in the setup window (editor, Play Mode).
- **Feedback form (`ProtokitePlaytestFormAnswers.cs`, `ProtokitePlaytestFormSubmission.cs`, `ProtokitePlaytestForms.cs`)** —
  `ProtokitePlaytestFormAnswers` is the inert half: Protokite's own validator's rules (a required checkbox answered unticked,
  empty optional answers left out, unknown kinds as text, select trimmed and ordinal, rating 1-5), every problem in the
  studio's order, and the answers as Protokite keeps them (`ToWire`). `ProtokitePlaytestFormSubmission` is one form as kept
  (session, Steam or device id, URL, Game Version ID, answers; no API key; read back with date parsing off) and its body (no
  empty `session_id`). `ProtokitePlaytestKeptForms` keeps one file per form under `persistentDataPath/ProtokitePlaytest/FeedbackForms`
  (a temporary file per write; oldest first; `Claim` with `FileShare.Delete` so another launch skips one being sent, and
  `ForgetClaimed` deletes it while held). `ProtokitePlaytestForms` is the public surface (`FeedbackForm`, open/close,
  `SendFeedbackForm`, `CanSendTheRecording`, `StopRecordingAndSendIt`) and the one sender, `SendWaitingFormsWhenDue`, run each
  frame before `Refresh`'s early return: at a new Flock client, once a form is kept, and after a failure that may pass when the
  network comes back (read only then) or `FormRetryInterval` later. A readable `result.id` deletes the file; 422 and 404
  delete it naming the question; 401 keeps the forms for a later launch; a failure Protokite gave for this form goes on to the
  next, one that says nothing about the form (no answer, a portal page, 403, 408, 429, 502-504) stops the rest. Sent with the
  launch's key and the form's own version and URL (`HeadersForTheSession`). `ProtokitePlaytestFormKeyWatcher` hears the key
  through `Input.GetKeyDown` where the Input Manager is on and IMGUI otherwise, alive only while a form can open. Tests point
  the folder elsewhere with `FeedbackFormsFolderForTesting` (both test assemblies' run-wide fixtures set it), with
  `ReachabilityForTesting` and `ClockForTesting` for the retry.
- **ProtokitePlaytestIdentity** — the device id file (a lower-case GUID under `persistentDataPath/ProtokitePlaytest/`),
  written through a temporary file of its own and moved into place, read back after, never replaced when unreadable; stray
  temporary files over a minute old are swept. `SetSteamId` refuses an id with whitespace rather than trim it. Tests point
  it elsewhere with `DeviceIdFilePathForTesting`, and a fixture checks the game's own file is untouched.
- **ProtokitePlaytestSavedFiles** — every change the playtest makes to its saved files (the consent answer, the device id,
  the kept forms, their temporary files and sweeps); after each, even one that fails, a WebGL player is asked to copy the
  files to browser storage through the Flock SDK's own call (`FlockCopySavedFilesToBrowserStorage`, core's
  `Plugins/WebGL/FlockSavedFiles.jslib`), so the page keeps one queue of copies. `CopyToBrowserStorageForTesting` sees each
  request. A test fails any direct change outside it in `Runtime/` except `Video/` (never reached in a WebGL player); another
  fails when the call it imports is not one core's library defines.
- **Video encoder (`Runtime/Video/`)** — `IProtokitePlaytestVideoEncoder` (configure, encode an I420 frame, finish) is
  the one seam recording goes through; `ProtokitePlaytestLibVpx` is its only implementation and the only file that names
  libvpx (a test scans for it). It calls `Runtime/Plugins/x86_64/protokite_vpx.dll`, a flat C wrapper over a static
  libvpx 1.17.0 (VP8 + VP9, static CRT, KERNEL32 only), with timestamps in milliseconds. The DLL's own wrapper version is
  checked before any other call; a missing, 32-bit or stale DLL means no video, logged once (Warning on Windows, Log
  elsewhere). The DllImports are fenced to Windows, so every other platform compiles none. The C wrapper owns the checks
  that guard native memory (frame length, codec, speed range). Settings default to D-Y9, with VP9 getting its own speed.
- **Recording file (`Runtime/Video/`)** — `IProtokitePlaytestRecordingFile` (open, write an encoded frame, close, finish a
  file a dead run left) is the seam a recording's frames go through, and names its own content type for the upload;
  `ProtokitePlaytestWebmFile` is its only implementation and the only file that names WebM (a test scans for it). VP8 or
  VP9, one cluster per frame, every size written before its bytes, each frame handed to the operating system as it is
  written; only the length and the duration are stamped on close, so a file cut off anywhere plays up to the cut. Every
  offset is read off the file itself, never a constant, so the name written into the file can change. Finishing a cut-off
  file checks each frame's first bytes against its codec (read from the file's own track), and the writer refuses any
  frame that check would stop at; after a failed write it takes no more frames, and closing cuts the torn one off
  (so does finishing the file after a crash). Written unbuffered, so a write the disk refuses fails where it happens.
  **ProtokitePlaytestFrameSchedule** decides which game frames are captured and when each is shown (the frame nearest each
  capture time, background time left out, times always rising, a length limit).
- **Video capture (`Runtime/Video/`, `Runtime/ProtokitePlaytestVideo.cs`)** — `UpdateVideo` runs at the end of every
  frame from the driver's coroutine (a batchmode editor never gets there, so its tests run in a windowed editor). It starts
  the launch's one recording when the loaded config turns video on, before sign-in; a Flock restart (config fetched again)
  does not stop it, a loaded config with video off or a closed playtest does. **ProtokitePlaytestVideoRecording** owns the
  schedule, an encoding thread (below the game's priority by default) and a writing thread, each fed by a bounded queue
  (8 and 300); frames are dropped before encoding and counted, the size limit is checked where a frame is handed to be
  written, the file is written as `.part` and renamed when finished, and a failure keeps every whole frame.
  **ProtokitePlaytestScreenFrameSource** is the one GPU class: `ScreenCapture.CaptureScreenshotIntoRenderTexture`, a blit to
  the video size, the `ProtokitePlaytestRgbaToI420` compute shader (in the package's `Resources`), and
  `AsyncGPUReadback` with at most 3 frames on their way, into a pool of blocks (`ProtokitePlaytestFrameBlocks`). Rows flip
  only where textures start at the bottom (OpenGL); Linear projects convert back to sRGB before conversion. The capture
  is asked for the encoder's pixel layout (the Android seam); `ProtokitePlaytestVideoEncoders` and
  `ProtokitePlaytestRecordingFiles` are the only places that pick libvpx and WebM. **ProtokitePlaytestVideoSettings**
  reads the settings asset's video values in range and fits the video to the screen, each side a multiple of 16. Quitting
  stops the capture first and waits for the file within the session end's 3 seconds.
- **Recording files on disk (`Runtime/Video/ProtokitePlaytestRecordingsFolder.cs`)** — self-contained (it does not use core's
  launch folders). **ProtokitePlaytestRecordingRun** is one recording's folder, `Recordings/Playtest/` or
  `Recordings/TestVideos/` + `<UTC time>-<8 hex>`: the video, `session.json` (session id, API URL, the session's Game
  Version ID; never the API key), `reserved-bytes.txt` and `in-use.lock`, opened with `FileShare.None` for the launch's life
  and let go at the end of quitting once the file is written (the Editor stays open after Play Mode), else in
  `ResetVideoForNewLaunch` after waiting for it. Another launch touches a run only after opening that lock itself (`ClaimEnded`),
  and small files are saved through a temporary file of their own per write. **ProtokitePlaytestRecordingsFolder** holds
  the finishing pass (`FinishEndedRuns`: finish a `.part` through `IProtokitePlaytestRecordingFile`, keep a playtest
  recording with a session and a test video, delete a playtest recording with no session and a run left with no video,
  list what it cannot finish or delete) and the budget (`MakeRoom`). The pass runs on `Task.Run`, started by the driver, and
  the recording starts only once it is done (10 s at most), since `MakeRoom` counts a cut-off run the pass has not finished at its whole reservation.
  `StartVideoRecording` makes its run with the room `BytesToMakeRoomFor` wants **before** making room, so a game starting at
  the same moment counts it; a run in use, or with an unfinished video, counts at its reservation and is never deleted,
  except that a held run whose video is finished (`FinishedVideoPath`: a file of a kind Protokite takes, so a stray file
  never counts) is counted at its files, since it grows no more and an upload may hold it for minutes;
  ended runs go test videos first, then waiting uploads, oldest first; under 1 MB left, no recording. The size limit is cut
  to the room left and the reservation raised to match. The session is saved into the run when it starts
  (`FinishPlaytestSessionStartAsync`, and quitting's wait for a start on its way) and when a recording starts after it.
  One start path, `TryStartRecording(kind, ...)` → `StartRecordingRun`, serves both kinds; `RoomToReserve` and the kind decide
  the rest: a test video reserves `max(BytesToMakeRoomFor, 1 MB)` and its size limit is never raised past that, and
  `MakeRoom` reads the new run's kind, so room for a test video deletes older test videos only, never a waiting upload.
- **Test videos (`Runtime/ProtokitePlaytestTestVideo.cs`)** — `RecordTestVideo(seconds, out whyNot)` (public since 1.60.0, with
  `TestVideoState`, `FinishedTestVideoPath` and `TestVideoProblem`; the editor window calls it too) asks the running game for one; `UpdateVideo` starts it at the end of a frame once the finishing pass
  is done. Its own fields (`_testVideo`, `_testVideoRun`), never the launch's recording slot, so no session is saved beside
  it and nothing uploads it; its run is let go as soon as its file is written. The launch's recording belongs to the
  playtest: a test video is refused while the playtest records or is due to, and when the config turns video on,
  `MakeTheTestVideoGiveWay` drops one waiting and stops one recording (`PlaytestRecordingStarts`), and the playtest's starts
  once its file is written. The player's consent answer does not touch it (it is never sent). Stopped and waited for at quit
  and on a new launch; `TestVideoState`, `FinishedTestVideo` and `TestVideoProblem` are what the window shows.
- **Uploads (`Runtime/ProtokitePlaytestUploads.cs`)** — this launch's recording goes when its file is finished and its session
  has started, whichever is second (`ReportFinishedVideo` and `FinishPlaytestSessionStartAsync` both ask), with the session's
  own URL and headers; never at quit (the session is no longer Started). Earlier launches' go from `Refresh` once the finishing
  pass is done and Flock runs: one at a time, oldest first, each run claimed while sent, with this launch's API key and the
  session's Game Version ID (`HeadersForTheSession`). Each upload: a link (`ProtokiteClient.RequestRecordingUploadLinkAsync`,
  enveloped `upload_url`) only now, then core's `UploadFileAsync` with the file's own content type (this launch's from its
  writer, an earlier one's from its ending through `ContentTypeFor`, a test holding the two equal); uploaded only on the
  storage's 2xx; one more try with a fresh link unless S3 said `SignatureDoesNotMatch`; uploaded → `DeleteEverything`. One
  `CancellationTokenSource` a launch, cancelled at quit, by `Stop` and on a new launch.
- **Native~/** — `protokite_vpx.c`, `build-protokite-vpx.sh` (maintainers: finds Visual Studio 2022 through vswhere,
  downloads libvpx, nasm and make pinned by SHA-256, builds, links, and refuses a DLL that needs more than KERNEL32;
  `--check-dll <dll>` runs those checks alone) and `link-protokite-vpx.bat`. A `~` folder, so
  Unity never imports it and it carries no `.meta`.
- **ProtokitePlaytestNativePluginImport** (Editor) — the DLL's platforms (64-bit Windows editor and players only) set
  through `PluginImporter` and saved, never by hand; run after every script load, because settings changed from an import
  rule do not stick to a native plugin (measured).
- **ProtokitePlaytestDriver** — a hidden `DontDestroyOnLoad` object started `BeforeSceneLoad` (`StartWithTheGame`) in every
  launch, playtesting on or off: with it off the status stays `TurnedOff` (no config, no session, no recording) and the
  driver only finishes and uploads what earlier launches kept, so a build with it off never strands a recording.
  Calls `Refresh()` every frame and `Stop()` when destroyed. Feeds heavy analytics (from `Update`) and the video (at the end
  of the frame) the real time since its last call, never `Time.unscaledDeltaTime`: a player reports a stall there 1 to 6
  frames late (measured), too late to leave out the frame that carries it.
- **Heavy analytics (`Runtime/ProtokitePlaytestHeavyAnalytics.cs`, `ProtokitePlaytestPerformanceTimeline.cs`)** — measures
  while the loaded config turns `heavy_analytics` on, the playtest is open and `FlockClient.Analytics` is not the
  `NullAnalyticsProvider` (warned once otherwise). **ProtokitePlaytestPerformanceTimeline** is the engine-free part: ten
  seconds of summed frame time make a window (nearest-rank median, 95th and 99th, hitches at or over a threshold read at the
  close, memory sampled every frame for the peak), and `LeaveOutNextFrame` drops one frame. Every scene load noted
  (`sceneLoaded`) since the last Update is carried by this Update's frame, which is left out; a Single load sends
  `level_loaded`. The map is the active scene, followed by handle each frame. Memory is `ProfilerRecorder` "System Used
  Memory" (`CurrentValue`). Events go through `TrackEvent` under the category `playtest`; `RecordPlaytestEvent` is the
  game's own, any thread, the two names refused. A start makes a new timeline, so a window a stop cut short is dropped.
- **ProtokiteClient** (internal) — `GET /game/sdk/playtest-config` through core's `FlockHttpClient` and a `RetryHandler` built
  from `FlockClient.RetryPolicy`; headers from `FlockClient.GetGameHeaders()` (key + version, never the bearer). The answer is
  enveloped; **`ProtokitePlaytestConfig` is read by hand from `JObject`** so IL2CPP stripping has no model of ours to strip.
  A missing or non-boolean feature is off; a null form or a form without an id is none; a question without an id is dropped.
- **ProtokitePlaytestSettingsMenu** — **Protokite > Playtest > Settings**; creates the asset, and refuses to save one Unity
  cannot link to its script.
- **Setup checks (Editor)** — **ProtokitePlaytestSetupChecks** decides four checks with no editor and no network, from a
  **ProtokitePlaytestSetupInput** read once (`FromProject`: `ProtokitePlaytestSettings.Load()`, `Resources/FlockConfig`, the
  active build target and `GetPlatformSettings("Win64", "Architecture")`, measured to read `x64`/`ARM64`): the switch, the URL
  (the runtime's `IsUsableApiUrl` and `Describe` words, made internal for it), the Game Version (a `pt-` name, resolved;
  an ID-shaped value is a pasted ID) and video on the target (Win64 on x64 only, as the runtime). A Flock answer counts only
  for the settings it was asked with (`AskedFor`, one owner). **ProtokitePlaytestGameVersionLookup** asks Flock through core's
  `FlockHttpClient`: `by-name` for the name, then for a pasted ID `GET /v1/game_version` with it as `X-Game-Version-ID` (the
  route names an ID of any game), then that name `by-name` again, so only a name that resolves back to the ID in this game,
  and only a playtest's, is suggested. Only the routes' own coded 404s mean "no such version". **ProtokitePlaytestGameVersionQuestions**
  asks once per change of settings and keeps only the latest question's answer (a cancelled one is dropped when it lands).
  `UseTheSuggestedGameVersion` writes the name and the ID Flock resolved it to into core's `FlockConfigAsset`.
  **ProtokitePlaytestWindow** (**Protokite > Playtest > Setup Checks And Test Video**, and core's **Check Playtest Setup**
  button through `FlockPlaytestInstaller.SetupWindowMenuPath`) draws them, reading the project once per Layout event, and
  records test videos in Play Mode; under **While testing** it forgets this machine's consent answer and opens the feedback
  form (the menu holds only Settings and this window). A button that changes what is drawn below it ends the event
  (`GUIUtility.ExitGUI`), so no event draws what its Layout did not count; nothing in it waits across a script reload.
- **The C# surface** — every public member of `ProtokitePlaytest` has a one-line doc and a call in
  `ProtokitePlaytestPublicSurfaceTests`, whose reflection test fails on a public member with no call. **Sample**:
  `Samples/PlaytestSample` (asmdef `Protokite.Playtest.Samples`), one IMGUI script making every call, shipped in the
  `.unitypackage` and compiled by a git install as core's quick start is.
- Tests: **ProtokitePlaytestStatusTests**, **ProtokitePlaytestVideoEncoderTests** (encode and decode frame for frame
  with each codec, a fake encoder held to the same contract), **ProtokitePlaytestWebmFileTests** (real VP8 and VP9
  recordings read back by a reader of their own, decoded frame for frame, cut off and finished; a fake recording file held
  to the same contract), **ProtokitePlaytestFrameScheduleTests**, **ProtokitePlaytestVideoRecordingTests** (fake frames and a fake encoder
  through the real file: limits, drops, failures, the bounded wait), **ProtokitePlaytestScreenFrameSourceTests** (the real
  shader and readback on known colours), **ProtokitePlaytestVideoSettingsTests**, **ProtokitePlaytestVideoTests** (when the
  playtest records and what stops it), **ProtokitePlaytestPerformanceTimelineTests**, **ProtokitePlaytestHeavyAnalyticsTests**
  (through core's real event queue, each test in a launch folder of its own), and in PlayMode
  **ProtokitePlaytestHeavyAnalyticsPlayModeTests** (a real scene load, time away and an active scene through the real
  driver), **ProtokitePlaytestScreenRecordingTests** (the real screen, encoder
  and file, judged against the screen; windowed editor only), **ProtokitePlaytestSessionTests** (held starts and ends for the quit and late-answer
  cases), **ProtokitePlaytestConfigTests** (EditMode, fake transport; a held-answer adapter
  for late replies), **ProtokitePlaytestDriverTests** (PlayMode, the real driver), **ProtokitePlaytestSetupChecksTests** (each
  check failing and passing from real settings objects, the project's own read, core's menu paths),
  **ProtokitePlaytestGameVersionLookupTests** (a fake Flock answering by name and by the ID header, held answers) and
  **ProtokitePlaytestTestVideoTests**; the windowed PlayMode pass records a real test video. A live `[Explicit]` check lives in
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
