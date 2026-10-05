# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).
It is released with the Flock SDK, at the Flock SDK's version.


## [1.67.0]

### Fixed
- **An Android build no longer logs shader warnings from the package** (6 for Vulkan alone, 13 with Unity's default graphics
  APIs, measured). Building for OpenGL ES and Vulkan, the capture's colour conversion warned of a power that could be handed a
  negative value and of a value it could leave unset. Neither could change a recording, and the conversion now gives neither
  reason: what it records is unchanged.

### Verified
- On a Galaxy S23 Ultra (Android 16), against the same game built without the package, in IL2CPP (64-bit) and Mono (32-bit)
  players at 60 frames a second: recording at the default settings cost no frame rate (60.01 and 59.95 frames a second against
  60.01), and collecting play data alone nothing measurable; the frames captured took about 1 to 3.5 ms more of the graphics
  card's time. The package adds 7.0 MB to an IL2CPP build and 1.4 MB to a Mono one.
- Installed from its git URL into a new Unity 6000.3 project with that project's own defaults (at 1.66.0, before this release's
  shader change), a live playtest on the phone, answered by taps, recorded, uploaded its recording, and the recording played in
  a browser's video player from the link Protokite gives its dashboard.
- No shader warnings in Android builds (IL2CPP and Mono, Vulkan and OpenGL ES), and on the phone the recordings are unchanged on
  Vulkan and OpenGL ES, a phone turned upright mid-recording included (black bars beside the picture); the Unity 6000.3 and 2021.3
  test suites.

## [1.66.0]

### Added
- **On a phone, the player chooses which networks recordings upload on.** On an Android player that records video, an answer to
  the consent question that lets the screen be recorded is followed, on the same panel, by a second question: upload on Wi-Fi
  only, or on Wi-Fi or mobile data (it says about how many MB a minute of play takes, at the recording's bitrate). The answer
  holds back uploads, never the recording: a Wi-Fi only player's recordings wait on the phone and upload the next time it is on
  Wi-Fi, in the same launch or a later one, and an upload under way stops when the phone leaves Wi-Fi and is sent again on Wi-Fi.
  The answer is kept beside the consent answer (`ProtokitePlaytest/playtest_upload_network.json`), counts in a build that stops
  asking, and is asked again by `AskForPlaytestConsent`. Nothing is asked on Windows, and nothing waits there.
- `ProtokitePlaytest.PlayersUploadNetworkAnswer` and `ProtokitePlaytest.SetPlaytestUploadNetwork(choice)`, with
  `ProtokitePlaytestUploadNetworkChoice` (`NotAnswered`, `WiFiOnly`, `WiFiAndMobileData`), for a game that asks in its own menu.
- Each session start carries the answer in its debug facts as `playtest_upload_network` (`wifi_only`, `wifi_and_mobile_data` or
  `not_asked`). On a phone that asks, the session starts once the question is answered, so it always carries the answer; the
  screen is recorded meanwhile.

### Changed
- **A recording waiting to upload is never deleted for a new one while the player's answer holds uploads back** (Wi-Fi only and
  the phone off Wi-Fi, or the question still to be answered): the new recording records into the room left in the disk budget,
  and with less than 1 MB records nothing that launch and says why. Otherwise a Wi-Fi only player who played again on mobile data
  would lose the last session's video once about 20 minutes of it waited (Android Recordings Disk Budget Mb 1024, with a new
  recording making room for 810 MB). At 1024 MB, about 94 minutes of play can wait for Wi-Fi in full.
- **Forget This Machine's Answer** in the setup window forgets the answer about upload networks too.
- The live self-test skips its upload step, saying why, when the player chose Wi-Fi only and the device is not on Wi-Fi, and its
  session steps while the session waits for that answer.

### Fixed
- **A recording the player took the screen back from is deleted even when its upload had failed.** Since 1.57.0, once this
  launch's upload had ended without going (refused, or no connection), an answer that no longer lets the screen be recorded left
  the recording and its session on disk, and the next launch uploaded it. Only an upload still under way, or one that went, is
  left alone now.

### Verified
- On a Galaxy S23 Ultra (Android 16, IL2CPP, Vulkan, High managed stripping), with the questions answered by taps on the panel:
  "Wi-Fi only" off Wi-Fi asked for no upload link and sent nothing, and back on Wi-Fi the recording uploaded in the same launch,
  whole; "Wi-Fi or mobile data" uploaded off Wi-Fi; "play data only" was never followed by the question about networks; and each
  session start carried its answer. Unity reported the phone's Wi-Fi as a local network, and no network once it was off (mobile
  data was off on that phone, so a mobile data reading was not measured). Reading the network costs about 2 µs.
- Unity 6000.3 and 2021.3 test suites, and every deliberate break of the new rules caught by a test (48 breaks).

## [1.65.0]

