# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).


## [1.79.0]

The groundwork for Flock's relay, which connects players on different networks: the SDK can now reserve an address on the relay
for a session, open it to the host's relay address, and send and receive packets through it. Nothing public uses it yet; the next
release plugs it into Netcode for GameObjects.

### Added
- An internal relay connection for a session, over UDP: logins Flock mints for the session's seat, the relay server's name looked up
  within 5 s, an address reserved (answering the relay's login challenge, and its stale nonce with the new one; a request lost on
  the way is sent again under its transaction, and given up on after 5 s), opened to the host's relay address, and kept alive on a
  thread of its own (the address, the opening and each channel renewed before the relay lets them go). Packets ride in 4-byte
  channel frames once a channel is bound to the peer; sending and taking one makes no garbage and never holds the game's thread.
  The session's end gives the address back before `Ended` is raised, answering a stale nonce on the way. Each failure has a reason
  of its own: a web player, a relay paused for the studio's bill, none offered for the game, too many logins in a minute, no seat
  in the session, Flock or the relay out of reach, a refused login, a full relay, a refused opening, a lost relay.

Released together with the Protokite Playtest's fix to its check of which video encoders the machine has (see its changelog).

## [1.78.0]

Two multiplayer samples, each one component to drop on an empty GameObject: play with friends by a session's code, or quick
match.

### Added
- `Samples/Multiplayer`, compiled only with Netcode for GameObjects 1.x or 2.x installed and left out of a package built
  without Multiplayer. `FlockPlayWithFriendsSample`: **Host** shows the session's code, **Join** takes a friend's.
  `FlockQuickMatchSample`: **Find Match** searches the queue named in **Queue Name**, and **Cancel** stops the search. Both sign
  in with the device (registering it the first time), start Netcode for GameObjects for the session with `StartNetcodeAsync`
  (the NetworkManager given, else the scene's, else one they make on **Port**), list the players, send a **Wave** over the
  netcode that the host passes on to the others, and **Leave** (**End Session** for the host). A start that fails gives the
  seat up. Each says it connects on a LAN or where the host's port is forwarded to it.

### Fixed
- The Quick Start sample's README named `Analytics.LogEvent` and a **Flock > Sync Schemas** menu that no longer exist.

## [1.77.0]

A host started with `StartNetcodeAsync` lets in only the session's players: each joining player's game sends a join token
Flock gives it, and the host checks it with Flock before the player is in.

### Added
- On the host, `StartNetcodeAsync` installs Netcode for GameObjects' connection approval: a joining player is held while Flock
  checks their join token (the frame is never held), then let in or refused with a reason the player's game is told. One
  connection a player: the same player connecting again while still connected is refused. When Flock's answer does not come in
  time (2 s before the NetworkManager's Client Connection Buffer Timeout, and at most 8 s, so a player on the default 10 s hears
  it), or cannot be read, the player is refused rather than let in unchecked.
- On a joining player, `StartNetcodeAsync` asks Flock for a join token once the host is known and sends it in front of the
  game's own `NetworkConfig.ConnectionData`, with connection approval turned on as the host needs; both are put back once the
  connect has ended. A refused player gets the new outcome `FlockNetcodeStartOutcome.Refused` with
  `FlockNetcodeStartResult.RefusedReason`, a `FlockNetcodeRefusedReason` value (`no_join_token`, `join_token_invalid`,
  `already_connected`, `flock_unreachable`, `session_ended`, `not_host`, `refused_by_the_game`, `game_check_failed`), the
  game's own reason, or Netcode for GameObjects' own words when the host's netcode turned the player away itself (such as
  "Client-1 disconnected by server." between builds whose netcode settings differ).
- `FlockNetcodeOptions.ApproveJoiningPlayer`: the game's own check on the host, run once Flock has let a player in, with a
  `FlockJoiningPlayer` (`PlayerId`, `ClientId`, and the game's own `ConnectionData` without the token) and Netcode for
  GameObjects' approval response, which arrives approved with the player object the config names.
- `session.PlayerForConnection(clientId)`: the Flock player behind a Netcode for GameObjects connection on the host, the host's
  own included.

### Changed
- A NetworkManager that already has a `ConnectionApprovalCallback` is refused by `StartNetcodeAsync` on the host, with a message
  naming `FlockNetcodeOptions.ApproveJoiningPlayer`. When the netcode stops, the NetworkManager's approval is put back as the
  game had it, unless the game set a check of its own meanwhile.
- A host on this version refuses players on 1.76.0, which connect with connection approval off.

## [1.76.0]

A session's players can connect their netcode directly: the host says where it is, and the others wait for that and connect.
With Netcode for GameObjects installed, one call does both.

### Added
- `FlockMultiplayerSession.PublishDirectConnectionAsync(port, cancellationToken)`: the host publishes how to reach it, mode
  `FlockMultiplayerConnectionMode.Direct`, with its public address (one request to the STUN server Flock lists) or, when that
  is unknown, its LAN address, and the port. Outside a LAN, players reach it only when that port is forwarded to the host.
- `FlockMultiplayerSession.WaitForConnectionAsync(timeout, cancellationToken)`: waits until the host has published and returns
  `Connection`, or null when the time runs out or the session ends. The session is read every 2 s while it waits.
- `FlockMultiplayerSessionConnection.FindDirectAddressAsync(cancellationToken)`: where this device connects, a
  `FlockDirectAddress` (`Address`, `Port`, `IsLan`): the host's LAN address when this device shares its public address, the
  published address otherwise; null for a connection that is not a usable direct one.
- With Netcode for GameObjects 1.x or 2.x installed: `session.StartNetcodeAsync(networkManager, options, cancellationToken)`.
  The host starts as host on Unity Transport's port and publishes; a player waits for the host (`FlockNetcodeOptions.WaitForHost`,
  60 s), connects to it and returns once connected. `FlockNetcodeStartResult.Outcome` says how it ended
  (`FlockNetcodeStartOutcome`: `started_as_host`, `connected`, `host_never_published`, `session_ended`, `could_not_connect`,
  `could_not_start`, `not_direct`). The package does not depend on Netcode for GameObjects: these calls exist only where it is
  installed. Not in a web player.

### Fixed
- `TryGetValue` and `TryGetData` return false for a number too large for the type asked for, instead of throwing.

## [1.75.0]

A game can find a match by queue name, alone or as a party, and gets the session the match seats its players in.

### Added
- `FlockClient.Instance.Multiplayer.FindMatchAsync(queueName, options, cancellationToken)`: searches alone in the queue with
  that exact name and hands back a `FlockMatchmakingResult`. When players are matched, its `Session` is the session the match
  seats them in, held like any other (the longest waiting player hosts), and `PlayerIds` lists them. A search that ends
  without a match is an outcome, not an error: `FlockMatchmakingOutcome.Expired` (nobody found within 5 minutes),
  `PartyChanged` or `Cancelled` (stopped by another game). Cancelling the token cancels the search on the server, and a
  match that landed meanwhile gives its seat up; the game can search again at once. A search of the player's own left by an
  earlier launch is cancelled first.
- `FlockParty.FindMatchAsync(queueName, options, cancellationToken)`: the leader's game searches as the party, matched as
  one unit and never split; another member's game waits for the leader's search and gets the same match.
- `FlockParty.SearchStarted`, `SearchEnded` and `IsSearching`: every member's game hears the party's search, through its
  own call or the party's refresh, including a party matched between two refreshes. A search that ended before the game
  got the party is not reported; one still running then is. `SearchEnded` is raised before the game's `FindMatchAsync`
  returns.
- `FlockMatchmakingOptions.Attributes`, passed to the server as given.

### Changed
- The party refresh also reads the player's own matchmaking ticket, one more request each refresh while in a party.
- `CreatePartyAsync`, `JoinPartyAsync` and `GetMyPartyAsync` read the player's own matchmaking ticket once when they hand
  over a party the game did not hold yet, so a search the leader makes right after is never taken for an old one.
  Cancelling the call's token stops only that read; the party is handed over.
- Quitting cancels a running search without waiting, so it matches nobody who has gone.
- The `MatchmakingAlreadyQueued` hint now says what is left once the SDK replaces the player's own earlier search.

## [1.74.0]

Sessions keep their players seated for as long as the game runs, and a game can find its session again after a restart.
Players on different builds of a game no longer end up in one session unless the host allows it.

### Added
- Heartbeats: while a session is held, the SDK keeps the player's seat with a heartbeat at the interval the server gives
  for the game's timeouts (set in the game's multiplayer settings in the Flock dashboard). Each heartbeat's answer updates
  the session, so `PlayersChanged`, `HostChanged` and `ConnectionChanged` are raised without the game reading it. Nothing to
  call or set. A heartbeat the server refuses for a reason other than the seat being gone logs one warning and stops; the
  next `GetSessionAsync` or `GetMySessionAsync` starts them again.
