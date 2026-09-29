# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).
It is released with the Flock SDK, at the Flock SDK's version.


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