### Added
- **A phone's recording follows the phone's heat, battery and free space**, with three new settings in **Video recording
  (Android)**, each with a property of the same name on `ProtokitePlaytestSettings`; a settings asset saved by an earlier
  version starts with these defaults:
  - **Slow Down The Recording When The Phone Is Hot** (on): when Android says it has started to slow the phone down for heat
    (thermal status moderate), the recording takes half its frame rate until the phone cools; when Android says it is slowing
    the phone down enough for the player to notice (severe or above), the recording stops for the launch, and what it holds is
    kept and uploaded as usual. Phones before Android 10 do not report their heat, record as set, and the log says so once.
  - **Stop The Recording Below Battery Percent** (15): the recording stops for the launch, kept and uploaded as usual, when the
    battery falls below it while the phone is not charging. 0 never stops it.
  - **Android Recordings Disk Budget Mb** (1024): Android players' recordings are held to it in place of Recordings Disk Budget
    Mb, and never take the phone's free space below the line where Android warns that storage is running out (500 MB, or a
    twentieth of the storage when that is less). A fuller phone gives a recording less room, and the log says so.
  - The phone is asked about its heat and battery every 5 seconds of play while a recording runs, and at once when one starts;
    only what a setting uses is asked. Every change of rate, and a stop, is logged once.

### Changed
- **Going to the background writes out everything a recording holds**: the frames still on their way from the graphics card,
  and those the encoder has not handed back, so a game Android ends while it is away keeps every frame recorded before it left
  (measured on the earlier version: about two frames were lost). On a phone the encoder is given back to the phone while the
  game is away, and the first frame after the return starts a new stream on a keyframe, in the same file; the time away is
  still left out of the video, with nothing filling it. A phone whose new stream would not match the video's ends the video
  where the game left, keeping what was recorded, and says why. The log of a finished recording says how many times the game
  went to the background.
- **Frames encoded before an encoding failure are written.** A recording whose encoder failed used to stop writing at once, so
  frames already encoded and waiting for the disk were dropped; only a failed write stops the writing now.
- A recording's start line names a budget or the phone's free space as cutting it short only when that room can stop it before its
  length limit.

### Verified
- On a Galaxy S23 Ultra (Android 16) with Unity 6000.3 players, High managed stripping: **five minutes in the background and back**
  (IL2CPP, Vulkan) gave one recording of 451 frames, read back by the phone's own decoder and played by Chrome at 30 s, with the
  time away left out (longest step between frames 83 ms), the first frame after the return a keyframe and no error; waiting for the
  frames on their way took 2.6 ms on the main thread and the encoder's hand-over 33.4 ms. The same 30 seconds away on OpenGL ES
  (13.2 ms and 41.4 ms) and on a 32-bit Mono player passed every check. A live playtest sent to the background for five minutes
  against a local Protokite and Flock kept one session, ended at quit, and its recording was stored as MP4 that Chrome plays at
  its 13.7 s of play, not five minutes more.
- **Killed 10 seconds into the background** (`am force-stop`), the file the kill left held every frame captured before the game
  left (IL2CPP and Mono), where the earlier version lost 135 ms; the next launch finished it whole.
- Heat, battery and free space, driven by the package's test stand-ins on the phone (ten minutes of encoding raised no heat on
  this phone): severe heat at 8 s stopped the recording at the next question (9.8 s kept, Chrome plays it); moderate heat from 5 s
  to 15 s halved the rate for 75 frames, then full rate again; no heat left it at 15 frames a second throughout; 10% battery not
  charging stopped it, 10% charging did not; 50 MB free recorded no test video and said why, and with an earlier test video kept,
  deleting it made the room. The package's own readers, called in the stripped player, read thermal status 0, the battery the
  phone reports and the free space `df` reports.
- The package's tests pass on Unity 2021.3 and 6000.3.

## [1.64.0]

### Added
- **Android players record the game's screen**, with the phone's own hardware H.264 encoder, into the same MP4 file Windows
  writes, uploaded as `video/mp4`. Android's media library is called from C#, so the package still ships no native file. The
  capture is the one Windows uses: copied, scaled and converted on the graphics card, read back without waiting for it.
  - A new **Video recording (Android)** section in **Protokite > Playtest > Settings**: **Record Video On Android** (on),
    **Android Video Long Side** (1280), **Android Video Frames Per Second** (15), **Android Video Bitrate Kbps** (1500) and
    **Android Allow Software Encoder** (off), with properties of the same names on `ProtokitePlaytestSettings`. A settings
    asset saved by an earlier version starts with these defaults.
  - The long side is the limit whichever way the phone is held, so a game held upright records upright: on a 20:9 phone,
    1280x592 sideways and 592x1280 upright.
  - The bitrate is held as a constant rate where the phone's encoder says it takes one (measured: 1,520 kbps against 1,500
    asked, where the phone's default mode wrote 1,895 on a busy scene).
  - A phone whose encoder does not take the video's size or rate records at the next size down it does (three quarters, a
    half, three eighths of the long side), then at 15 and 10 frames a second, and the log says which.
  - A phone with no hardware H.264 encoder that takes the capture's frames records no video and says why once; **Android
    Allow Software Encoder** has it record with Android's software encoder on the processor instead, which costs frame rate
    and battery. An encoder that refuses to start ends the recording, and the log gives its status.
  - With **Record Video On Android** off as the game starts, an Android player records no video and never asks the phone for
    its encoders; everything else in the playtest still runs. Turned on later in a launch, it takes effect from the next one.
  - The setup window's Video check passes for an Android build target with Record Video On Android on, and says how to turn
    it back on when it is off.