- `FlockClient.Instance.Multiplayer.GetMySessionAsync`: the session the player is seated in, found without its id (after a
  restart, or one a party leader's game made), or null when seated nowhere. A session held until then that the player is no
  longer seated in ends, with the reason the server gives.
- `HostSessionAsync(sameVersionOnly: false)` lets players on any build join. By default a session keeps to its host's Game
  Version, and a player whose game sends another one is refused with `MultiplayerVersionMismatch`.
- `FlockErrorCode.MultiplayerVersionMismatch` and `MatchmakingVersionMismatch`, with hints.

### Changed
- When hosting moves, `Connection` is cleared until the new host publishes, and `ConnectionChanged` is raised for it.
- A game paused past the timeouts (a phone on the home screen, a browser tab in the background, a desktop window that lost
  focus with Run In Background off) sends no heartbeats, so the server gives its seat up; the first heartbeat after it comes
  back ends the session, as `dropped` while others kept it going, or with the session's own reason if it closed meanwhile.

### Removed
- `FlockErrorCode.MultiplayerExtrasPaused`: the server no longer sends it.

## [1.73.0]

Sessions: a player hosts a game others join with a code, says how to reach it, and the SDK keeps the session up to date
from every answer. Heartbeats are not in this version yet, so after about a minute the server moves hosting on and then
gives the seats up.

### Added
- `FlockClient.Instance.Multiplayer.HostSessionAsync` (seats 2 to 16, 8 when not given, and the game's own data),
  `JoinSessionAsync` (by join code; letter case does not matter) and `GetSessionAsync` (a session the player holds or once
  held a seat in; one that is over gives the session's own reason, so a host who left last reads `host_left`). A player
  holds one seat at a time: hosting or joining another session ends the one held.
- `FlockMultiplayerSession`: `JoinCode`, `HostPlayerId`, `IsHost`, `Players` (longest seated first; when the host leaves,
  the first of the others hosts), `MaxPlayers`, `Data` (read one value with `TryGetData<T>`), `Connection` (how to reach
  the host: a `Mode` and its `Values`, null until the host publishes), and `LeaveAsync`, `EndAsync`, `MakeHostAsync`,
  `PublishConnectionAsync`, `RequestJoinTokenAsync` (a token proving the player's seat, good for 60 s) and
  `VerifyJoinTokenAsync` (the host learns which player a token belongs to).
- `FlockMultiplayerSession` events: `PlayersChanged`, `HostChanged`, `ConnectionChanged`, and `Ended` with a reason from
  `FlockMultiplayerSessionEndReason` (`ended_by_host`, `host_left`, `expired`, `empty`, `left`, `dropped`,
  `moved_to_another_session`, `signed_out`; the server can add reasons, so keep a default). `Ended` is raised once, and not
  when Flock shuts down or the game quits. A session that has ended refuses its calls.
- Quitting the game gives the player's seat up without waiting, so the other players see it at once.

### Changed
- The session error hints name the calls to use (`JoinSessionAsync`, `GetSessionAsync`, `RequestJoinTokenAsync`,
  `HostSessionAsync`).

### Fixed
- A party whose player signed out, or another signed in, could end as `Left` or `Disbanded` after it had already read as
  `SignedOut`, when a leave or disband answered in the same frame. It now stays `SignedOut`.
- An answer to a party change that landed after another player signed in made that player's party read go out again.

### Known limits
- No heartbeats yet: by default the server hands hosting on from a host it has not heard from in 60 seconds and gives up
  a seat it has not heard from in 90 (the game's multiplayer settings in the Flock dashboard can change both), so a session
  lasts about a minute for its players. Heartbeats come in a later version and will be sent for you.
- The session changes only when a call answers (any call, or `GetSessionAsync`); nothing reads it in the background yet.
- After hosting moves, `Connection` can still show the previous host's address until the new host publishes.

## [1.72.0]

Parties: players group up with an invite code, and the SDK keeps the party up to date. And a retried call never goes
out as a player who signed in after it was made.

### Added
- `FlockClient.Instance.Multiplayer.CreatePartyAsync`, `JoinPartyAsync` (by invite code; letter case does not matter) and
  `GetMyPartyAsync` (the player's party, or null). A party lasts until the player leaves it, across launches, so call
  `GetMyPartyAsync` after signing in; it returns the same `FlockParty` object while the party lasts.
- `FlockParty`: `InviteCode`, `LeaderPlayerId`, `IsLeader`, `Members` (in the order they joined), `MaxSize`, `Settings`
  (read one value with `TryGetSetting<T>`), and `LeaveAsync`, `KickAsync`, `MakeLeaderAsync`, `UpdateAsync` and
  `DisbandAsync`. New settings replace all of the old ones.
- `FlockParty` events: `MembersChanged`, `LeaderChanged`, and `Ended` with a `FlockPartyEndReason` (`Left`, `Disbanded`,
  `Removed` when the server no longer lists the player, `SignedOut` when the sign-in ended). `Ended` is raised once, and
  not when Flock shuts down. A party that has ended refuses its calls.
- **Party Refresh Seconds** in **Flock > Settings** (`FlockInitConfig.PartyRefreshInterval`, default 10): how often a held
  party is read again. Each read is one API call per player; 0 turns it off, and the party then changes only when the game
  calls `GetMyPartyAsync`. A party is also read again after each change the game makes, so its events come the same way
  whether this game or another player changed it.

- `FlockProviderBase`: `SignInToActFor` and an `actsForSignIn` argument on `ExecuteAsync`, `ExecuteWithoutResultAsync` and
  the snapshot reads, for a provider of your own whose calls act as the signed-in player.

### Changed
- The party error hints name the calls to use (`GetMyPartyAsync`, `LeaveAsync`, `MakeLeaderAsync`, `DisbandAsync`).

### Fixed
- A call that acts as the signed-in player, retried after a timeout, a 5xx, a 408 or a 429, could go out as the next
  player when another signed in (or this one signed out) while it waited, since each try takes the sign-in current at that
  moment: an account link or unlink, a token revoke, a password reset or email verification, any notification call, the
  leaderboard's "my rank" and "around me", a shop purchase or consume. Such a try is now cancelled
  (`OperationCanceledException`) and never sent. Calls that need only the game's key (catalogs, config, public boards) and
  player-data writes, which name their row, still retry as before. A shop purchase also takes its sign-in with its player
  id, and records its analytics only for that sign-in, so neither the purchase nor its started, failed or completed record
  goes to a player who signed in after it began.

## [1.71.0]

The multiplayer entry point, ahead of its calls, and one fewer wasted request for every refusal that means "not allowed".

### Added
- `FlockClient.Instance.Multiplayer`, the entry point for multiplayer. It has no calls of its own in this version.

### Changed
- A request refused with 403 is no longer answered with a token refresh and sent again. A 403 means the player may not do
  this (for example, a player who is not the host), never that the sign-in has lapsed, which the backend answers with 401.
  Such a refusal is now one request, where it also cost a token refresh and a second send; the debug log no longer says the
  access token expired; and a 403 that came while the refresh token had lapsed no longer signs the player out. The
  exception is still a `FlockAuthException` with `StatusCode` 403.

## [1.70.0]

The first step of matchmaking and multiplayer support: the SDK now recognises every refusal the matchmaking, party and
multiplayer session services send, ahead of the calls that use them.

### Added
- `FlockErrorCode` members for the 27 refusals those services can send: 8 `Matchmaking...` (for example
  `MatchmakingAlreadyQueued`), 11 `Multiplayer...` (for example `MultiplayerSessionFull`, `MultiplayerNotHost`) and 8
  `Party...` (for example `PartyInvalidInviteCode`), each with a hint saying what to do next. The two "not the party
  leader" refusals have separate members: `MatchmakingNotPartyLeader` for a search, `PartyNotPartyLeader` for the party.
- A 404 from these services usually means "not yours, or no longer live" (a session the player has no seat in, a search
  that belongs to someone else), and their hints say so.

## [1.69.0]

No changes for games. Every release now carries `FlockSDK-<version>.unitypackage` and `ProtokitePlaytest-<version>.unitypackage`
from the moment it is published: the release builds them itself, in a Unity editor, and reads them back before it publishes. So
**Install Protokite Playtest** on the **Playtesting** tab of **Flock > Settings** can install the playtest from the first minute
of a release. A release is published only under a tag of the form `v<version>`, the address the tab downloads from.

## [1.68.0]

Released together with the Protokite Playtest's Playtest ID setting (see its changelog). With a Playtest ID set, the Flock SDK
keeps its own Game Version and every request it makes.

### Added
- `FlockClient.GameVersion`: the Game Version name the client was initialized with, beside `GameVersionId`.

## [1.67.0]

Released together with the Protokite Playtest's fix for the shader warnings an Android build logged (see its changelog).
Nothing in the Flock SDK itself changed.

## [1.66.0]

Released together with the Protokite Playtest's question about which networks a phone's recordings upload on (see its
changelog). Nothing in the Flock SDK itself changed.

## [1.65.0]

Released together with the Protokite Playtest's handling of a phone's background, heat, battery and free space (see its
changelog). Nothing in the Flock SDK itself changed.

## [1.64.0]

Released together with the Protokite Playtest's video recording on Android (see its changelog).

### Fixed
- **Signing in on Android no longer logs Unity's "using Byte parameters is obsolete" warnings.** The Android token store passed
  byte arrays to Java and read them back, which Unity 2022 and later warn about on every call: about 12 warnings, with their
  stack traces, at each sign-in (measured on a Galaxy S23 Ultra). Bytes now cross to Java as Java's own signed type, bit for bit,
  so tokens saved by an earlier version still read back: measured by installing this version over a build with the earlier store
  that had signed in, and the same player was restored. The store also lets go of the Java class handles it used to leave to the garbage
  collector.

## [1.63.0]

Released together with the Protokite Playtest's move to Windows' own video encoder, which ships no native file (see its
changelog: a breaking change to the playtest's video settings only).

### Fixed
- **A Mono player stripped at High could not start the SDK.** The first request failed with "The type initializer for
  'System.Net.HttpWebRequest' threw an exception": the linker removed .NET's configuration host and its `system.net` section types,
  which .NET builds by reflection the first time a web request is used, so the SDK could make no request at all. Every player build
  now keeps `System.Configuration.ExeConfigurationHost` and the `System.Net.Configuration` namespace. Measured in Mono players on
  Unity 2021.3 and 6000.3, which now sign in and send their events at High; IL2CPP players at High were not affected.
- The SDK's own tests compile on Unity 2021.3 again: two used `PlayerSettings.insecureHttpOption` (2022.1 and later) and NUnit's
  `Assert.DoesNotThrowAsync`, which 2021.3's test framework does not have.

## [1.62.0]

No changes to the Flock SDK's code. The README's playtesting section links the Protokite Playtest's new step-by-step guide,
released together with this version.

## [1.61.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest's live self-test.

## [1.60.0]

### Changed
- **The SDK adds no Qwacks Dev menu to your project.** The Package Builder and the Protokite Playtest release builder are the
  SDK maintainers' tools; a project installing the SDK from its git URL used to show them under **Qwacks Dev**. Nothing in a
  studio's use of the SDK changes. Released together with the Protokite Playtest's C# surface and sample.

## [1.59.0]

### Added
- **Check Playtest Setup** on the **Playtesting** tab of **Flock > Settings**, once the Protokite Playtest is installed:
  opens its setup checks. Released together with the Protokite Playtest's setup checks and test videos.

### Fixed
- **Flock > Settings no longer logs "Invalid GUILayout state" when it opens after Game Version was changed elsewhere.** The
  automatic Game Version resolve started while the window was laying itself out, after its status line had been laid out,
  so the window drew a line its layout had no room for. It now starts before the status line is laid out.

## [1.58.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest's feedback form, and its fix that
keeps the playtest's own files on WebGL.

## [1.57.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest's consent question.

## [1.56.0]

### Fixed
- **An exception whose own `Message` getter throws no longer escapes exception capture.** A game's exception type can
  throw from its getters, for example one whose `Message` reads a field that is null. With Unity's logging off, such an
  exception left uncaught on a thread ended an IL2CPP player through the capture's hook, where without capture the player
  kept running. From a faulted task nobody awaited, the capture threw on the finalizer thread, and that fault was lost. Such
  an exception is now reported under its type name, with `(its message could not be read: ...)` in place of its message.
  `LogDiagnosticException(Exception)` records it the same way instead of throwing back at the caller. A `FlockEvents`
  subscriber that throws one is logged instead of throwing out of the SDK code that raised the event.

### Changed
- **A captured exception's message is cut to 4,096 characters and its stack to 8,192**, with the text's full length noted
  at the cut. Up to 256 exceptions wait between frames. Before this change they were limited in number only, so a thread
  logging exceptions with 64 KB messages while the main thread was busy held 18 MB until the next frame. Captured
  exceptions are sent as cut; what a game reports itself through `LogDiagnosticException` is not cut.

## [1.55.0]

### Added
- **The game's exceptions are captured from start-up and from every thread.** Capture used to start at sign-in and hear
  only what Unity logged on the main thread. It now also hears exceptions logged or left uncaught on other threads and faulted
  tasks nobody awaited; one raised before sign-in is kept and sent once a player signs in. `Debug.LogError` lines are still not reported, and neither are the SDK's own exceptions. Each entry names where
  it came from in its extra data's `exception_source`.
- **Repeats of an exception are counted rather than sent one by one.** The first occurrence is sent at once; its repeats
  within 60 s are sent as one entry with `repeat_count` when the window closes. An exception thrown every frame used to
  queue an entry per frame, 216,000 an hour at 60 frames a second, and now costs about two a minute.
- **A launch reports at most 100 different faults.** Past that, a new fault is counted, and an
  `exception_reports_held_back` entry says how many reports were held back, at most once a minute.
- **Analytics Capture Exceptions** (on) and **Analytics Exception Repeat Window** (60 s) in Flock > Settings, and
  `FlockAnalyticsConfig.CaptureExceptions` and `ExceptionRepeatWindowSeconds` in code. With capture off,
  `LogDiagnosticException` still records.

### Changed
- **The crash marker is kept from start-up to a clean quit**, not only while a session runs, so a crash before sign-in or
  after sign-out is reported on the next launch. Its `app_termination` entry leaves out `previous_session_id` when no
  session was running, and `unhandled_exception_count` counts every captured exception, repeats and other threads included (none with
  capture off).

## [1.54.0]

### Changed
- The Playtesting tab will not install the Protokite Playtest into a Flock SDK exported without Analytics, and says why: the
  playtest calls the Flock SDK's analytics and would not compile there.

## [1.53.0]

### Fixed
- **WebGL: events recorded before sign-in were sent again on every later visit, credited to whoever signed in.** A WebGL
  player keeps its files in memory and copies them to the browser's storage only when asked, and only the sign-in save
  asked; so what the SDK sent and deleted afterwards came back on the next visit. Every change the SDK makes to its saved
  files now asks for that copy, so queued events, session records and the rest survive a reload as they last stood.
  A game that turns on `autoSyncPersistentDataPath` in its WebGL template keeps Unity's own copying. Two tabs of one game
  share the browser's storage, and the last to save wins.
- **`FlushAsync` could return without sending.** When another flush was already sending, an awaited `FlushAsync` returned
  at once with its events still queued. It now waits for that flush and then sends what is left. Flushes the SDK starts
  itself still leave the queue to the one already running.

## [1.52.0]

### Added
- **`Analytics.TrackEvent(eventName, properties, eventCategory)`** records a gameplay event for the Game Metrics
  dashboards. It never touches the network: the event is queued on disk and sent on the flush triggers while a player is
  signed in, so it is safe on a hot path, offline and from any thread; one recorded while nobody is signed in is held and
  credited to whoever signs in next. Properties keep their JSON types. It answers false, with a warning, for an empty name,
  a name over 200 characters or a category over 100 (the server cannot store either and fails every event sent with it),
  `session_started` (the server records it itself), properties that cannot be written as JSON, or no consent. With
  `CacheFailedEvents` off it sends the event once, straight away, while a player is signed in.

### Changed
- **The diagnostics calls are named for the dashboard they write to**: `LogDiagnosticEvent`, `LogDiagnosticError` and
  `LogDiagnosticException` replace `LogEvent`, `LogError` and `LogException`, which read like gameplay analytics while
  writing under Diagnostics. The former names still work, forward to the new ones and are marked `[Obsolete]`, so the
  compiler names the replacement. `IAnalyticProvider` gains the new members, so a class of your own implementing it needs them.
- **The SDK records no gameplay events of its own.** The heartbeat sent an `sdk_heartbeat` event every interval, which
  showed up on the Game Metrics dashboards among the game's own events; it no longer sends anything. The report of an
  earlier crash, `app_termination`, is now a diagnostic (type debug, under Diagnostics → Events) with the same data.
- **Gameplay events are sent only while a player is signed in.** `FlushAsync` called while signed out used to send events
  still carrying the pre-sign-in placeholder, which the server refuses together with every event sent beside them.
- The QuickStart sample's test event is a gameplay event (`TrackEvent`).

### Fixed
- **WebGL: analytics stopped after their first send, and a retry never came back.** WebGL runs everything on one thread
  with no thread pool and no timers, and the analytics queues resumed on the thread pool after each send, so each queue
  sent one batch per page and was then stuck; `FlushAsync` never finished; and on every visit after the first, the
  previous visit's session end hung sign-in, so `await` on a sign-in call never returned. A request the SDK retried
  waited on a timer that never fires. On WebGL the SDK now resumes on the main thread and waits between retries a frame
  at a time. Other platforms are unchanged.

## [1.51.0]

### Fixed
- **Nullable list, dict and object fields are generated, read and saved.** A player-template or game-config field typed
  `list?`, `dict?` or `object?` (the dashboard's nullable marker, which the backend also writes itself when it converts an
  older template) was skipped by codegen, so the generated class had no property for it and a typed `UpdateAsync` sent
  `null` in its place; the update replaces the whole row, so every typed save erased that field. Such fields are now
  generated as `List<T>`, `Dictionary<string, T>` or a nested class, like their non-nullable forms, read back as plain
  values, and saved with what they hold. Run **Codegen > Sync** again to get the new properties.
- **A write made online no longer overtakes an older write still queued.** After a reconnect the queued writes wait for a
  flush; a save made online in the meantime went straight to the server, and the older write's replay then put the old
  value back. `UpdatePlayerDataAsync`, `UpdatePlayerDataFieldAsync` and `UnlockAchievementAsync` now queue behind any write
  of the same player still waiting, and start a flush. The call then returns the cached row with the change applied (null
  when the row is not cached), as it does offline; with nothing waiting it goes straight to the server as before.
- **A flush under way when the signed-in player changes no longer takes a write off the next player's queue.** It stops;
  the write it was sending stays queued for its own player and is sent again when they next sign in (a queued write may
  reach the server twice; money is never queued), and the next player's own queue is sent straight after.
- **A token refresh answered after the player signed out, or after another player signed in, changes nothing.** It used to
  sign the old player back in and save their tokens (so the next launch restored them), replace the new player's tokens,
  or sign the new player out when it failed; and a request refused as one player could be sent again as the next. Each
  sign-in (a login, a restored session) and each sign-out now ends the sign-in before it, and a refresh or a retry for one
  that ended is dropped.
- **An asset download whose link has expired fetches a fresh link and tries once more.** A download link is signed for a
  few minutes, while an asset's record is kept for the whole session and reloaded on the next launch, so a download some
  minutes after the list was read failed with 403 until `ClearCache()`. A 401 or 403 from storage now reads that asset's
  record again, keeps it, and tries once more (`DownloadAsync` and `PreloadAsync`); when that read fails, the download's
  own failure is reported.
- **Codegen deletes only the files it wrote.** Sync deleted its `Player`, `Configs`, `Commands`, `Shops` and `Achievements`
  folders whole, and **Delete Generated Code** the whole output folder, so an output path shared with the game (such as
  `Assets/Scripts`) lost the game's own scripts. Both now delete only `.g.cs` files that carry codegen's header, with their
  `.meta`, and only the folders that leaves empty. The `Catalog/` folder older versions wrote goes only when the catalog
  was all that was in it, and the Codegen tab reads "generated" only when a sync's manifest is there.

## [1.50.0]

### Added
- **`FlockHttpClient.UploadFileAsync(url, filePath, contentType)`** streams a file from disk to a URL with a PUT, the way a
  presigned storage link wants it: the file is read as it is sent and is never whole in memory (measured: 1.5 GiB on Mono and
  IL2CPP), the request carries the Content-Type it is given and no other header of the SDK's, and a whole upload has no time
  limit, while one that sends nothing and gets no answer for 60 seconds is given up. It answers a `FlockFileUploadOutcome`:
  the storage's status and body when it answered, and `IsUploaded` only for a 2xx. Cancelling throws; every other failure
  comes back in the outcome, including a missing file and an upload Unity will not begin (an address it cannot parse, or a
  file another program holds open to itself). Call it on the main thread. On WebGL, which cannot stream a file, it sends
  nothing and says so, rather than reading the file into memory.
- **`IFlockFileUploader`** is the seam behind it, kept apart from `IFlockHttpAdapter` so an adapter a studio wrote needs no
  change; `FlockHttpClient.UseFileUploader` puts a stand-in in place for tests (null goes back to the platform's own
  `UnityWebRequestFileUploader`).

## [1.49.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest package's recordings kept on disk
across crashes, and its disk budget.

## [1.48.0]

### Fixed
- **Two copies of one game on one machine no longer disturb each other's analytics.** Every copy of a game, and the Editor
  beside a player, shares `Application.persistentDataPath` and PlayerPrefs, and the SDK kept one crash marker, one
  live-session record and one set of event queues for all of them. A second copy read the first's live marker as a crash,
  ended the first's session on the server, and sent the events the first had queued, a second time. Each launch now keeps
  those files in a folder of its own under `Flock/analytics/launches/`, locked while it runs, and takes over only the
  folders of launches that have ended, however they ended: their queued events are sent once and their crash and open
  session are reported once. Consent and the saved sign-in stay one per install.
- **Every file the SDK saves through a temporary file now has a temporary file of its own** (the event queues, the offline
  snapshots, the asset cache), and only temporary files over a minute old are deleted as left over. Two copies saving the
  same file no longer write, move or delete each other's, and a fresh temporary file (another copy still writing) survives.
  The offline snapshots and the asset cache now delete their left-over temporary files at all.
- **A package built without the Commands provider now compiles.** `FlockClient` named the commands queue's snapshot
  category outside the Commands guard, so every such export failed with CS0103. Every other single provider left out, and
  all of them at once, compiled already.

### Changed
- The crash marker and the live-session record are files in the launch's folder rather than PlayerPrefs keys. The first
  launch of this version takes over what an earlier version left, once: its queued events, its crash marker and its open
  session.
- The Editor now reports a crash of a standalone player of the same game, since they share the folder. The Editor still
  never records a crash marker of its own.

## [1.47.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest package's video recording.

## [1.46.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest package's recording files.

## [1.45.0]

No changes to the Flock SDK: this version is released together with the Protokite Playtest package's video encoder.

## [1.44.0]

### Added
- **`FlockClient.ServerSessionId`**: the id the server gave the current analytics session, or null until that session has
  reached the server (and once it has ended). `CurrentSessionId` still falls back to the local id; this one never does, so
  a service that must name the server's session can tell the two apart.

## [1.43.0]

### Added
- **`FlockClient.GetGameHeaders()`**: a copy of the headers that identify your game to another Qwacks service (your API
  key and Game Version ID). It never carries the player's sign-in.
- **`FlockClient.RetryPolicy`**: a copy of the retry settings the client was initialized with, so a service that calls its
  own API can retry the way the SDK does. Changing the copy changes nothing in the client.

## [1.42.0]

### Added
- **A Playtesting tab in Flock > Settings** that installs the Protokite Playtest package in one click: it downloads the
  version released with your Flock SDK and imports it, with no Git needed and however you installed Flock. Once
  installed, the tab opens its settings, warns when its version differs from Flock's (and updates it), and removes it.
  The SDK's runtime never depends on the playtest; only the editor tab knows it exists.
- **The Protokite Playtest package** ships in the same release, as its own `.unitypackage` and as a Package Manager
  package (`?path=/ProtokitePlaytest~`). This first version sets it up: settings with playtesting off, and a status
  that says what it is waiting for. See its own CHANGELOG.

### Fixed
- **Installing the SDK by git URL no longer warns that `Runtime/Docs` has no `.meta` file.** A `.gitignore` rule meant for a
  folder at the repository root matched that file too, and Package Manager leaves out ignored files when it installs a git
  package, so the folder's `.meta` never reached studios. The rule is anchored to the root, and CI now fails any tracked
  file an ignore rule matches.

## [1.41.0]

### Fixed
- **Games built with IL2CPP at Medium or High managed stripping can sign players in and send analytics again.**
  At those levels the linker removed what only the JSON serializer reaches: the constructors and property getters of
  the SDK's models. Every request reached the server without its fields (sign-in, session start and log events were
  refused), responses could not be read, and queued log events were dropped as refused. The SDK now keeps its own
  models in every player build, at every stripping level. Minimal and Low were never affected.
  - **Code generated with Codegen is kept too**, with nothing to regenerate: the classes under `Flock.Generated` are
    kept in whichever assembly they compile into, and the rest of your game is stripped as before.
  - This happens in the build itself. A `link.xml` shipped inside a package is not read by Unity's linker, so the SDK
    hands the linker its list on every player build instead.
- **A success with nothing in its body is now a success.** A `204 No Content`, or a `200` with an empty body, from a
  route that has nothing to return (analytics events, log events, transactions, ending a session) used to raise
  `FlockSerializationException: Empty response from server`, and a batch of queued events answered that way was
  deleted as unreadable. Reads that exist for their answer still fail on an empty body, as before.
- **Queued events are kept when the answer is not the server's.** A `200` whose body is not JSON (a captive portal's
  sign-in page, say) used to delete the batch it answered as unreadable, though the server never saw it. The batch now
  stays queued and goes out on a later flush.

### Added
- **`FlockHttpClient.PostAsync`, `PutAsync`, `PatchAsync` and `DeleteAsync` without a type argument**, for a route
  whose answer has nothing to read: a 2xx with no body or a JSON body is a success, a 204 included. A body that is
  not JSON raises `FlockSerializationException`, and every refusal is raised exactly as the reading overloads raise it.
- **`FlockProviderBase.ExecuteWithoutResultAsync`**, for a provider of your own: runs a call that returns nothing with
  the same retry, token refresh and error rules as `ExecuteAsync`.

## [1.40.0]

### Fixed
- **`Player.GetBanAsync` no longer throws for a player who is not banned.** The server answers a 2xx with
  `result: null` for anyone with no ban — the ordinary state of almost every player — and that was going
  through the shared null-result check and surfacing as
  `FlockNetworkException("Invalid response from server")`. The message compounded it: the caller was told
  the *network* had failed and the *server* had misbehaved, when the request had in fact succeeded.
  The call now returns an empty `PlayerBan` for that answer and **never returns null**.
- **Shipping a build with a new Game Version no longer discards the player's queued offline writes.** A
  write made with no reachable server is kept on disk and replayed when one comes back — but the queue
  was stored under the game version, and the SDK deletes every *other* version's stored data at startup.
  So a player who saved offline and then took an app update lost those writes: no error, no log. The
  queue now lives outside the version-scoped tree, where that cleanup cannot reach it.

  **A queue written by an older build is moved for you, once, on the next `FlockClient.Create`** — nothing
  is lost by upgrading. A write queued under an older version still routes and still addresses the right
  row; if its template changed in the meantime the server refuses the replay and the queue reports that
  and drops it, which is a far better outcome than deleting the write unasked.

### Added
- **`PlayerBan.IsBanned`** — the only test for whether a record describes a real ban, since `GetBanAsync`
  now always returns a record. **`PlayerBan.IsBannedFrom(feature)`** for the per-feature question, because
  a player can be banned from one feature and not others.
- **`FlockSnapshotStore.StateScope`**, the reserved place for things that are not re-fetchable. The
  offline cache is scoped by game version and pruned when that version changes, which is right for a
  cached response and wrong for anything the server has never seen. Anything written under this scope
  survives a version change.

### Notes
- **Cached answers behave exactly as before**: a new game version still drops the previous one's
  snapshots, so a running build picks up dashboard changes rather than serving stale content.

## [1.39.0]

### Changed
- The doc comment on `FlockClient.ApiVersion` no longer carries a maintenance note that was aimed at SDK maintainers rather than at callers. The const, its value and its behaviour are unchanged — only the text that shows in tooling differs.
- The assets guide drops a stale sentence about where uploaded content can be reused. The asset surface itself is unchanged.

## [1.38.0]

### Added
- **Eight error codes the backend can send but the SDK could not name.** `FlockErrorCode` had drifted behind the API, so these failures arrived as `Unknown` and read exactly like an error the server had chosen not to code. Five of them belong to the shop surface added in 1.36.0 — `ShopMalformedReward`, `ShopPackGrantsNothing`, `ShopRewardCurrencyNotHeld`, `PlayerInventoryAlreadyUsed` and `PlayerInventoryInventoryEntryNotFound` — so purchase and consume now have typed outcomes for their own failures. The rest are `LeaderboardNotFound` (an unknown board name, which had never had a code), `GameCommandRateLimited` and `AnalyticsInvalidCurrencyId`.
- **Every error code now carries a `Fix:` hint.** 24 codes had none, including all eight above and 16 that had shipped unhinted since 1.35.0 — among them `PlayerOauthFailed`, `PlayerInvalidRegistrationRequest`, `GameConfigInvalidTag` and the four "player not found" variants. Coverage is now complete: every member of `FlockErrorCode` has a hint except `Unknown`, which has nothing to advise.
- A test that walks `FlockErrorCode` and fails if any member is missing a hint, so a code added later cannot ship silently unhinted.

### Fixed
- `FlockErrorCodes.Parse` now resolves these eight codes to their typed member instead of falling through to `FlockErrorCode.Unknown`. Code that branches on `ErrorCode` sees the real outcome; callers matching on the raw `Code` string are unaffected.

## [1.37.0]

### Added
- **`GetScheduledAsync` reads the player's scheduled notifications from the server.** Until now the only list was local bookkeeping of what *this install* had scheduled, so a reinstall or a second device lost the handle and the player could not cancel their own reminders. The server route now exists and the SDK uses it. Filter by state with `ScheduledNotificationStatuses.Pending` / `Delivered` / `Canceled`; pending is the default.

### Changed
- **`CancelAllScheduledAsync` now cancels everything the *player* has pending, not just what this install tracked.** It reads the server list first and falls back to local bookkeeping only if that read fails, so an offline caller still cancels what it knows about.
- `GetPendingSchedules()` is unchanged and still local/no-network, but is now the fallback rather than the primary — [the guide](Docs~/notifications.md) says which to reach for.

### Fixed
- Schedule states are modelled as string constants rather than an enum, so a state added server-side later cannot break deserialization.

## [1.36.0]

### Added
- **Shop items can grant rewards, and the SDK now surfaces what was granted.** A purchase or a consume returns the rewards it handed out alongside the updated wallet, so a game can show "+500 Gold" without a follow-up read. `ShopItemReward` carries the reward `Type`, the currency `Code`, and the `Amount`; `ShopItem.Rewards` advertises what an item will grant before it is bought.
- `FlockShopProvider.ConsumeAsync(inventoryId)` consumes an owned inventory item and returns the updated row plus its granted rewards. Consuming moves currency, so it carries the same money safety as a purchase: an ambiguous failure surfaces instead of being re-sent, and only provably-unprocessed failures (408/429) retry.

### Changed
- **BREAKING: `PurchaseAsync` now returns `PurchaseResult` instead of `PlayerInventory`.** The inventory row moved to `result.Inventory`, joined by `PurchaseId`, `ItemType`, `Granted` and `Wallet`. The server had already moved to this shape, so the previous return type no longer matched the wire. Update call sites to read `result.Inventory` where they used the returned value directly.
- Reward types are modelled as strings against `ShopItemRewardTypes.Currency`, not a C# enum — the API stores rewards as typed entries so new kinds can ship without a schema migration, and an unknown value must not break deserialization.
- `Granted` and `Rewards` default to empty lists rather than null, so callers can iterate without a null check on items that grant nothing.

### Fixed
- **`GetPlayerInventoryAsync` works again** — the backend route that returned HTTP 500 unconditionally has been fixed server-side and verified returning 200. The 1.35.0 caveat in [Shop](Docs~/shop.md) has been removed. The SDK call was correct throughout and is unchanged; only the documentation moved.

## [1.35.0]

### Changed
- **Errors now say what went wrong and what to do about it.** A failed call used to surface as `Validation failed (HTTP 400)` — a message that named neither the cause nor the fix, with the server's own explanation buried in the raw response body. `FlockException.Message` is now composed from the call that failed, the server's reason, the coded identifier, and an SDK-authored next step. A device login against an unregistered device reads:

  ```
  Device login failed: Invalid login credentials [player.invalid_login_credentials, HTTP 400]
  Fix: This device is not registered yet. Call Authentication.RegisterWithDeviceAsync(deviceId) once to create the account, then Authentication.LoginWithDeviceAsync(deviceId) on later launches.
  ```

  Client-side throws (`"deviceId cannot be null or empty"`) are unaffected — with no status, code or server reason to add, the message stays exactly as it was.
- **FastAPI's own 422 field errors are now readable.** Those responses put an array in `detail`, which the coded-error parser could not read, so the SDK reported a bare `Validation failed` with no reason at all. They now surface as `body.player_data: Input should be a valid dictionary`, naming the offending field.

### Added
- **`FlockException.ServerMessage`, `.Hint` and `.Operation`.** The server's human reason, the SDK's suggested next step, and a label for the call that failed. All three feed `Message`; `Body`, `StatusCode`, `Code` and `ErrorCode` are unchanged.
- **`FlockErrorHints`** — the `FlockErrorCode`→next-step table behind `Hint`, public so callers can reuse the same wording in their own UI. Hints are keyed on the error code, never on message text. `FlockErrorHints.ForAuth` additionally takes the credential, because one code means different things per method: `player.invalid_login_credentials` means "register this device first" for device login but "wrong password" for email.
- **Dashboard-authored content now points at the sync step.** A missing template, achievement, currency or config reports that it must be authored in the dashboard *and* that `Flock > Settings > Codegen > Sync` has to run afterwards — the sequence newcomers miss.
- **`FlockErrorMessageTests`** — 10 EditMode tests locking the composed message, the hint table, the credential-specific auth hints, FastAPI field-error parsing, and that a client-side throw's text is untouched.
- **A Console hint when a compile error looks like un-synced schemas.** Calling a generated accessor before codegen has run is a *compile* error (`'PlayerProvider' does not contain a definition for 'GetPlayerProgressAsync'`), so no SDK exception can ever fire for it — the SDK now watches compilation instead and says what the compiler can't:

  ```
  [Flock] Codegen has never run in this project, and these compile errors look like generated code that doesn't exist yet:
    - PlayerProvider.GetPlayerProgressAsync

  Typed accessors like these are generated from the schemas you author in the Flock dashboard — they do not exist in the SDK until codegen runs.
  Fix: author the schema in the Flock dashboard, then run Flock > Settings > Codegen > Sync Schemas.
  ```

  It recognises unresolved members on the four providers codegen extends, the `Flock.Generated` namespaces, and the generated id types — and stays silent on ordinary compile errors. Once codegen *has* run, the wording changes to ask for a re-sync and names the game version it last synced for. Toggle in **Flock > Settings > Codegen > Status**; stored per machine in `EditorPrefs`.
- **A Status card on the Codegen tab** — says plainly when codegen has never run (and that missing accessors fail at compile time, not runtime), when the generated code was synced for a different game version than the project targets, and otherwise which version it matches.
- **`FlockCodegenHintTests`** — 14 EditMode tests over real Roslyn error text: what the classifier recognises, what it ignores, both hint wordings, per-pass de-duplication, and that the compilation hook is actually subscribed.

### Known limitation
- **The Console hint fires on recompiles inside a running Editor, not on a cold start.** Unity compiles before `[InitializeOnLoad]` can subscribe, so errors that already exist when the Editor opens produce no hint. That is the case the Codegen tab's Status card covers.

### Documentation
- [Error handling](Docs~/errors.md): documents `ServerMessage`, `Hint` and `Operation`, what the composed `Message` looks like, that a client-side throw's text is untouched, and `FlockErrorHints.For` — with the caveat that `Hint` is written for developers, not players.
- **Corrected a `Docs~/errors.md` example that could never fire.** It showed catching `ex.IsAlreadyRegistered()` around `RegisterWithEmailAsync`, but `RegisterWith*` *swallows* those codes and returns `null` rather than throwing, so the catch was unreachable. It now shows the `null`-check-then-login pattern, and notes that a successful register does sign the player in — so the login call is only needed on the `null` path. `IsAlreadyRegistered()` is still documented for calls that don't swallow, such as `Link*Async`.
- README "Error handling" shows the composed message and states the branch-on-`ErrorCode`-not-text rule.
- **Swept every `Docs~/` guide for examples that can't run**, after the one above. Checked mechanically: every `*Async` and `Flock*` identifier against the real public surface, every call's argument count against the real overloads, every cited editor menu path against the actual `MenuItem` attributes, and every `catch` against whether that call can throw what it catches. All clean except one:
- **[Shop](Docs~/shop.md) documented `GetPlayerInventoryAsync` as a working call.** `GET /v1/player_inventory/player/{id}` returns **HTTP 500 unconditionally** — a backend fault, verified against a live server, with the SDK call itself correct. The guide now says so and points at the workarounds (read the `PlayerInventory` a purchase returns, or track ownership in player data). Nothing about the SDK changed.

## [1.34.0]

### Fixed
- **An offline first launch pinned the asset catalog for the whole process.** `FetchAllAssetsAsync`'s offline branch returns before its first `await`, so the method ran synchronously and its `finally` cleared the in-flight field *before* `GetAllAsync` assigned it — leaving a completed task pinned and every later read served from it, reconnection or not. `ClearCache()` reset the flags but not that field, so the documented recovery lever did not work either. The same latent shape in `PlayerProvider` and `FlockConfigProvider` is fixed alongside it.
- **One unparseable response wedged the offline write queue permanently.** A malformed or empty 2xx — a captive-portal login page is the everyday case — produced an error the queue classifier read as transient, so the write parked at the head of the queue, was re-sent on every flush trigger for the life of the app, and blocked every write behind it across relaunches, with no public API to clear it. The classifier now treats an unparseable body as permanent (matching `RetryPolicy`, which already did — the two layers disagreed) and distinguishes a recoverable 401 from an authoritative 403. A generous per-write attempt cap backstops anything the classifier still misreads, so no single write can block the queue forever.
- **`FlockEvents.ClearAll()` cleared 16 of its 17 events**, leaking `OnNotificationReceived` across `Shutdown()` and across every play session when Domain Reload is disabled — the delegate kept a destroyed object and the next invoke threw from inside the SDK.
- **Two codegen emitters were non-deterministic.** `PlayerAccessorEmitter` and `ConfigAccessorEmitter` iterated the raw server response while every sibling sorted, so identical schema content could emit byte-different files. Worse, with two names that sanitize alike, `UnDuplicate` assigns `Foo` and `Foo_2` in iteration order — a reorder silently rebound an accessor to a different template, and `ContentHash` could not see it because the hasher sorts, so `Verify` reported no drift.
- **A failed session-end spool lost the session and logged success.** `End()` clears the live marker on the invariant that the handler persisted the end durably; the handler discarded the value telling it whether that worked and logged "Session end spooled" either way. On a disk or permissions failure the session vanished from both places at once. The marker is now kept so the next launch recovers it.
- **Withdrawn analytics consent did not stop transmission of already-queued data.** Every ingress was consent-gated but the flush was not, so revoking consent and signing in again shipped everything spooled beforehand. Egress is now gated too. Nothing is deleted — queued records stay on disk and deliver normally if consent is granted again.
- **`Notification.ClearCache()` destroyed every other player's pending schedules.** The snapshot scope is shared across players on a device; the method deleted it wholesale and restored only the signed-in player's rows, orphaning reminders that still fire server-side and can no longer be cancelled. It now preserves every player's schedules and watermarks, which also makes it safe to call signed out.

### Added
- **EditMode tests covering the fixes above.** Most fail against the previous code; the successful-spool case is a non-regression guard rather than a mechanism test: the asset catalog refetching after an offline launch; an unparseable 2xx dropping instead of wedging the write queue; a 403 dropping where a 401 is kept; the attempt cap draining a permanently-stuck queue; `ClearAll` dropping every event (asserted by reflecting over the hub, so a future event is covered automatically) plus a count guard that fails when an event is added; a failed session-end spool leaving a recoverable marker and a successful one clearing it; and one player's pending reminders surviving another player clearing cache.

### Notes
- **Consent egress gating is implemented but not yet locked by a test.** `FlockAnalyticsConsentTests` runs on a fake that records nothing, so asserting "no request was sent" needs that suite moved onto the shared recording transport first.
- The write-queue attempt cap is a backstop, not the primary policy — the classifier fix handles the known causes. It is set generously because dropping a queued write is data loss, so a genuine multi-day outage must not trigger it.

## [1.33.0]

### Fixed
- **Leaderboard reads addressed routes that do not exist — `GetStandingsAsync`, `GetMyRankAsync` and `GetAroundMeAsync` could not succeed against any backend.** Each resolved the board name to an ID and then requested `/v1/leaderboard/{id}`, `/{id}/me` or `/{id}/around-me`; the `/v1` surface has no by-id read routes, so every call 404'd (confirmed against a live backend, which answers 404 on all three and serves the by-name equivalents). All three now call `/v1/leaderboard/by-name/{name}/standings`, `/me` and `/around-me` directly, and the whole read surface is verified end-to-end against a live backend — standings, my-rank, around-me and the unknown-name rejection all returning real data. Query parameters, response envelopes and every public signature are unchanged — including `FlockValidationException` for a name this game doesn't have, which now comes from the read's own 404 instead of a preceding lookup. Reads no longer spend a name→ID round trip at all, so the first read of a session is one call rather than two.
- **The refresh token was serialized into the log on every refresh.** `Debug.Log` reaches `Player.log`, logcat, and any crash reporter subscribed to `Application.logMessageReceived`, so a long-lived credential was leaving the device in plaintext — and since `Logout()` is local-only by design, it stayed valid. The refresh log line now carries the URL and player ID only.
- **Errors and warnings were silenced in every default build.** `EnableDebugLogs` defaults to false and selected a logger that no-oped `LogError`, `LogWarning` and `LogException` as well as debug output, so failures like a token store rejected by an invalidated Android Keystore, a failed refresh, or analytics failing to initialize reported nothing at all. Severity is now separate from verbosity: errors, warnings and exceptions always reach the console, and `EnableDebugLogs` adds info and debug on top. `NullFlockLogger` is unchanged and still available for total silence.
- **The package used editor APIs newer than the Unity version it declares support for.** `Object.FindAnyObjectByType` (2021.3.18+) and `EditorStyles.linkLabel` were referenced while `package.json` declares `2020.3`, so `Flock.Editor` failed to compile for anyone on an older editor — taking **Flock > Settings** down with it. Both now go through `FlockEditorCompat`, which selects the available API.

### Added
- **EditMode test `Endpoints_MatchTheLiveV1Paths`** asserts the four leaderboard paths as literal strings. Every other route test stubs the fake transport with the same `FlockEndpoints` helper the provider builds its URL from, so the expectation is derived from the code under test and a wrong path is invisible to it — which is how the by-id routing above survived a green suite.
- **`FlockLoggerTests`** pins the logging contract that had none. `DebugLogsDisabled_StillSelectsAReportingLogger` is the guard that matters: it fails if the default config ever again selects a logger that swallows errors. The rest bracket it — `LogAssert.Expect` proving the quiet logger's error and warning paths are not no-ops, direct `Application.logMessageReceived` capture proving info and debug really are suppressed rather than merely not throwing, and `NullFlockLogger` pinned as a deliberate total-silence opt-out so it is not "fixed" into reporting.

## [1.32.0]

### Added
- **Notification template catalog.** `FlockClient.Instance.Notification` can now read the templates a game has authored: `GetTemplatesAsync()` for the active catalog, `GetTemplateByNameAsync(name, locale)` for one, and `ResolveTemplateIdAsync(name, locale)` when you want the raw ID for logging or a deep link. `NotificationTemplate` carries `Id`, `Name` and `Category` — authoring content (title, body, data) stays server-side. See the [Notifications guide](Docs~/notifications.md).
- **Both template reads are game-scoped, not player-scoped.** They authenticate with the API key alone, so unlike everything else on this provider they work signed out, and they cache per game rather than per player.
- **`PendingSchedule.TemplateName`**, so a locally tracked reminder can be identified by the name it was scheduled with. `TemplateId` remains, now carrying the ID that name resolved to.
- **`FlockErrorCode.NotificationTemplateNotFound`** (`notification_template.not_found`), returned when the game has no active template of that name.
- **`FlockEvents.OnNotificationReceived`** (`Action<Notification>`) — raised for each notification the SDK hasn't surfaced before, seen during `GetInboxAsync` or `GetSummaryAsync`, once each and **oldest first**. Not a poller: it rides the fetches the game already makes and adds no traffic, because there is no realtime channel and the SDK deliberately doesn't poll. The **first fetch for a player is silent**, seeding a watermark so an existing inbox doesn't arrive as a burst on launch. One event covers schedules, triggers and campaigns alike — the payload already separates them (`CampaignId`, or `trigger_id` in `Data`), so three events would triple the surface to express what one `if` answers.
- **The seen-watermark is player-scoped state, not cache** — it survives `ClearCache()` alongside the pending-schedule list, so dropping cache can't replay notifications the game already handled.
- **EditMode tests**: the resolved ID — never the name — reaching `template_id`, the name escaped into the query rather than a path segment, `locale` forwarded only when supplied, the session memo collapsing repeated lookups into one round trip, an unknown name failing before anything is scheduled, and the catalog read working signed out. Plus the received-event contract: the silent first fetch, one raise per new notification, no re-raise on refetch, oldest-first ordering, the summary read raising it too, and the watermark surviving `ClearCache()`.
- **`Docs~/events.md` gained a Notifications section.** It documented neither notification event before — `OnUnreadCountChanged` had shipped in 1.29.0 undocumented there.

### Changed
- **BREAKING: `ScheduleAsync` now takes the template's *name*, not its ID.** The parameter is still a string in the same position, so existing calls keep compiling and fail at runtime instead — a call that passes an ID now raises `notification_template.not_found`. Swap the ID for the template's dashboard name. This retires the documented limitation that IDs had to be obtained out-of-band: `/v1` had no route exposing them until now, which is why the surface shipped ID-based in 1.29.0.
- **`ScheduleAsync` gained an optional `locale`** — the `by-name` route's own query parameter, threaded through so a name-only surface can still reach a specific localisation. It sits after `channels` and before the `CancellationToken`; a caller that passed a `CancellationToken` positionally into that slot gets a compile error, not a silent behaviour change.
- **`ClearCache()` now also drops the template memo.** Pending schedules still survive it.

### Notes
- Resolution costs one extra round trip the first time a name is used and is memoized for the rest of the session, so scheduling the same template repeatedly does not pay it again. The lookup runs after the empty-name and signed-in guards, so neither spends a request.
- A template may exist in several locales under one name; the server prefers English and falls back to the first locale on file. The client projection carries no `locale` field, so `GetTemplatesAsync` returns one entry per locale sharing a name and differing only by ID — use `GetTemplateByNameAsync(name, locale)` when that matters.

## [1.31.0]

### Added
- **Account linking.** `FlockClient.Instance.Authentication` can now attach extra credentials to the signed-in player — the "I played as a guest, now let me keep my progress" flow: `LinkEmailAsync`, `LinkDeviceAsync`, `LinkGoogleAsync`, `LinkAppleAsync`, `LinkSteamAsync`, `LinkFacebookAsync`, `LinkDiscordAsync`, plus `UnlinkAsync(FlockCredentialProvider)` and `GetLinkedAccountsAsync()`. See the [Authentication guide](Docs~/authentication.md).
- **Every call returns the player's full updated credential list**, so a link or unlink doubles as a refresh. `PlayerLinkedAccount` carries the provider, the provider-side user id, the email, and `EmailVerified` — plus `ProviderType`, a typed view you can hand straight to `UnlinkAsync`.
- **`FlockEvents.OnAccountLinked` / `OnAccountUnlinked`.** `Action<FlockCredentialProvider>`, raised only on success — one place to refresh an account-settings screen.
- **Four new `FlockErrorCode` members**: `PlayerAccountAlreadyLinked`, `PlayerAccountNotLinked`, `PlayerCannotUnlinkLastCredential`, `PlayerInvalidLinkRequest`. The two worth handling are the credential already belonging to another player, and the server refusing to remove a player's last remaining way back in.
- **EditMode tests**: exact request-body shape per route (including the OAuth routes' bare `token`, which is *not* the login routes' `id_token`/`session_ticket`), the `device_id` wire segment, the root — not enveloped — response contract, the bearer-only guards firing before any request, the events, the coded-error mapping, and the password-reset gate opening and re-closing.

### Changed
- **`ResetPasswordAsync` now accepts a linked email**, not only an email sign-in. It previously required `_currentAuthMethod == Email` and threw for a device or social session even when the account had an email credential the server would have honoured. The check is scoped to the current session and never persisted — it resets on logout and on every new session (login *or* restore), so one player's linked email can't leak into the next player's session on a shared device. After a restore the SDK doesn't know what's linked until you call `GetLinkedAccountsAsync()`. This only widens the gate — nothing that worked before now throws.

### Notes
- Credential state is **never cached** — same rule as bans and inventory — and linking is **never queued offline**: a link that may have landed is never re-sent, since the retry would come back as `account_already_linked`.
- There is no account-merge flow. When a credential already belongs to another player the server returns `account_already_linked` and the choice (sign in as that player instead, or cancel) belongs to your game.
- The unversioned `POST /player/{player_id}/link-oauth/{login_type}` route is superseded by `/v1/player/link/oauth/{provider}` and is deliberately not called.

### Fixed
- **Package Builder crash on the provider-stripping UI.** The Leaderboard manifest entry had been fused with the Notification entry and left `DependsOn` null, which the dependency walk dereferences unguarded.

## [1.30.0]

### Added
- **Leaderboards.** `FlockClient.Instance.Leaderboard` reads standings, the signed-in player's rank, and an around-me slice, all addressed by **board name**: `GetByNameAsync`, `GetStandingsAsync`, `GetMyRankAsync`, `GetAroundMeAsync`, `ResolveIdAsync`. There is no score-submit call by design — a board projects over a player-data field, so writing that field through the command surface is what moves a player. See the [Leaderboards guide](Docs~/leaderboards.md).
- **Board configuration and formatting helpers.** `GetByNameAsync` returns the board's `ValueType` / `Direction` / `Aggregation` / `WindowType` / `Scope` as typed enums, plus `IsHigherBetter` (high-score vs best-time) and `FormatScore(double?)`, which renders a score the way the board measures it — including `m:ss.fff` for duration boards, whose scores are **seconds** (hence `FlockLeaderboardValueType.DurationSeconds`). A null score, meaning an unranked player, formats as an empty string.
- **Window selection.** `FlockLeaderboardWindow.Current` (default, the board's live window), `.Season("id")` for a finished season, and `.Period("2026-W31")` for a raw period key. The window is a *key*, not the board's `WindowType`.
- **EditMode tests**: envelope handling on all four routes, query/window/paging wire shape, name encoding, resolve memoization, the bearer-only guards firing before any request, unknown-name short-circuit, offline relaunch (resolve *and* data served from snapshots), per-player rank scoping, and the formatting helpers.

### Notes
- Reads are snapshot-cached like the rest of the SDK, with my-rank/around-me scoped per player. The first read of a board costs two calls (name lookup + data) and later reads cost one; `Leaderboard.ClearCache()` drops both layers.

## [1.29.0]

### Added
- **Notification inbox.** `FlockClient.Instance.Notification` exposes `GetInboxAsync`, `GetUnreadCountAsync`, `GetSummaryAsync`, `MarkReadAsync` and `MarkAllReadAsync`. Reads are snapshot-cached per player, so one player's inbox is never served to the next on a shared device. See the [Notifications guide](Docs~/notifications.md).
- **`FlockEvents.OnUnreadCountChanged`.** Drives a mailbox badge without any polling: the SDK deliberately runs no background loop (these calls are metered), so the event fires whenever a call returns a server-reported count, and only when the value changes.
- **Notification scheduling.** `ScheduleAsync` asks the server to deliver a dashboard-authored template to the signed-in player later — by `TimeSpan` delay or an absolute `DateTime` — with `CancelScheduledAsync` to undo it. Channels are a `[Flags]` enum (`FlockNotificationChannels`); leaving it `None` uses the template's own channels.
- **Pending-schedule tracking.** `GetPendingSchedules()` and `CancelAllScheduledAsync()`. The id returned by `ScheduleAsync` is the only handle on a pending notification and the API has no route to list them, so the SDK persists its own record (player-scoped, dropped once the delivery time passes, preserved across `ClearCache()`).
- **Typed accessors for a notification's `data` payload.** `TryGetData<T>`, `GetData<T>` and `GetDataAs<T>` on `Notification`. JSON round-trips into `Dictionary<string, object>` as `long` for whole numbers, `double` for decimals and `JObject`/`JArray` for nested values, so a direct `(int)` cast throws on an ordinary payload and nested access leaks `Newtonsoft.Json.Linq` into consumer code. These normalise it and never throw — a missing key, null `data`, or an unconvertible value returns the fallback.
- **One-call push registration on iOS.** `RegisterThisDeviceAsync()` requests permission, registers with APNs, obtains the token and sends it — no token argument. Enabled by installing `com.unity.mobile.notifications`; the SDK's asmdef carries a `versionDefines` entry so the define appears automatically and consumers without the package are unaffected. It stays an **optional** dependency: the SDK still ships with `com.unity.nuget.newtonsoft-json` as its only hard requirement. Android can't work this way — an FCM token comes from Google's messaging client, which ships as the Firebase SDK and cannot be a package dependency — so there it throws with that explanation and you use the token overload.
- **Push device-token registration.** `RegisterDeviceTokenAsync` and `UnregisterDeviceTokenAsync`. The platform is detected from the running build, with an explicit `FlockDevicePlatform` overload when the token comes from elsewhere. On a platform the push backend has no value for — PC, Mac, Linux, console, and the Editor — registration **throws instead of guessing**, because a token filed under the wrong platform fails silently at delivery time rather than at the call site. The SDK cannot obtain the token itself (Unity has no first-party FCM/APNs): get it from Firebase or `com.unity.mobile.notifications` and pass it in. Logout does not unregister — `Logout()` is local-only by design, so call `UnregisterDeviceTokenAsync` first on a shared device.

### Fixed
- **Money-safety retry tests could not fail.** The shared test harness pins `MaxRetries = 0`, so `Purchase_ServerError_NotRetried_Throws` held regardless of the `idempotent:` flag it claimed to verify. Those tests now raise the retry cap through the existing config hook and are paired with a 408 contrast case (provably-unprocessed failures *do* retry), which is what proves the assertion measures idempotency. `AddGameFunds` had no non-retry test at all and now has both halves. No runtime change.

### Known limitations
- **`ScheduleAsync` requires the notification template's ID, not its name** — a name returns `404 "Notification template not found"`. No `/v1` route exposes template IDs and the dashboard does not display them, so the ID must be obtained out-of-band. Backend gap, tracked separately.
- **Push delivery exists server-side** (confirmed 2026-08-05), but no push has yet been observed landing on a physical device from this SDK. The registration call is wired; obtaining the token is still the game's job (Firebase on Android, `com.unity.mobile.notifications` on iOS).
- **Desktop cannot receive push at all**, and no backend change can fix it: push requires an OS-level service (FCM / APNs / Web Push), and Windows standalone, Linux, and console have none reachable from a generic backend. The API's platforms are `android`, `ios`, `web`. On desktop the inbox and email are the only delivery paths.


## [1.28.0]

### Fixed
- **WebGL: a stray space in the configured API URL no longer breaks all requests.** `FlockInitConfig` now trims `apiUrl`. A leading space made WebGL's `UnityWebRequest` fail its `http://` absolute-URL check, so calls resolved relative to the page origin and 404'd against the host server. `HttpClient` on Editor/standalone tolerated it, so it only surfaced in WebGL builds. One-line guard at the single config chokepoint — no API change.

### Changed
- **"Game ID" → "Game Name" in user-facing copy.** Config tooltip and validation message, the editor window, the bootstrap log line, the README, and the in-editor guide now read "Game Name" (the value is the game's dashboard name). Label and documentation only — the `gameId` field and public API are unchanged (non-breaking).

### Added
- **Command offline-queue tests.** EditMode: `update_player_data` offline replay serializes `data` as an object (not the `DataField` array), and repeated offline writes to the same field are last-write-wins in replay order. PlayMode `FlockCommandConcurrencyTests`: the flush single-flight guard makes a second flush invoked during an in-flight POST a no-op, so the queue is never double-POSTed.

## [1.27.0]

### Fixed
- **`Commands.UpdatePlayerDataAsync` no longer fails with HTTP 422.** The update payload's `data` was serialized as the internal `DataField` descriptor array rather than the flat `{field: value}` object the backend requires — a regression from the codegen-command rework that retyped the request model to `List<DataField>`. It now flattens to the expected object, so the live call *and* the offline-queued replay send the correct shape. No API change: `UpdatePlayerDataAsync(playerDataId, List<DataField>)` and the generated typed `UpdateAsync()` accessors are unchanged; only the wire payload is corrected.

### Added
- **Per-feature EditMode/PlayMode test suite.** Catalog-driven coverage across the SDK surface (auth/init guards, player-data reads, game/config, shop, offline assets, snapshot store, retry, 401→refresh, command offline-queue, session snapshot) on a shared `Flock.Tests.Support` harness (`FlockFakeTransport` + `FlockTestClient`), with a PlayMode assembly for the concurrency cases. Includes a wire-shape guard asserting the `update_player_data` payload serializes as an object, locking in the fix above.

## [1.26.0]

### Added
- **Complete coded-error coverage.** `FlockErrorCode` now mirrors the backend OpenAPI `detail.code` set (57 codes). Added the five previously-missing player codes: `player.invalid_reset_code`, `player.invalid_verification_code`, `player.no_email_account`, `player.player_not_found`, and the now-shipped name conflict. Every server error carries a typed `FlockException.ErrorCode` — catch and branch on it (e.g. `catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.ShopInsufficientFunds)`).
- **`FlockException.IsAlreadyRegistered()` extension.** A readable check for the register/login "this identity already belongs to an account" group (email/device/OAuth). A taken display name is deliberately excluded — it's a different fix.
- **EditMode tests**: name-conflict pipeline path (`player.name_already_registered` → `FlockValidationException`) and direct coverage for the `IsAlreadyRegistered()` grouping.

### Changed
- **Name conflict is now a coded error.** The provisional `FlockErrorCode.PlayerNameAlreadyTaken` is replaced by the shipped `FlockErrorCode.PlayerNameAlreadyRegistered` (HTTP 400 → `FlockValidationException`). Registering with a taken `name` throws with this code instead of the earlier unhandled `500`; catch it to prompt for another name. Migration: if you referenced `PlayerNameAlreadyTaken`, switch to `PlayerNameAlreadyRegistered`.
- `FlockAuthProvider`'s internal already-registered check now delegates to the shared `IsAlreadyRegistered()` extension (one source of truth).

### Documentation
- New [Error handling](Docs~/errors.md) guide: the `FlockException` hierarchy, `.Code`/`.ErrorCode`, the full `FlockErrorCode` list, and the throw-vs-branch pattern. Linked from the README feature guides.
- README gains a short "Error handling" section.
- [Authentication guide](Docs~/authentication.md): the `name` registration note now documents catching `PlayerNameAlreadyRegistered`.
- [ARCHITECTURE.md](ARCHITECTURE.md): removed the resolved name-collision entry from the backend-backlog list.

## [1.25.0]

### Added
- **Password reset.** `Authentication.ForgotPasswordAsync(email)` emails a reset code (the backend always reports success, so account existence is never revealed); `ResetPasswordAsync(email, code, newPassword)` sets the new password and throws on a bad or expired code.
- **Email verification.** `Authentication.SendEmailVerificationAsync()` emails a verification code; `VerifyEmailAsync(code)` marks the address verified. Neither enforces a sign-in client-side (the spec marks the bearer token optional on both routes; it's attached automatically when a player is signed in). Note: the backend does not yet expose a readable "is verified" flag, so verification can't be queried back — tracked as a backend gap.
- **Server-side token revocation.** `Authentication.RevokeTokenAsync()` kills the signed-in player's refresh token on the server (logout hardening / stolen-token response); already-issued access tokens live out their TTL. `Logout()` stays local-only, matching the Firebase/PlayFab/UGS convention — compose `await RevokeTokenAsync(); Logout();` for a full sign-out.
- **Name preflight.** `Authentication.IsNameAvailableAsync(name)` checks display-name availability before registering (advisory — a race can still lose at register time).
- **EditMode tests**: `FlockAuthEndpointsTests` (endpoint path constants incl. offline-replay paths and query escaping, request/response wire contracts for the new auth calls); `FlockAuthProviderTests` (behavioral coverage through the real client with a scripted fake transport — the full login → get data → logout → re-login → get data lifecycle, silent 401→refresh→retry, session restore incl. expired-token refresh and relaunch persistence, already-registered swallow, logout event/persistence semantics, email-gated password reset, revoke confirmation/fail-fast, and the Facebook/Discord `login_type` literals).

### Changed
- **Endpoint paths centralized.** Every relative API path the SDK calls now lives in the internal `FlockEndpoints` class (`Runtime/Http/FlockEndpoints.cs`) instead of ~55 scattered string literals — one place to view or diff the wire surface against backend spec updates. Pure refactor; no wire behavior change. Codegen-generated command paths remain dynamic by design.

## [1.24.0]

### Added
- **Unexpected-termination detection.** If the previous run died without a clean quit (crash, hang force-kill, foreground OOM, power loss), the SDK detects it on the next launch and queues one `app_termination` analytics event: `previous_session_id`, `classification` (`background_kill` = died while backgrounded — OS eviction / swipe-close; `abnormal` = died foregrounded without Unity's quit path), `last_alive_at`, `unhandled_exception_count` (context only — an unhandled managed exception does not crash a Unity app, so it never drives classification), `app_version`, `sdk_version`. Implemented as a tombstone marker in PlayerPrefs owned by the new internal `FlockTerminationTracker` — `FlockSession` and its wire payloads are untouched. Alt-F4 / window close is a clean exit (no event); mobile swipe-close reports `background_kill` (the app switcher backgrounds the app first). Requires `PersistSessionOnDisk`; disabled in the Editor and on WebGL; consent-gated like all analytics. The marker clears only after the event's durable enqueue, so a failed write retries next launch.
- **`Analytics.FlushAsync(ct)`.** Awaitable drain of everything queued (session ends first, then events, then logs) for the rare "make sure it landed before X" moment — the one real await in the tracking surface. Automatic flushing (interval/pause/session end/login) makes calling it optional. Transient failures keep records queued; never throws.
- **EditMode tests**: `FlockTerminationTrackerTests` (classifier matrix, marker round-trip, malformed-marker tolerance, lifecycle persistence); `FlockAnalyticsConsentTests` gained `FlushAsync_NoConsent_DoesNotThrow` and moved the log-method assertions to the new sync API.

### Changed
- **BREAKING: enqueue-only log APIs are now synchronous.** `LogExceptionAsync` (both overloads) → `LogException`, `LogErrorAsync` → `LogError`, `LogEventAsync` → `LogEvent` — all `void`, `CancellationToken` parameters removed. These methods only ever wrote to the on-disk queue; the old `await` resolved on enqueue, not on delivery, which read as a false server acknowledgment. This matches the fire-and-forget convention of GameAnalytics / Firebase / Unity Analytics; delivery still happens on the flush triggers, and `FlushAsync` is the explicit await when delivery matters. Migration: drop the `await` and the `Async` suffix at each call site.
- The internal tracking path follows suit: `TrackEventAsync` → private `TrackEvent` (returns the cache enqueue handle), `EnqueueAndSendLogAsync` → `EnqueueLog`, and the global Unity exception handler is a plain sync handler instead of `async void`.

### Fixed
- **Version drift.** `package.json` stayed at `1.19.0` while releases `1.20.0`–`1.23.0` shipped (changelog numbering was correct; the manifest bump was skipped four times). Realigned: `package.json` and `FlockSdkVersion.Current` now both say `1.24.0`. The bump-together-in-one-commit rule now genuinely includes the changelog entry as the third leg.

### Documentation
- README Analytics section rewritten around the real public surface: sync log examples, the new `FlushAsync`, and a new "Unexpected-termination detection" subsection. Removed `TrackEventAsync`/`TrackEventsAsync` examples — those methods were never public (commented out in `IAnalyticProvider` pending the analytics cleanup), so the examples couldn't compile for a consumer.
- [ARCHITECTURE.md](ARCHITECTURE.md): `FlockTerminationTracker` / `FlockTerminationMarker` entries under Runtime/Analytics.
- QuickStart sample and its README updated to the sync `LogEvent` (status line now says "queued", which is the truth the old await obscured).

## [1.23.0]

### Added
- **Analytics consent gate.** `FlockClient.Instance.Analytics` gains `HasConsent`, `SetConsent(bool)`, and `EraseLocalAnalyticsData()`. The switch gates session lifecycle (device/FPS/screen-view capture), behavioral event tracking, and log/crash events (`LogExceptionAsync`, `LogErrorAsync`, `LogEventAsync`) — all carry player-identifiable data. `RecordTransactionAsync` is the one exception, unaffected by consent, since purchase records typically need financial/tax retention independent of tracking consent. `FlockAnalyticsConfig.RequireExplicitConsent` (default `false`) switches between today's opt-out behavior (collection runs once authenticated, unchanged for existing integrations) and a real opt-in gate where no session/event/log tracking starts until the game calls `SetConsent(true)`. The decision persists across launches. New `FlockEvents.OnConsentChanged` event. `EraseLocalAnalyticsData()` is local-only — it clears events, session-end records, and log/crash events queued on-device but not yet sent; there's no backend endpoint yet to delete analytics already ingested by the server. Editor: new checkbox in Qwacks > Flock's Analytics section.
- `FlockSession.Discard()` — internal session-stop path used by consent revoke; stops the session without spooling a final session-end record (distinct from `End()`/`Reset()`, which do).
- **EditMode tests**: `FlockConsentStoreTests`, `FlockEventCacheClearTests` (first coverage for the existing, previously-unused `IEventCache<T>.Clear()`), `FlockSessionDiscardTests`, `FlockAnalyticsConsentTests`.

### Fixed
- `FlockBehaviour.Instance` called `DontDestroyOnLoad` unconditionally, which throws outside Play Mode — guarded with `Application.isPlaying`. Unmasked by the first EditMode tests to construct a live session; no behavior change in Play Mode.

### Documentation
- README: new "Consent" subsection under Analytics.

## [1.22.0]

### Added
- **Achievement codegen.** Sync now generates `Flock.Generated.Achievements.FlockAchievementId` — an enum of the fields on the player template tagged `achievement` (each member carries a `/// <summary>raw_name</summary>` doc) — plus an **additive** `UnlockAchievementAsync(FlockAchievementId)` extension on `FlockCommandProvider` that resolves the achievements row and maps the enum back to the raw `achievement_name`. The raw-string overload stays public (same additive pattern as the shop `FlockFundId` methods); the typed one is the recommended, typo-proof path. No new endpoint — achievements come from the player-template fetch already in the sync.
- **Achievement details config.** A game config tagged `achievement` (a list of entries, each with a `name`) wires to the enum: the entry's `name` is generated as `FlockAchievementId` via a generated `FlockAchievementIdConverter`, and a `GetAchievementDetailsAsync(FlockAchievementId)` extension on `FlockConfigProvider` returns the matching entry. (The canonical `/achievement` resource is OAuth2/dashboard-only, so codegen sources details from a game config it can reach with the API key.)
- **Catalog achievements section.** `FlockContentCatalog` gained an `achievements` list (name + type), surfaced as its own foldout in the inspector, so designers can see the available `FlockAchievementId` members without reading generated C#. The `achievement`-tagged template is no longer also duplicated under Player Templates.
- **`SchemasManifest.AchievementCount`** const, alongside the existing template/config/shop counts.

### Changed
- **Cache normalization in `FlockConfigProvider` / `FlockShopProvider`.** Each now keeps one canonical id-keyed dictionary per entity (configs, patches, shops, shop items); by-name/by-tag/by-schema/by-version lookups are id indexes into that store instead of separate copies of the full objects. A side effect: fetching by one key now warms the others for free (e.g. `GetGameConfigsAsync` also satisfies a later `GetConfigByIdAsync` for the same id without a new request), and list-returning methods always hand back a fresh `List<T>` instead of the cached reference.
- **`FlockProviderBase` snapshot-scope helpers.** `FetchWithSnapshotAsync(category, ...)` now resolves `{GameVersionId}/{category}` internally instead of every call site spelling out `GetSnapshotScope(SnapshotCategory)`; added matching `DeleteSnapshotCategory` / `TryReadSnapshot` / `WriteSnapshot` wrappers for the callers that weren't going through `FetchWithSnapshotAsync`. The one caller needing a raw, pre-composed scope (`FlockGameProvider`'s by-name version lookup, which intentionally stays on `BootstrapScope` instead of nesting under `GameVersionId`) now calls a separate `FetchAtScopeAsync`, so that exception stays explicit instead of relying on parameter-shape coincidence.

### Removed
- **`FlockSchemaProvider` / `Client.Schema`.** Dead code — unreferenced anywhere in the SDK or codegen, and 3 of its 4 methods duplicated endpoints `FlockConfigProvider` already fetches and caches. `ISchemaProvider`'s `SchemaTag` enum and the `SCHEMA` provider-strip unit (still gating the codegen tree) are unaffected.

### Fixed
- **Offline write queue is now player-scoped.** The pending-writes queue was keyed only by game version and its in-memory copy survived a player switch, so Player A's queued offline writes could replay under Player B after an account switch. The queue is now keyed by player id and reloads whenever the signed-in player changes — each player's writes stay isolated and replay only when that player signs back in.
- **`FlushPendingWritesAsync` single-flight guard** moved into the method itself, so a manual call can no longer race the auto-flush and double-POST a non-idempotent queued command.
- **Analytics auth-id rewrite race.** Stamping the player id onto anonymously-cached events at login could race an in-flight flush and delete freshly-attributed files before they were sent (attribution lost). The flush now marks its batch in flight and the rewrite skips in-flight files.
- **Token-refresh piggyback** no longer assumes the refresh token rotates: queued waiters detect a completed refresh via a generation counter, so a backend that re-issues the same refresh token won't cause redundant refresh POSTs.
- **Atomic snapshot writes.** `FlockSnapshotStore.Write` uses `File.Replace` instead of delete-then-move, closing the crash window that could lose the prior snapshot.
- **Optimistic-row eviction** on a permanently-rejected queued write no longer discards sibling writes' overlays for the same `player_data_id`.
- Removed a dead, never-read `etag` field from the snapshot envelope.

### Documentation
- README codegen section: achievement enum generation, the achievement details config + `GetAchievementDetailsAsync`, and a `FLOCK_NO_PLAYER` + codegen note; the quick-start examples now use `FlockAchievementId`.
- [ARCHITECTURE.md](ARCHITECTURE.md): dropped the `FlockSchemaProvider` entry from the Runtime/Providers list and folder overview (removed above).

## [1.21.0]

### Added
- **Download progress.** `DownloadAsync<T>` and `PreloadAsync` now accept an `IProgress<float>` parameter (0 → 1); batch and predicate overloads aggregate across all assets via `Interlocked.Increment`.
- **Concurrent download throttle.** `FlockInitConfig.AssetMaxConcurrentDownloads` (default `4`) caps parallel S3 fetches during batch calls via `SemaphoreSlim`. Set `0` or negative to remove the limit.
- **Predicate-based preload.** `PreloadAsync(Func<AssetSchema, bool> predicate, IProgress<float>, CancellationToken)` warms the disk cache for any matching assets in the catalog without decoding into a Unity type.
- **`GetUncached(IEnumerable<AssetSchema>)`** — filters a list to only entries not yet on disk; useful for building a targeted prefetch queue.
- **EditMode tests** (`FlockAssetProviderTests`) — cache hit/miss/eviction and `GetUncached` coverage; `Runtime/AssemblyInfo.cs` adds `InternalsVisibleTo("Flock.Tests.Editor")` so `FlockAssetCache` is reachable from tests without becoming public.

### Changed
- `GetByNameAsync` now **throws `FlockException`** when the asset is not found instead of returning `null`. Update any `if (asset == null)` guards to `try/catch`.
- `DownloadToCacheAsync` (the internal preload path) writes directly via `UnityWebRequest.Get` without a `byte[]` intermediate copy — lower peak memory on large assets.
- `FlockSession` is only constructed when `AnalyticsConfig.Enabled = true`; previously it was always created, which called `DontDestroyOnLoad` at init time and crashed EditMode tests.

### Documentation
- README asset section updated: `GetByNameAsync` throw-on-miss note, progress overload example, predicate preload example, `GetUncached` example, batch concurrency note.

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
