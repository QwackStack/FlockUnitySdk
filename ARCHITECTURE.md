# Flock Unity SDK — Code Map

Orientation map of the folders and classes. For **API usage and examples**, see [README.md](README.md) — this file only says *what each piece is*, not how to call it.

```
Runtime/      runtime SDK            (asmdef Flock.Runtime)
├─ Providers/   feature APIs (auth, player, config, game, shop, command, asset)
│  ├─ Analytics/  analytics sender (+ no-op stub)
│  └─ Multiplayer/  multiplayer entry point, its repeating calls, queue names, parties and sessions
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
- **Multiplayer/FlockMultiplayerProvider** — `FlockClient.Instance.Multiplayer` (stripped by `FLOCK_NO_MULTIPLAYER`; the whole folder is the provider's). Public calls: `CreatePartyAsync`, `JoinPartyAsync`, `GetMyPartyAsync`, `HostSessionAsync`, `JoinSessionAsync`, `GetSessionAsync`, `GetMySessionAsync`, `FindMatchAsync` (and `FlockParty.FindMatchAsync`); it owns what the matchmaking and session calls build on. **FlockRepeatingCalls** runs every call multiplayer repeats on the game's frames (`FlockBehaviour.OnTick`, so a web player needs no timers): one request in flight per call, each wait the interval jittered by a quarter either way (or Retry-After when longer), keyed by name within one sign-in. The sign-in number stops a call and drops an answer or failure that lands after the sign-in ended; `Stop()`, Flock's shutdown, the no-domain-reload reset and quitting stop calls and cancel their requests on the way. No answer, a 5xx, 408, 429 or an unreadable answer keep a call going; any other failure stops it and is reported once. **FlockMatchmakingQueueNames** finds a queue's id by its exact name from the game's queue list (API key only, at most 200, oldest first, no paging), read once a launch and again only on a miss; a name still missing is refused with `matchmaking.queue_not_found` before any search, and of two queues sharing a name the older is used, with one warning. **FlockParties** is the one owner of the player's party: at most one `FlockParty` held per sign-in, changed only from the newest reading of `party/me` (or `party/{id}` after a join). Every read is numbered when sent; one sent before the newest applied read, or before a change this game made finished, changes nothing, and a call whose answer was overtaken reads again. Kick, make leader and update are followed by a read, so events come from one place whatever made the change; create needs none (its only member is its maker). The party is read again every `FlockInitConfig.PartyRefreshInterval` (Party Refresh Seconds, 10; 0 off) through the repeating calls; a permanent failure stops that with one warning and `GetMyPartyAsync` starts it again. It ends once (`Ended`, after the state is settled): `Left` and `Disbanded` by this game (also when a refresh sees the game's own leave first), `Removed` when the server no longer lists the player (a refresh finding no party or another, or an action answered `party.not_found`), `SignedOut` when the sign-in ended (read at once, raised on the next frame or the next read), and quietly at Flock's shutdown and the no-domain-reload reset. **FlockPartyRequests** sends the party routes, each attempt checking it still goes out as the sign-in it was asked for (a retry after a player switch is cancelled, never sent as the next player). **FlockMultiplayerSessions** is the one owner of the player's session: at most one `FlockMultiplayerSession` held per sign-in. Every session route answers the whole session, so a change's answer is taken as it comes and settles the reads this sign-in sent before it; reads are numbered as with parties; a reading whose host or connection epoch is below the held one's (the server's only grow) changes nothing. A call answered `session_not_found` or `not_a_participant` reads the session once to learn why it ended, then throws. Hosting or joining another session ends the held one at once (`moved_to_another_session`); `GetSessionAsync` hands back the held object, or an ended one for a session the player left. It ends once (`Ended`, a string reason): the server's (`ended_by_host`, `host_left`, `expired`, `empty`), `left`, `dropped`, and `signed_out` for a sign-in that ended (read at once; every later end of such a session stays `signed_out`); quitting gives the seat up without waiting and raises nothing; Flock's shutdown and the reset end it quietly. While a session is held, a repeating call ("Session heartbeat") keeps the seat at the interval each answer gives (`heartbeat_interval_seconds`, 20 s when none); its answer is a numbered reading, so the heartbeat is also the refresh; a new interval restarts it, ending the session stops it, and a heartbeat refused because the seat is gone reads once to learn why and ends the session (any other refusal warns once and stops them, and the next reading starts them again). `GetMySessionAsync` reads `sessions/current`: null when seated nowhere (a held session then ends with the reason a reading gives). A change of host clears the address, and `ConnectionChanged` is raised for that too. `HostSessionAsync` sends `same_version_only` only as false. **FlockMultiplayerSessionRequests** sends the session routes the same way (host, end and make host never retried). **FlockMatchmaking** is the one owner of the player's search for a match: at most one per sign-in (a second is refused), its ticket checked through the repeating calls ("Matchmaking check", every 3 s jittered) until it leaves the queue. A match hands over the session it seats the player in through the sessions owner (held, any held one ending as moved); expired, `party_changed` and another game's cancel are outcomes (`FlockMatchmakingResult`), not failures. The game's token cancels the search on the server, and a match that landed meanwhile (the cancel refused as not cancelable, or a ticket still on its way) gives its seat up. On `already_queued` it reads "my ticket" and cancels it only when it is the player's own search alone, or this party's when its leader searches again, then searches once more; it never cancels a party's search for a search alone. A party member's game learns of the leader's search from the party refresh, which also reads the player's own ticket, or from its own `FindMatchAsync`, which reads it until the party's search shows up: its watch starts with one reading when create, join or get-mine first hands the party over, which only marks where its searches stood (a search already running is followed by the next reading, once the game can listen), each party ticket is reported once, one matched between two readings is reported started then ended, `SearchEnded` is raised before the game's call returns, and readings are numbered so one sent before a search began changes nothing. A sign-in that ends ends the search as cancelled with nothing more sent; quitting cancels it without waiting; Flock's shutdown ends it quietly. **FlockMatchmakingRequests** sends the ticket routes, each acting for its sign-in. **Connecting players:** `PublishDirectConnectionAsync(port)` publishes mode `direct` with `address` (the public address when known, the LAN one otherwise), `port`, `lan_address` and `public_address`; the backend checks none of it, so the SDK does. **FlockDirectAddresses** finds the LAN address from the route toward the STUN server (a UDP socket's Connect, nothing sent) and the public address from one STUN binding request (XOR-MAPPED-ADDRESS, 2 s), the STUN servers read once a launch from `multiplayer/relay-credentials` asked with no session (a failed read is not kept); a web player has no sockets and asks nothing. `WaitForConnectionAsync` is woken by the host publishing, the session ending, or Flock's shutdown (cancelled); while any call waits, a repeating call ("Session connection wait", every 2 s) reads the held session, its answers numbered readings like the heartbeats', and the last waiter to go stops it (a session ending wakes its waiters inline on the main thread, so that is the one place reads stop). `FlockMultiplayerSessionConnection.FindDirectAddressAsync` picks the host's LAN address when this device's public address equals the host's, the published address otherwise, and refuses anything but a dotted IPv4 address and a whole port from 1 to 65535. **Flock.Multiplayer.Netcode** (`Runtime/Providers/Multiplayer/Netcode/`, its own assembly, compiled only with Netcode for GameObjects 1.x or 2.x installed and dropped with Multiplayer) adds `session.StartNetcodeAsync(networkManager)`: the host listens on Unity Transport's port on every address, starts as host, then publishes (a refused publish shuts the netcode down and throws); a player waits for the host, connects to the address found and returns on its own connect or disconnect, or when the netcode stops without either; a game's cancel shuts the netcode down. **Join verification (1.77.0):** on the host, `StartNetcodeAsync` installs **FlockJoinChecks** as the NetworkManager's approval callback (refused when the game set its own; removed, and the approval switch put back, when the netcode stops, unless the game set a check since). A payload without Flock's join token in front (`FlockJoinTokenPayload`: a 5-byte marker, a 2-byte length, the token, then the game's own bytes) is refused at once; otherwise the approval is held pending while `VerifyJoinTokenAsync` asks Flock, given until 2 s before the Client Connection Buffer Timeout (at least 1 s, at most 8 s, so a player on the default 10 s hears the refusal), and the answer is taken on the main thread: a player still waiting is let in, a second connection of a player still here (waiting or connected) is refused, the game's own check (`ApproveJoiningPlayer`) runs after Flock's, and a connection is remembered as its player (`PlayerForConnection`) only once let in. Every failure but Flock's verdict refuses (`flock_unreachable`). A joining player asks for a token once the host is known, sends it in front of its own `ConnectionData` with approval on (part of the config both ends must agree on), puts both back when the connect ends, and reads a refusal from the host's reason (Netcode for GameObjects 2.x writes `[Disconnect Event]...` of its own when there is none).
- **FlockSnapshotStore** — on-disk snapshot cache backing offline reads.
- **Analytics/FlockAnalyticsProvider** — sends sessions, gameplay events (`TrackEvent`: refused past 200/100 characters, for an empty or reserved name, or without consent; queued on disk and sent only while a player is signed in, a pre-sign-in event credited to the next sign-in; sent once at once when the queue is off), diagnostics (`LogDiagnostic...`; the former `LogEvent`/`LogError`/`LogException` are `[Obsolete]` forwarders), the game's captured exceptions (taken on its frame tick, through the repeat rule, queued like diagnostics) and transactions. It records no gameplay event of its own: the heartbeat is local, and the crash report is a diagnostic. · **NullAnalyticsProvider** — no-op when analytics is switched off in settings (`Enabled`).

## Runtime/Http
- **FlockHttpClient** — static GET/POST/… facade; maps status→exception in one place for every call, parses the coded `detail` (object *or* FastAPI's field-error array) into `Code`/`ServerMessage`, attaches the matching `Hint`. `…Async<T>` reads the body and fails on an empty one; the overloads without a type argument are for routes with nothing to read (a 2xx with no body or a JSON body is a success, a 204 included; a body that is not JSON, such as a captive portal's page, still fails) and carry the six no-schema analytics/log/session-end calls.
- **FlockEndpoints** — every relative API path the SDK calls (consts + parameterized builders); no raw path literals at call sites.
- **FlockProviderBase** — base class for providers; shared fetch + snapshot + validate helpers. `ExecuteAsync<T>` runs a call through retry + token refresh, refreshing and retrying a 401 only while the sign-in it went out under (`FlockClient.SignInNumber`: moved by every login, restore and sign-out, never by a refresh) is still the current one; a 403 ("not allowed": not the host, not the party leader, a proxy) is never answered with a refresh; a call that acts as the signed-in player through their token passes `actsForSignIn: SignInToActFor`, and each of its tries, retries included, is cancelled (`OperationCanceledException`) once that sign-in has ended, so it never goes out as the next player (auth links/unlink/revoke/reset/verify, every notification call, leaderboard my-rank and around-me, shop purchase and consume, every party request; key-only reads and player-data writes, which name their row, keep retrying); `ExecuteWithoutResultAsync` does the same for a call that returns nothing.
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
- **TokenStore/** — **ITokenStore** + `StoredTokens`, with **Android/Ios/Mac/Windows/WebGl/Other** secure-storage impls. Android's hands bytes to and from Java as `sbyte[]` through **FlockJavaBytes** (bit for bit, so earlier files still read): Unity warns on every byte array at its Java boundary, and `FlockJavaBytesTests` fails a Java call that reads a `byte[]` back.

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
- **FlockModelPreservation** — `IUnityLinkerProcessor`: on every player build writes `Library/Flock/link.xml` keeping `Flock.Runtime` whole and each `Flock.Generated.*` namespace in whichever player assembly holds it, so IL2CPP Medium/High stripping cannot remove what Newtonsoft reaches by reflection; it also keeps .NET's `System.Configuration.ExeConfigurationHost` and `System.Net.Configuration`, which a Mono player stripped at High otherwise loses, so that no web request could start (measured, 1.63.0). A `link.xml` inside a UPM package is not read by the linker (measured); a runtime-only `.unitypackage` (Package Builder, Editor unticked) ships a static one instead.
- **FlockCodeGenValidator** — warns when the baked version id drifts from generated schemas; `GetGeneratedGameVersionId()` returning null is the "codegen never ran" signal.
- **FlockCodegenCompileHint** / **FlockCodegenHintClassifier** — watch compilation and point at Codegen > Sync when an unresolved member looks like a generated accessor (classifier is pure/testable). Recompiles only — a cold start compiles before `[InitializeOnLoad]`; the Codegen tab's Status card covers that.
- **FlockSetupChecklist** / **FlockSetupClassifier** (+ `FlockSetupItem`/`FlockSetupState`/`FlockSetupFacts`/verdict enums) — pure, testable setup-readiness logic.
- **FlockFirstRunBootstrap** — opens the window on first import. · **FlockSdkGuideEditor** — inspector for the guide.
- **FlockProviderManifest** — maps providers ↔ `FLOCK_NO_*` defines for event-subset builds.
- **FlockPackageBuilder** — assembles the distributable package (`ShowWindow`), through one static `Build(PackageContents)` the window
  and the release share; `BuildReleaseFromCommandLine` (`-executeMethod ... -releaseOut <folder>`, run by `unity-packages.yml`) builds
  `ReleaseContents` (every provider, editor, samples, docs) and the playtest's package, exiting 0 only when both were built. No menu item in the SDK: a git install ships
  this file, so the maintainers' project (FlockUnityProject's `Assets/FlockTestRun/QwacksDevMenus.cs`) adds **Qwacks Dev >
  Package Builder**, and `FlockMaintainerToolingTests` fails if the SDK itself names a Qwacks Dev menu.
- **FlockPlaytestInstaller** — the Playtesting tab's install, update and remove for the Protokite Playtest package: downloads `ProtokitePlaytest-<version>.unitypackage` from the GitHub release matching `FlockSdkVersion.Current` (a blocking, cancellable download, so a script reload cannot drop it) and imports it; an update deletes the old `Assets/` copy only once the new one has downloaded, so a dropped file cannot linger; every download result but success counts as a failure (a failed disk write answers 200). Finds an installed copy from its assembly definition, wherever it is. Reads the version through `InternalsVisibleTo("Flock.Editor")`. Refuses to install into a Flock SDK exported without Analytics (`WhyPlaytestCannotBeInstalled`, whose refusal compiles only under `FLOCK_NO_ANALYTICS`: the playtest calls `FlockClient.Analytics`, and the define lives in Flock's own `csc.rsp`, where the playtest cannot see it). Names the two playtest menu items the tab
  opens (`SettingsMenuPath`, `SetupWindowMenuPath`); the playtest's own tests read both and check its menu has them.
- **FlockPlaytestPackageBuilder** — maintainer tooling (`BuildFromMenu`, called by the maintainers' project from **Qwacks Dev > Build Protokite Playtest Package** through `InternalsVisibleTo("FlockTestRun.Editor")`, or `-executeMethod ...BuildFromCommandLine -playtestOut <folder>`): stages the playtest's `Runtime`, `Editor` and `Samples` under `Assets/ProtokitePlaytest/` with GUIDs made from their paths and exports it; a Unity text asset naming another shipped file by GUID (the panel settings name their theme) is given that file's new GUID (`NewGuidsByOldGuid`, `WithNewGuids`), or the release would name a file it does not carry (measured, 1.63.0). Excluded from core's own `.unitypackage`.

## Editor/Codegen/
Writes typed accessors to `Assets/Flock/Generated/`. Each sync replaces the files it generated and nothing else: `GeneratedFiles` is the one owner of what codegen may delete (a `.g.cs` with codegen's header, its `.meta`, and a folder that leaves empty).
- **FlockCodegenMenu** / **FlockCodegenCli** — menu + headless CI entry points.
- **SchemaFetcher** / **SchemaHasher** / **FlockSchemaSnapshot** — pull schemas + content-hash for drift detection.
- **TypeMap** — backend→C# type mapping. · **CodeGenNamingHelpers** — safe identifier names.
- **\*Emitter** (GameConfig, ConfigAccessor, PlayerAccessor, PlayerTemplate, SchemaProperty, Command, Shop) — generate the typed C#.
- **ManifestEmitter** — emits `SchemasManifest` (GameVersionId + hash). · `EmitResult`/`CodegenResult` — codegen DTOs.

## Samples/
Shipped with the package (not `Samples~`, which a `.unitypackage` cannot carry). **QuickStart** (asmdef `Flock.Samples.QuickStart`):
one IMGUI script, device sign-in, a test event and a player-data read. **Multiplayer** (asmdef `Flock.Samples.Multiplayer`,
compiled only with Netcode for GameObjects 1.x or 2.x, and listed in the Multiplayer provider's folders so a package built
without Multiplayer leaves it out): **FlockMultiplayerSample**, the shared base (device sign-in with a register fallback,
`PlayAsync` starting the netcode for a session and giving the seat up when the start fails, the players, a Wave named message the
host passes on, Leave or End Session, the session's `Ended`), with **FlockPlayWithFriendsSample** (host by code, join by code) and
**FlockQuickMatchSample** (Find Match in a queue named on the component, Cancel). A test in `FlockMaintainerToolingTests` fails
when a sample built on a provider's code is not dropped with that provider.

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
  **Playtest ID** (1.68.0) chooses the playtest: the editor keeps the playtest's version ID it resolved and the Playtest ID it was
  resolved for in two hidden fields, and `PlaytestVersionId` answers only while they match letter for letter (`ChoosesAPlaytest`
  is false for a blank one, which leaves the playtest to Flock's Game Version as before).
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
  test turns it on). The file's save, read and forget are `ProtokitePlaytestAnswerFile<TChoice>`'s, shared with the next answer.
- **Upload networks (`ProtokitePlaytestUploadNetwork.cs`)** — the phone's second question (1.66.0): `ProtokitePlaytestUploadNetworkChoice`
  (`NotAnswered`, `WiFiOnly`, `WiFiAndMobileData`), the inert rule `AllowsUploadOn` (Wi-Fi only uploads only on what Unity reports
  as a local network; Unity's Android player reports a default network with the cellular transport as carrier data and any other
  as local, read in its classes), the spellings `wifi_only` / `wifi_and_mobile_data` / `not_asked`, and
  `ProtokitePlaytestUploadNetworkFile` (`playtest_upload_network.json` beside the consent file). Put only where a build that asks
  records a phone's screen (`OnAndroid`, Record Video On Android) after an answer that lets the screen be recorded, and only when
  the loaded config turns video on (`UploadNetworkAnswerIsDue`); the session start waits for it (`SessionCanStart`) and sends it
  as `playtest_upload_network`; the recording never waits. `UploadsWaitForWiFi()` is the one gate both uploads read (the network
  is read only for a Wi-Fi only answer, through `NetworkForTesting` in tests, the form's network check included);
  `StopUploadsTheNetworkNoLongerAllows` cancels an upload under way when the device leaves Wi-Fi (a token of its own beside the
  launch's), and the next frame's `Upload...WhenReady` starts again a pass whose result says it stopped for that. An answer that
  cannot be saved holds for the launch. `RecordingsWaitForThePlayersNetwork()` (that gate closed, or the question still to come)
  has `MakeRoom` keep every recording waiting to upload (owner, 2026-10-04).
- **Panels (`Runtime/Panels/`)** — **ProtokitePlaytestPanel**: a full-screen UI Toolkit panel built from code (a `UIDocument`
  and a copy of `Runtime/Resources/ProtokitePlaytestPanelSettings.asset`, which holds the empty imported theme
  `ProtokitePlaytestPanelTheme.tss`: settings or a theme made in code warn and throw on Unity 2021.3, measured 1.63.0; the built-in
  font given at the root, sorting order 30000), one view at a
  time so a second question can follow, the view itself focused so no button is; it keeps the cursor free while open, notes a
  game that locks it again, gives the game's cursor back when closed (not an ended launch's), and is drawn only while playing,
  not in batch mode, with graphics. The panel's `DeliberateAnswers`, `AnswerButton` and `QuestionView` build every question:
  a press in the first half second is ignored (`SecondsBeforeAnAnswerCounts`, read by the form too), and a mouse press while
  the game keeps the cursor locked. **ProtokitePlaytestConsentQuestionView**: the words and the four buttons from one options
  table the tests read too. **ProtokitePlaytestUploadNetworkQuestionView**: the phone's second question, shown in the same panel
  after a consent answer that lets the screen be recorded (`UpdateConsentQuestion` picks the question due, so the cursor the panel
  freed stays free between them), with the megabytes a minute at the recording's bitrate. **Forget This Machine's Answer** in the
  setup window (editor) forgets both saved answers. **ProtokitePlaytestFormView**: the
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
- **Video encoder (`Runtime/Video/`)** — `IProtokitePlaytestVideoEncoder` (configure, encode an NV12 frame, finish; it
  says what encoded once it has) is the one seam recording goes through. **ProtokitePlaytestWindowsVideoEncoder** drives
  Windows' Media Foundation H.264 encoder (an MFT) directly from C# COM interop (`ProtokitePlaytestMediaFoundation`: every GUID
  and interface method order read from the Windows SDK 10.0.26100 headers), so the package ships no native file (Smart App
  Control refused the unsigned libvpx DLL earlier versions carried, measured). Graphics card encoders first, the game's
  graphics card maker's first; Windows' software encoder only with Allow Software Encoder. `Start` starts Media Foundation
  and the encoder on the recording's encoding thread before any frame is captured (0.1 to 2.2 s, measured), and that thread
  makes every later call and disposes of it; graphics card encoders are asynchronous, so a frame waits (polled, 10 s at most)
  for the encoder to ask for one and takes what it hands back, one output call per event: after a change of output format it
  waits for the next event, as Windows requires, and re-reads the buffer size the new format needs (Intel's encoder refused
  an output call on a laptop, most likely the one made at once). Tests drive this
  through a stand-in that keeps those rules (`StandInEventEncoder`, via `TransformForTesting`). Each frame goes in with its own time and comes out in order
  (B-frames off by low latency); output that is not an H.264 byte stream fails the recording rather than vanish, and the
  units are stored after their lengths, the first frame carrying the stream's sequence and picture settings
  (`ProtokitePlaytestH264`, which also checks a stored frame). **ProtokitePlaytestAndroidVideoEncoder** drives Android's
  MediaCodec through `libmediandk` from C# (`ProtokitePlaytestMediaCodec`), so Android needs no native file either. It codes
  against `IProtokitePlaytestAndroidCodec`, so tests run it through a stand-in codec that hands back frames late, in buffers
  of their own, past their buffer or without start codes (`ProtokitePlaytestStandInAndroidCodec`). It asks for constant
  bitrate where the phone takes it (the default overshot a busy scene by 26%, measured), no B-frames, a keyframe every 10
  seconds (as on Windows) and BT.709 limited range; copies NV12 at the stride and slice height the codec's input format gives; and sends the
  stream's sequence and picture settings with the first frame, from their own buffer or the output format. A configure or
  start the codec refuses ends the recording with the status. When the game goes to the background
  (`HandOverEverythingAndLetGo`, an encoder interface call) it ends the stream, hands over every frame the codec held and gives
  the codec back to the phone; the next frame makes a new one with the same settings, which must start on a keyframe with the
  stream settings the file's header already holds, or the video ends where the game left. Windows keeps its encoder (the call
  does nothing there). **ProtokitePlaytestVideoEncoders** asks the platform once a
  launch, on a thread of its own started with the finishing pass when playtesting is on (and, on Android, Record Video On
  Android is on), which encoders it has: Windows (64-bit, Media Foundation present, a graphics card encoder or the software
  one allowed), or the phone's codec list read through Java on that thread attached to it (hardware encoders that take NV12
  first, the software one only when allowed; each keeps its capabilities object, released at the next launch). A recording
  or test video becomes due only once that answer is in or its 10 s are up, so the main thread never waits for it, and a
  check an earlier launch started never answers a later one. On a phone, `FitTheEncoder` asks the chosen encoder whether it
  takes the video's size and rate and steps down when not (the box at 8, 6, 4 and 3 eighths at the asked rate, then 15 and
  10 frames a second), logging which. It logs the first "no video" (Warning where video is built, Log elsewhere). Only
  these three files name Media Foundation (a test scans for it).
- **Recording file (`Runtime/Video/`)** — `IProtokitePlaytestRecordingFile` (open, write an encoded frame, close, finish a
  file a dead run left; `BytesFor` says exactly what a frame adds, header included, for the size limit) is the seam a
  recording's frames go through, and names its own content type for the upload; **ProtokitePlaytestMp4File** writes
  fragmented MP4 and is the only file that names its boxes (a test scans for it). The file's type at open, the movie's
  header with the first frame, then one fragment per frame written in one piece, unbuffered, each laid out the same way;
  only the length (`mehd`) is stamped on close, so a file cut off anywhere plays up to the cut. Finishing a cut-off file walks
  the fragments against that layout and each frame's first unit, and the writer refuses any frame that check would stop at;
  after a failed write it takes no more frames, and closing cuts the torn one off. **ProtokitePlaytestWebmFinisher** finishes
  the WebM recordings earlier versions left (read off the file itself), and **ProtokitePlaytestRecordingFiles** picks the
  file a recording is written to and, by ending, the finisher and content type of a kept one.
  **ProtokitePlaytestFrameSchedule** decides which game frames are captured and when each is shown (the frame nearest each
  capture time, background time left out, times always rising, a length limit, and `HalfTheFrameRate` for a warm phone).
- **Video capture (`Runtime/Video/`, `Runtime/ProtokitePlaytestVideo.cs`)** — `UpdateVideo` runs at the end of every
  frame from the driver's coroutine (a batchmode editor never gets there, so its tests run in a windowed editor). It starts
  the launch's one recording when the loaded config turns video on, before sign-in; a Flock restart (config fetched again)
  does not stop it, a loaded config with video off or a closed playtest does. **ProtokitePlaytestVideoRecording** owns the
  schedule, an encoding thread (below the game's priority) and a writing thread, each fed by a bounded queue
  (8 and 300); frames are dropped before encoding and counted, the size limit is checked where a frame is handed to be
  written, the file is written as `.part` and renamed when finished, and a failure keeps every whole frame.
  `WriteOutEverythingHeld` (the driver's `OnApplicationPause(true)`, through `HandleGameWentToTheBackground`, for the
  playtest's recording and a test video) waits for the frames on their way (`TakeFramesOnTheirWay`), sends them, and queues a
  marker behind them that has the encoding thread call the encoder's `HandOverEverythingAndLetGo` and write what comes out, so
  a game Android ends while away keeps everything recorded; the marker takes no frame's room and returns no block.
  **ProtokitePlaytestScreenFrameSource** is the one GPU class: `ScreenCapture.CaptureScreenshotIntoRenderTexture`, a blit to
  the video size, the `ProtokitePlaytestRgbaToNv12` compute shader (BT.709, limited range; in the package's `Resources`), and
  `AsyncGPUReadback` with at most 3 frames on their way, into a pool of blocks (`ProtokitePlaytestFrameBlocks`). Rows flip
  only where textures start at the bottom (OpenGL); Linear projects convert back to sRGB before conversion. A screen whose
  shape no longer matches the video (a window resized, a phone turned) is scaled to its own shape in the middle, and the
  shader writes black outside it (centred, so no row order matters); a difference under 16 pixels, the sides' rounding, fills. The capture
  is asked for the encoder's pixel layout (the Android seam); `ProtokitePlaytestVideoEncoders` and
  `ProtokitePlaytestRecordingFiles` are the only places that pick the encoder and the file. **ProtokitePlaytestVideoSettings**
  reads the settings asset's video values in range, from the Android section in an Android player (the long side as a
  square box, so an upright game records upright) and from the Windows one everywhere else, the editor included; with
  Record Video On Android off an Android player records nothing and never asks the phone. It fits the video to the screen,
  each side a multiple of 16. Quitting
  stops the capture first and waits for the file within the session end's 3 seconds.
- **The phone's limits (`Runtime/ProtokitePlaytestPhoneLimits.cs`, `Runtime/Video/ProtokitePlaytestPhoneConditions.cs`)** —
  `UpdateVideo` first calls `KeepRecordingsWithinThePhonesLimits`: while a recording of an Android player captures, the phone is
  asked every 5 seconds of play (and at the first frame after any recording starts, `AskThePhoneAtTheNextFrame`) only what its
  settings use: Android's thermal status (`PowerManager.getCurrentThermalStatus` through Java, Android 10+; a phone that does not
  say is said once a launch) and the battery (`SystemInfo`). **ProtokitePlaytestPhoneConditions** holds the inert rules
  (`HeatStep`: moderate halves the frame rate, severe and above stop; `BatteryTooLow`: below the percentage and neither charging
  nor full; `LowStorageLine`: 500 MB or 5% of the storage, Android's `StorageManager` defaults) and the readers, each answering "not known" outside an Android
  player, with `...ForTesting` stand-ins the tests and the phone probes set. Every change of rate and every stop
  (`PhoneTooHot`, `BatteryLow`) is logged once. `StartRecordingRun` reads an Android player's free space (`StatFs` through
  Java: .NET's `DriveInfo` throws in an IL2CPP player, measured) and `MakeRoom` holds the budget to the other runs' files plus
  the free space above the low-storage line, so a deleted run counts as freed space.
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
  is done (and is held to the phone's limits like the playtest's). Its own fields (`_testVideo`, `_testVideoRun`), never the launch's recording slot, so no session is saved beside
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
  `CancellationTokenSource` a launch, cancelled at quit, by `Stop` and on a new launch, and one cancelled when the device leaves
  Wi-Fi under a Wi-Fi only answer (the outcome's `StoppedWhenTheDeviceLeftWiFi`); both kinds wait for `UploadsWaitForWiFi()`,
  checked every frame from `Refresh`.
- **ProtokitePlaytestDriver** — a hidden `DontDestroyOnLoad` object started `BeforeSceneLoad` (`StartWithTheGame`) in every
  launch, playtesting on or off: with it off the status stays `TurnedOff` (no config, no session, no recording) and the
  driver only finishes and uploads what earlier launches kept, so a build with it off never strands a recording.
  Calls `Refresh()` every frame and `Stop()` when destroyed; `OnApplicationPause(true)` writes out what the recordings hold, and
  coming back (pause off or focus on) leaves out the frame that carries the time away. Feeds heavy analytics (from `Update`) and the video (at the end
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
  (the runtime's `IsUsableApiUrl` and `Describe` words, made internal for it), which playtest (the Playtest ID resolved; with
  it empty, the Game Version: a `pt-` name, resolved, an ID-shaped value being a pasted ID) and video on the target (Win64 on
  x64 and Android, as the runtime). A Flock answer counts only for the settings it was asked with (`AskedFor`, one owner). **ProtokitePlaytestGameVersionLookup** asks Flock through core's
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
- **Playtest ID (Editor, 1.68.0)** — **ProtokitePlaytestIdLookup** finds a playtest's version from what was pasted (trimmed, a
  leading `pt-` dropped, upper-cased): `by-name pt-<id>` for a test's ID, else `GET /v1/game_version` for a version ID, proven
  by its name resolving back to it in this game, and refused when the name is no playtest's. **ProtokitePlaytestIdResolver**,
  one for the editor, asks once per change of the Playtest ID, Flock URL or key and `Keep`s the answer in the settings asset with
  a fingerprint of the Flock settings asked with (`IsResolvedWith`), saving that asset alone, only while the settings still hold
  what was asked; a question a newer one replaced is dropped; failing to reach Flock keeps what was resolved, and "no playtest"
  clears it. Views call `ResolveForAView`, which resolves only the settings a build loads and nothing while a test owns the
  resolver. **ProtokitePlaytestIdFieldDrawer** draws the field, the Resolved Version ID and what Flock said (`WhatToSay`).
  **ProtokitePlaytestBuildCheck** refuses a build with Playtesting Enabled on and a Playtest ID not resolved, or resolved with
  other Flock settings. At runtime `VersionThatNamesThePlaytest` is the version the config is fetched with, and only Protokite is
  sent it: the Playtest ID's; with none, a `pt-` Game Version's own ID (core's `FlockClient.GameVersion`); else none, which
  Protokite answers with the game's newest playtest. `_configGameVersionId` (the answering playtest's version when none was
  sent) owns every later request about the launch's playtest.
- **Live self-test (`Runtime/ProtokitePlaytestSelfTest.cs`, `ProtokitePlaytestSelfTestReport.cs`)** — the public front is
  `ProtokitePlaytestSelfTest.RunAsync(closedPlaytestGameVersionId)` and `IsRunning`; the steps are private members of the
  `ProtokitePlaytest` partial class, so they read the session's own URL and headers and the game's identity. Refused in a
  release player (`Debug.isDebugBuild`), without a running Flock, and while one runs. Its probes go through a `ProtokiteClient`
  of its own with `MaxRetries = 0` and header copies swapped by name whatever their letter case (`WithHeader`), so no refusal
  touches the game; a session a probe is wrongly given is ended with the headers it was started with. A refusal counts only
  with its own status, and for a form only naming its own question letter for letter (`WhyNotTheRefusal`). It waits frame by
  frame on the main thread (`Task.Yield` against real time: no timer, so WebGL waits too). A skip says its real reason. A Flock
  shutdown fails the next step once and skips the rest. Quitting logs how far it got (`StopSelfTestForQuitting`, from
  `HandleGameQuitting`). The last line is a warning when anything failed or nothing passed. The recording step stops the
  launch's recording and waits for `ThisLaunchsUpload` (renamed from `...ForTesting`, which it now serves), and passes on the
  storage's 2xx with the files deleted. The video's `_videoNotStartedBecause` / `_videoNotStartedIsExpected` say whether a
  missing recording is the build's nature (no encoder, nothing drawn) or a fault. The session is left for quitting to end.
  The setup window's **Run Live Self-Test** (Play Mode) runs it.
- **The C# surface** — every public member of `ProtokitePlaytest` has a one-line doc and a call in
  `ProtokitePlaytestPublicSurfaceTests`, whose reflection test fails on a public member with no call. **Sample**:
  `Samples/PlaytestSample` (asmdef `Protokite.Playtest.Samples`), one IMGUI script making every call, shipped in the
  `.unitypackage` and compiled by a git install as core's quick start is.
- Tests: **ProtokitePlaytestStatusTests**, **ProtokitePlaytestVideoEncoderTests** (the real Windows encoder on a thread of
  its own, decoded frame for frame by Windows' own reader, the keyframe interval, the software encoder only when allowed;
  every "no video" reason through a stand-in for what Windows offers; scans for Media Foundation and for any native file; a
  fake encoder held to the same contract), **ProtokitePlaytestMp4FileTests** (written files read back by a reader of their
  own that finds each frame from its fragment's own fields, as a player does, and names a fragment that points elsewhere;
  cut off every way and finished; a recording shaped like a phone's, with frames of thousands of bytes at uneven times and a
  later keyframe, cut through every part of every fragment; a real recording decoded by Windows; a fake recording file held
  to the same contract), **ProtokitePlaytestAndroidVideoEncoderTests** (the Android encoder through the stand-in codec, a new
  codec of its own after the background, and a new stream described otherwise refused) and
  **ProtokitePlaytestPhoneEncoderTests** (the phone encoder's choice, its reasons, the size and rate step-down),
  **ProtokitePlaytestH264Tests**, **ProtokitePlaytestWebmFinisherTests** (earlier versions' WebM, cut off and
  finished), **ProtokitePlaytestFrameScheduleTests**, **ProtokitePlaytestVideoRecordingTests** (fake frames and a fake encoder
  through the real file: limits, drops, failures, the bounded wait, what is on disk while the game is in the background),
  **ProtokitePlaytestPhoneLimitsTests** (heat, battery and free space through the phone's stand-ins, on a test video, the
  playtest's recording and the real driver), **ProtokitePlaytestScreenFrameSourceTests** (the real
  shader and readback on known colours), **ProtokitePlaytestVideoSettingsTests**, **ProtokitePlaytestVideoTests** (when the
  playtest records and what stops it), **ProtokitePlaytestPerformanceTimelineTests**, **ProtokitePlaytestHeavyAnalyticsTests**
  (through core's real event queue, each test in a launch folder of its own), and in PlayMode
  **ProtokitePlaytestHeavyAnalyticsPlayModeTests** (a real scene load, time away and an active scene through the real
  driver), **ProtokitePlaytestScreenRecordingTests** (the real screen, encoder
  and file, decoded by Windows (`ProtokitePlaytestWindowsMp4Reader`, in the PlayMode assembly, which the EditMode tests also
  use) and judged against the screen; windowed editor only), **ProtokitePlaytestSessionTests** (held starts and ends for the quit and late-answer
  cases), **ProtokitePlaytestConfigTests** (EditMode, fake transport; a held-answer adapter
  for late replies), **ProtokitePlaytestDriverTests** (PlayMode, the real driver), **ProtokitePlaytestSetupChecksTests** (each
  check failing and passing from real settings objects, the project's own read, core's menu paths),
  **ProtokitePlaytestGameVersionLookupTests** (a fake Flock answering by name and by the ID header, held answers) and
  **ProtokitePlaytestTestVideoTests**, **ProtokitePlaytestUploadNetworkTests** (the answer's rules, spellings and file, when a
  phone asks, and what the session start carries) with the upload and disk budget cases in **ProtokitePlaytestUploadTests**
  (a held upload stopped by leaving Wi-Fi and sent again), **ProtokitePlaytestUploadNetworkPanelTests** (PlayMode, windowed:
  the second question on the same panel, through its own buttons), and **ProtokitePlaytestSelfTestTests** (a fake Protokite that refuses for the server's
  reasons, reading the key, version, body and session, with each probe read back); the windowed PlayMode pass records a real test
  video. Live `[Explicit]` checks live in FlockUnityProject: `ProtokitePlaytestLiveConfigTests`, and
  `ProtokitePlaytestLiveSelfTest` (PlayMode, windowed), which `Libraries/Unity/playtest-self-test/run_p15_live.py` runs and
  judges on the backends' rows.

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