- The settings are now grouped as **Video recording (64-bit Windows)**, **Video recording (Android)**, **Recordings (every
  platform)** (length, size and disk budget) and **Heavy analytics**. The editor, whatever it builds for, reads the Windows
  section, so a test video in the editor records with it.

### Changed
- **A window resized, or a phone turned, during a recording keeps its shape** inside the video's size, with black bars, where it
  used to be stretched to fill it. The video's size is still set when the recording starts.

### Verified
- On a Galaxy S23 Ultra (Android 16), in Unity 6000.3 players built with High managed stripping: IL2CPP 64-bit on Vulkan and
  OpenGL ES, and Mono 32-bit on Vulkan. Each recorded a 20 second test video through the phone's hardware encoder
  (`c2.qti.avc.encoder`): 300 frames read back by the phone's own decoder, played and sought by Chrome, at 1,511 to 1,517 kbps
  against 1,500 asked, while the game kept 60 frames a second. Held sideways it recorded 1280x592, held upright 592x1280.
- Counter-cases on the same phone: with Record Video On Android off, the test video was refused with the setting named and the
  phone was never asked for its encoders; with OpenGL ES 3.0 forced (no compute shaders), it was refused with that reason, said once.
- Turned upright 8 seconds into a sideways recording, the game was recorded at its own shape in the middle of the 1280x592 video,
  with black bars beside it, as Chrome showed 15 seconds in, on Vulkan and OpenGL ES; a Windows player whose window was made tall
  mid-recording did the same.
- Unity 2021.3 Android players on the same phone: every case above passed (IL2CPP on Vulkan and OpenGL ES, Mono 32-bit).
- A live playtest on the phone, against a local Protokite and Flock: the player signed in, its session started and recorded,
  the recording uploaded (stored as MP4 that Chrome plays), the session ended when the game quit, and its playtest event reached
  Flock once.
