# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).
It is released with the Flock SDK, at the Flock SDK's version.


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