- The package's tests pass on Unity 2021.3 and 6000.3, the capture's new margins checked on the editor's own graphics card.
- Not yet measured: other phones (a flagship's costs are a best case), and x86 Android devices (some Chromebooks and emulators).
- On that phone, a 32-bit Mono player built with Unity 6000.3 sometimes stayed paused before its first scene until it was left and
  reopened, with or without this package (measured: 1 of 3 and 2 of 3 cold starts reached the scene unaided); IL2CPP players always
  started, and a 2021.3 Mono player started unaided.

## [1.63.0]

### Changed (breaking)
- **Video is recorded through Windows' own H.264 encoder on the graphics card, into MP4, and the package ships no native file.**
  The package used to carry its own encoder, `protokite_vpx.dll` (libvpx, VP8 into WebM). On a Windows 11 laptop with Smart App
  Control on, Windows refused new copies of that unsigned DLL (error 4551, measured), so those players recorded no video; and
  encoding on the processor cost a slow laptop 10 to 42% of its frame rate, depending on what else the PC was doing. Now each frame
  is encoded by the graphics card's own video engine through Windows' Media Foundation, called from C#, and written straight into a
  fragmented MP4 file a browser plays with nothing installed, uploaded as `video/mp4`. There is no DLL of the package's for Windows
  to refuse, and on a desktop encoding costs 3% of a processor core over the capture where the DLL cost 8% (measured).
  - Colour is converted as BT.709 and the stream says so, so a player that ignores what the stream says still shows it right.
  - Each frame is a fragment of its own, written whole, so a recording a crash cut off keeps every frame before the cut, and the
    next launch finishes it as before.
  - A recording starts capturing once the graphics card's encoder has started, which takes 0.1 to 2.2 s (measured), so a slow
    start delays the video by that moment rather than losing its first frames. An encoder that cannot start ends the recording,
    and the log says why.
  - **A PC whose graphics card has no H.264 encoder records no video**, and the log says why once; everything else in the playtest
    runs. The new setting **Allow Software Encoder** (off) has such a PC record with Windows' own encoder on the processor instead,
    which costs the game frame rate (measured at the defaults: 11% of a processor core on a desktop, about 30% on a slow laptop,
    where the game still kept its 60 frames a second).
  - A Windows N edition without its Media Feature Pack has no Media Foundation, records no video and says so.
  - The setup window's video check names the encoder Windows offers first on the PC you work on, or why it records none; a test
    video shows whether that encoder records.
  - Recordings an earlier version kept as WebM files are still finished after a crash and uploaded as `video/webm`.

### Removed (breaking)
- The settings that only the old encoder used: **Video Codec**, **Encoder Threads**, **Use Codec Default Speed**, **Encoder Speed**
  and **Encoder Below Game Priority**, their properties on `ProtokitePlaytestSettings` (`VideoCodec`, `EncoderThreads`,
  `UseCodecDefaultSpeed`, `EncoderSpeed`, `EncoderBelowGamePriority`) and the `ProtokitePlaytestVideoCodec` enum. A settings asset
  that still holds them loads as before; Unity drops the old values the next time it saves the asset.
- `Runtime/Plugins/x86_64/protokite_vpx.dll`, libvpx's licence and patent files, and the encoder's source in `Native~`.

### Fixed
- **On Unity 2021.3, the consent question and the feedback form logged a warning and threw an exception.** Each panel made its
  settings in code, which 2021.3 checks for a theme the moment they exist ("No Theme Style Sheet set to PanelSettings, UI will not
  render properly"), and its empty theme, also made in code, threw a `NullReferenceException` inside UI Toolkit as the panel styled
  itself, which the Flock SDK then reported as one of the game's exceptions. Each panel now draws with a copy of
  `Runtime/Resources/ProtokitePlaytestPanelSettings.asset`, which holds an empty imported theme. Unity 6000.3 logged neither,
  before or after.

### Verified
- In 64-bit Windows players built with Unity 2021.3 and 6000.3, Mono and IL2CPP (High stripping), against a running playtest:
  each recorded on the graphics card, uploaded its recording, which is stored as `video/mp4` and plays in Chrome, and carried no
  native file of the package's.
- Recording on Direct3D 11 and 12, Vulkan and OpenGL in players: every frame decoded back by Windows in the screen's own colours,
  and no stall of the game's main thread.
- The package's tests pass in the Unity 6000.3 Editor with the real encoder, a recording read back frame for frame by Windows'
  own decoder.
- On a slow laptop with Intel graphics and Smart App Control on, Windows' software encoder recorded every frame, read back by
  Windows. Recording through Intel's own graphics card encoder is not yet confirmed on that laptop: a build before this release
  had it refuse to hand back the first frame, most likely because it changes its output format first and was asked again too
  soon. This release asks only when the encoder says a frame is ready, as Windows' rules require, and takes the buffer size the
  new format asks for; both are tested with a stand-in that keeps those rules.
- Not yet measured: AMD's graphics card encoder, and how many bits a busy picture takes at the default bitrate.

## [1.62.0]

### Documentation
- **A step-by-step guide with screenshots** at [docs.qwacks.com/protokite/unity-package](https://docs.qwacks.com/protokite/unity-package),
  in English and Arabic: install from the Playtesting tab, point the build at the playtest, check the setup, sign a player in and
  press Play, with every status that stops a playtest and what fixes it. A second page covers what a playtest collects, the
  recording limits, what "no video on this platform" means, the feedback form, every call a game can make, and testing in the
  editor.
- The README no longer calls this an early version, and links the guide.

No code changes.

## [1.61.0]

### Added
- **A live self-test**, `ProtokitePlaytestSelfTest.RunAsync`, and **Run Live Self-Test** in **Protokite > Playtest > Setup Checks
  And Test Video**. It checks this build's playtest end to end against the live Protokite:
  - the config loads, and the session starts
  - an exception and a playtest event reach the Flock SDK
  - a filled-in form is taken, and the recording is uploaded
  Each check is paired with a request Protokite must refuse, judged by its own status. It sends each refused request once, through
  a client of its own, so the game is left as it was. The report lists every step with what it found, and a run id carried by
  what it sends: the exception's message, the event's properties and the form's text answers. It runs in the editor and in
  development builds, and a release build refuses it.

### Fixed
- The log line after an upload no longer says the recording is gone from disk when its files could not all be deleted.

## [1.60.0]

### Added
- **More of the playtest for your own code**: `Describe(status)` and `Describe(answer)` in the words the playtest logs,
  `PlayersConsentAnswer` (what the player answered on this machine, never what a build that does not ask assumes),
  `IsRecordingVideo` and `StopVideoRecording()` for a game ending play on its own schedule, and `RecordTestVideo` with
  `TestVideoState`, `FinishedTestVideoPath` and `TestVideoProblem`, for a game's own "record a clip" button.
- **A sample**, `Samples/PlaytestSample`: one script putting every call on one screen, with sign-in, consent, a Steam id, a playtest
  event, the recording, the feedback form (opened, or answered from code) and a test video.

### Changed
- **Protokite > Playtest holds Settings and Setup Checks And Test Video only.** Ask The Player Again and Open The Feedback Form are
  now buttons under **While testing** in that window, **Forget This Machine's Answer** and **Open Feedback Form**, each saying what it
  does. The methods behind them are kept.
- Every public member has a one-line description.

### Fixed
- The setup window no longer draws a line its layout did not count when **Record Test Video** is refused, and each of its
  buttons that changes what is drawn below it ends the event there.
- **Set Game Version To pt-...** and creating the playtest settings save only that asset. Before, they saved every unsaved
  asset in the project, so an edit you had not saved yet was written to disk with them.
- `RecordTestVideo` refuses in the editor outside Play Mode, saying why. Before, it answered true and recorded nothing, as the
  next Play cleared the request.
- The note under **Open Feedback Form** says when the consent question is holding the form back, instead of saying it opens.
- The descriptions of `ProtokitePlaytestFeatures.ExceptionCapturing` and `SendFeedbackForm` say what the package does: the switch
  is read by nothing here (the Flock SDK captures exceptions whatever it says), and a send is also refused when nobody can be
  named as the sender or the answers cannot be kept on the device.

## [1.59.0]

### Added
- **Protokite > Playtest > Setup Checks And Test Video**, a window that checks what a build of the project needs for a
  playtest: **Playtesting Enabled**, a usable **Protokite API URL** (the game's own rule), Flock's **Game Version** set to a
  playtest's name (`pt-<test id>`) and resolved, and a build target that records video (64-bit Windows, x64). Each failed
  check says what to change and opens the place to change it. It opens centred on the editor, big enough to read every
  check without scrolling.
- **A Game Version ID pasted where a name belongs is flagged**, in Game Version or over the resolved ID, and the window asks
  Flock which version it is: when that is a playtest's whose name resolves back to it in the game, it offers **Set Game
  Version To pt-...**, which sets the name and its ID. A pasted ID is replaced whenever Game Version is resolved again, so a
  build would otherwise send another version.
- **Detect Key** beside the **Feedback Form Key** setting: click it and press the key the form should open with. Escape or
  **Cancel** keeps the key it had, and Shift, Control, Alt and Command are skipped, so a key pressed with one held is the key
  set. The list beside it still offers every key, and the setting is stored as before.
- **Record Test Video**, in the same window in Play Mode: the Game view for the seconds chosen, with the playtest's video
  settings and no playtest needed, into `Recordings/TestVideos/`. It is never uploaded, makes room for its own length at its
  bitrate by deleting older test videos only (never a recording waiting to upload), and gives way to the playtest's own
  recording.

## [1.58.0]

### Added
- **The feedback form.** When the playtest publishes one, the player opens it over the game with **F9** (the new **Feedback
  Form Key** setting; None leaves it to the game) and fills it in. It is built from the published form, so editing it in
  Protokite needs no new build: text, many-line text, a 1 to 5 rating, options and a checkbox, and a question of a kind this
  package does not know is a text box. It opens and sends whatever the player's consent answer: it is their own message.
- Sending checks the answers the way Protokite does and shows every problem against its question first: a required checkbox
  counts unticked, and an empty optional answer is left out.
- **A sent form is kept on the device, then sent**: when the network comes back, two minutes later, or by a later launch.
  One Protokite refuses for good is deleted, naming the question it refused. It names this launch's Protokite session once
  one has started. One naming no session that may already have been stored when its send failed is sent again, so the studio
  may see it twice rather than lose it.
- **Upload your recording** on the form stops this launch's recording and sends it straight away, once its session has
  started. Opening the form never stops it.
- `ProtokitePlaytest.FeedbackForm`, `CanOpenFeedbackForm`, `OpenFeedbackForm`, `CloseFeedbackForm`, `IsFeedbackFormOpen`,
  `SendFeedbackForm` (for a form the game draws itself), `CanSendTheRecording` and `StopRecordingAndSendIt`, with
  `ProtokitePlaytestFormAnswers`, `ProtokitePlaytestFormProblem` and `ProtokitePlaytestRatings`. **Pause The Game While The
  Form Is Open** (off). **Protokite > Playtest > Open The Feedback Form** in Play Mode.

### Changed
- "Collect nothing" on the consent question now says a feedback report the player chooses to send still goes; with that
  answer the playtest otherwise behaves as with playtesting off.
- The consent question waits while the feedback form is open, and the form does not open over it.

### Fixed
- **On WebGL, the playtest's saved files now outlive the page.** A WebGL player keeps them in memory and copies them to the
  browser's storage only when asked, and the playtest never asked, so a change made after the page's first seconds could be
  lost when the tab closed, unless something else had the files copied first: the player's consent answer (a player who
  changed theirs to "nothing" could have the old one back on their next visit), the device id (a new player every visit), and
  feedback forms waiting to be sent (one already sent could come back and be sent again). Every change now asks for the copy,
  the way the Flock SDK asks for its own files.

## [1.57.0]

### Added
- **The player is asked what the playtest may collect**, and nothing is collected until they answer: the screen and play
  data, the screen only, play data only, or nothing. **Nothing** reads exactly like Playtesting Enabled off: no recording, no
  play data, no Protokite session. The question says in its own words that it is the playtest's own, separate from any
  privacy or analytics choice the game asks about, and it changes neither the Flock SDK's analytics consent nor its exception
  capture.
- The question is drawn over the game with UI Toolkit built from code, needs no EventSystem of the game's, and works with the
  Input Manager, the Input System or both. While it is on screen the cursor is shown and free; the game's own cursor state
  comes back once it is answered. It is answered with the mouse, and no answer is selected when it appears, so a game's own
  Submit key (Space, by default) presses nothing. A press in its first half second is ignored, so a player still clicking
  at the game does not answer a question they never read, and a click is never taken as an answer while the game keeps
  locking the cursor (each click would land at the screen's centre); a warning then says to stop locking it while
  `ProtokitePlaytest.IsConsentQuestionOpen` is true.
- The answer is kept in `ProtokitePlaytest/playtest_consent.json` under the game's persistent data folder and counts in
  every later launch, even in a build that stops asking. Each session start sends it as `playtest_consent` and
  `playtest_consent_asked` in its debug facts.
- **Ask The Player For Playtest Consent** (on) in the playtest settings. `ProtokitePlaytest.PlaytestConsent`,
  `SetPlaytestConsent`, `AskForPlaytestConsent` and `IsConsentQuestionOpen`. Two statuses: `WaitingForPlayerConsent` and
  `PlayerRefusedPlaytest`. **Protokite > Playtest > Ask The Player Again** forgets the answer the editor's machine gave.

### Changed
- `ProtokitePlaytest.IsFeatureEnabled` also needs the player's answer; a feature this build does not know needs the answer
  that allows everything.
- Taking the screen back deletes this launch's recording rather than keeping it for a later launch to send, and removes the
  session saved beside it at once.
- Recordings earlier launches kept wait while the question is on screen, while the config that decides whether it is put is
  on its way, and for as long as the answer is nothing, even with Playtesting Enabled off; they go in the same launch the
  player changes their mind.
- A run in batch mode or without graphics cannot show the question, so it collects nothing, and says so once.

## [1.56.0]

No changes to the Protokite Playtest package: this version is released together with a Flock SDK fix to exception
capture, which no longer ends an IL2CPP player when a game's exception throws from its own `Message` getter.

## [1.55.0]

No changes to the Protokite Playtest package: this version is released together with the Flock SDK's wider exception
capture, which reports a playtest build's exceptions from start-up and from every thread.

## [1.54.0]

### Added
- **Heavy analytics.** When the playtest's config turns `heavy_analytics` on, every ten seconds of play become a
  `performance_window` event (frame time median, 95th and 99th percentile, hitches, memory used and peak, the scene), and every
  scene loaded in place of another a `level_loaded` event (the scene, the one before, and how long the load held the game up),
  sent through the Flock SDK under the category `playtest`. A frame time is the real time from one frame's Update to the
  next. Time in the background and the frame a scene load holds up are left out; a window a stop cuts short is dropped. The
  scene is the active one, however it became active.
- `ProtokitePlaytest.RecordPlaytestEvent` puts the game's own events on the same timeline, from any thread.
- **Hitch Frame Time Ms** in the playtest settings (default 60, and never read as less than 1).

### Fixed
- Time the game spent in the background is left out of the recording again on a player. A player reported that time several
  frames after the game came back, so it was recorded as a still picture; the recording now measures real time between frames.

## [1.53.0]

No changes to the Protokite Playtest package: this version is released together with the Flock SDK's WebGL saved-files fix
and the awaited flush.

## [1.52.0]

No changes to the Protokite Playtest package: this version is released together with the Flock SDK's public gameplay
events call.

## [1.51.0]

No changes to the Protokite Playtest package: this version is released together with fixes in the Flock SDK.

## [1.50.0]

### Added
- **Recordings are uploaded to their Protokite session.** This launch's recording goes once its file is finished (it reached
  its length or size limit, or the game stopped it) and its Protokite session has started, whichever comes second. A
  recording still going when the game quits is kept, and so is one whose upload the quit interrupted: the next launch sends
  it. When a launch starts, the recordings earlier launches kept go too, once the folders are gone through and the Flock SDK
  is running, one at a time and the oldest first, each with this launch's API key and the Game Version ID its own session
  started with (Protokite finds a session's playtest from that version, so a later build's own would be refused).
- **A recording counts as uploaded only when the storage answers its upload with a 2xx.** Protokite marks a session as having
  a recording as soon as it hands out a link, so a link is never taken for an upload. The link is asked for only once the
  file is finished, for the content type the file is written as, and a failed upload is tried once more with a fresh link
  (not when the storage refused the link's signature, which a fresh link would repeat). An uploaded recording is deleted; one
  that was not is kept, with its session, for a later launch, and the log says why.
- **What earlier launches kept is uploaded with Playtesting Enabled off too**, so a release build never strands what a
  playtest build of the game recorded; with it off nothing is recorded, no session is started and no playtest config is
  fetched. Every refusal (403 another playtest's session, 404 no such session included) keeps the recording for a later
  launch to ask again, until a launch that records needs its room.

### Changed
- **Recordings earlier launches left are finished, kept or deleted in every launch**, Playtesting Enabled or not, since the
  upload above needs them finished; before, a build with it off left them untouched.
- **A finished recording that is in use counts against Recordings Disk Budget Mb at what it takes**, no longer at the room
  it once reserved: an upload holds one for as long as it takes to send, and counting it at its reservation meanwhile could
  delete a recording waiting to be uploaded, or cut this launch's recording short, for room that was free. It is still never
  deleted while in use. A stray file in a recording's folder (one a file browser leaves) is never taken for its video.

## [1.49.0]

### Added
- **A recording cut off by a crash, a power cut or a killed game is kept.** Each recording now has a folder of its own,
  `ProtokitePlaytest/Recordings/Playtest/<UTC time>-<8 hex digits>/`, holding the video, the Protokite session it belongs to
  (`session.json`: the session id, the Protokite API URL and the Game Version ID the session started with, never the API
  key), the room it may take, and a lock file the game keeps open, shared with nobody, for as long as it runs. When a later
  launch starts, a thread of its own goes through the folders whose game has ended: a cut-off video is finished with every
  whole frame it holds, and a recording with a saved session is kept to be uploaded to that session. A recording no Protokite
  session started for is deleted, and so is a folder left with no video. A folder whose game still runs is never touched.
  Anything that cannot be finished or deleted is named in a warning and tried again by the next launch.
- **Recordings Disk Budget Mb** (4096) in Protokite > Playtest > Settings: the most every recording kept on the machine may
  take together. Before a recording starts, recordings whose game has ended are deleted, the oldest first, until it fits.
  A recording makes room only for what its length limit records at its bitrate, with a quarter to spare, so a short recording
  never deletes one waiting to be uploaded that it would fit beside. It may
  then grow into all the room left, up to Max Recording Size Mb. A recording still being written counts at the most it
  may grow to and is never deleted. With less than 1 MB left, that launch records no video and says which setting to raise.
- Two copies of a game starting to record at the same moment never take each other's room: each makes its own folder and
  reserves its room before making room, so each counts the other.
- A recording starts once the launch has gone through what earlier launches left (usually a few milliseconds; at most 10
  seconds), so a cut-off recording not yet finished is never counted at more than it holds, and no recording waiting to be
  uploaded is deleted for room that is in fact free.
- A launch lets go of its recording's folder when it ends, once the file is written: the Editor, which stays open after Play
  Mode, no longer keeps it held until the next Play.
- A session that starts after its recording, and one quitting waits for, is saved beside the recording, so the recording
  can still be uploaded to it.

### Changed
- Recordings are written into their run folder. Files 1.47.0 left straight in `ProtokitePlaytest/Recordings/` are left where
  they are, and not counted against the budget; delete them by hand if you no longer want them.

## [1.48.0]

No changes to the Protokite Playtest package: this version is released together with the Flock SDK's own launch folders.

## [1.47.0]

### Added
- **The playtest records the game's screen** on 64-bit Windows when its config turns video_recording on: from the moment the
  config loads, before anyone signs in, one recording a launch, written as it records to `ProtokitePlaytest/Recordings/` as a
  WebM file. It stops for good at the length or size limit, or when the game quits, which waits for the file within the same
  3 seconds as the session end. This version keeps recordings on disk; sending them comes in a later one.
- **Frames are captured without making the game wait**: copied, scaled and converted on the graphics card and read back
  asynchronously, with encoding and writing each on a thread of their own. A frame that cannot keep up is dropped and
  counted, never waited for. Measured at the defaults: on a fast PC the main thread's median frame time went from 2.60 ms to
  2.86 ms and the frame rate did not move; on a slow laptop (4 cores, integrated graphics) it cost 10.6% of the frame rate,
  with no frame dropped.
- **Video settings** in Protokite > Playtest > Settings: codec, largest size (each side rounded down to a multiple of 16),
  frames per second, bitrate, encoder threads, the encoder's speed (or the codec's own), whether the encoder gives way to
  the game, and the length and size limits.
- Recorded upright and in the screen's own colours (within 1 of each channel) on Direct3D 11 and 12, Vulkan and OpenGL, in
  the Built-in Render Pipeline and URP, in Linear and Gamma colour, in Mono and IL2CPP players.

## [1.46.0]

### Added
- **Recordings are written as WebM files**, ready for the video capture that follows in the next release: VP8 or VP9, a file a
  browser plays with nothing installed. Each frame reaches the operating system as it is written, so a recording cut off by
  a crash or a power cut still plays up to the cut, and a later launch can finish it (its whole frames kept, a half-written
  frame cut off, its length stamped in).
- **Which game frames are captured is decided by the frame they are nearest**, at the recording's frame rate: a steady
  rhythm when frame times wobble, time spent in the background left out, and a length limit.

## [1.45.0]

### Added
- **A video encoder for 64-bit Windows**, ready for the recording that follows in the next releases: VP8 or VP9, through
  libvpx 1.17.0 in `Runtime/Plugins/x86_64/protokite_vpx.dll`, beside libvpx's licence and patent grant. It loads in the
  64-bit Windows editor and 64-bit Windows players only, in Mono and IL2CPP builds alike (checked at High stripping).
- **Every other platform builds with the package and records no video**, saying so once; everything else in the playtest
  runs. So does a Windows build whose DLL is missing, or a DLL of another version. A 32-bit or ARM64 Windows build is
  told video is for 64-bit Windows, as a plain message rather than a warning about a missing DLL.
- The defaults are the ones a slow PC can afford: VP8 at speed 12 on one thread, 15 frames a second, 1280×720 at 1.5 Mbps.
  A VP9 recording gets VP9's own default speed.

## [1.44.0]

### Added
- **One Protokite session per launch.** It starts once the playtest's config is loaded and a Flock session has reached the
  server (a playtest never signs a player in, so that is after your game's own sign-in), names that Flock session, and is
  ended when the game quits. A sign-out, a sign-in, a new Flock session or a Flock restart never starts a second one.
  **`ProtokitePlaytest.PlaytestSessionId`** reads it.
- **The session end is waited for at quit**, 3 seconds at most in all, because Protokite takes no end after the fact. A start
  still on its way when the game quits is waited for within those 3 seconds and then ended with the time left; one that
  answers later is left in progress. On WebGL a page closing cannot be held up, so the end is sent once and not waited for.
- **Who is playing**: a device id the package makes once and keeps in `ProtokitePlaytest/device_id.txt` under the game's
  persistent data folder, or **`ProtokitePlaytest.SetSteamId(steamId, playerName)`** for a game with Steam, called before the
  session starts. A Steam id that is empty, longer than 64 characters or holds whitespace is refused, not trimmed, and the
  device id is sent. No Steamworks dependency.
- **`PlaytestNoLongerCollecting`**: the playtest has closed (Protokite answered 400), so playtesting is off until the game is
  launched again.
- Each session carries the Unity version, the build type, the GPU, the active scene and this package's version.

### Changed
- A session start that fails is not sent again that launch: one that reached Protokite may already have created a session.

## [1.43.0]

### Added
- **The playtest fetches its config from Protokite** once the Flock SDK is running: which playtest this build belongs to,
  which features it turns on and its feedback form. Read it with **`ProtokitePlaytest.Config`** and
  **`ProtokitePlaytest.IsFeatureEnabled`**; a feature the config does not mention is off.
- **New statuses say what stopped it**, each logged once as a warning that names what to change:
  `FetchingPlaytestConfig`, `PlaytestNotLinked` (no playtest for this Game Version ID), `ProtokiteRefusedApiKey`,
  `PlaytestConfigUnavailable` (Protokite could not be reached; asked again when the next Flock session starts) and
  `PlaytestConfigForAnotherVersion`. A refusal is not asked again until the Flock SDK is started again.
- The request carries your Flock API key and Game Version ID, never the player's sign-in, and retries as the Flock SDK does.

### Changed
- **`Ready` now means the playtest's config is loaded**, not only that the Flock SDK is running.
- A Protokite API URL with a space inside it is refused as unusable.

## [1.42.0]

### Added
- **The Protokite Playtest package.** Install it with one click from Flock's Playtesting tab, from the release page, or
  with Package Manager. It needs Unity 2021.3 or later and the Flock SDK at the same version.
- **Playtest settings** at `Assets/Resources/ProtokitePlaytestSettings.asset`, created by **Protokite > Playtest >
  Settings**. Playtesting is **off** until a studio switches it on, and the Protokite API URL is filled in.
- **`ProtokitePlaytest.Status`**: whether the playtest can run, and what stops it if not. It follows the Flock SDK
  starting and stopping.
