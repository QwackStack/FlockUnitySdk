# Protokite Playtest

Playtesting for games built with the Flock SDK: each play session, gameplay recording and in-game feedback,
reported to your Protokite playtest.

> **Early version.** This release loads your playtest's config, runs one Protokite session per launch, records the game's
> screen on 64-bit Windows and uploads it to that session, and, with heavy analytics on, sends performance and level events
> through the Flock SDK. The feedback form arrives in a release that follows.

## Install

You need the **Flock SDK** in your project first, at the **same version** as this package.

**One click (recommended).** Open **Flock > Settings**, go to the **Playtesting** tab and press **Install Protokite
Playtest**. It downloads the version that matches your Flock SDK and imports it. No Git needed.

**From the release page.** Download `ProtokitePlaytest-<version>.unitypackage` from the same GitHub release as your
Flock SDK and double-click it.

**With Package Manager.** **Window > Package Manager > + > Add package from git URL**, then:

```
https://github.com/QwackStack/FlockUnitySDK.git?path=/ProtokitePlaytest~#v<version>
```

Use the same `<version>` as your Flock SDK. This route needs Git installed on your machine.

## Switch it on

1. **Protokite > Playtest > Settings** (or **Open settings** on Flock's Playtesting tab). The settings are created the
   first time, at `Assets/Resources/ProtokitePlaytestSettings.asset`, with playtesting **off**.
2. Tick **Playtesting Enabled**.
3. Set your Flock **Game Version** to your playtest's version name (it starts with `pt-`).

The Protokite API URL is filled in for you (`https://api-protokite.qwacks.com`); change it only to point at a local
Protokite, such as `http://localhost:8020`. Your Flock API key and Game Version are used as they are; there is
nothing else to enter.

## Is it running?

Once the Flock SDK is running, the playtest asks Protokite for this build's playtest, using your Flock API key and Game
Version ID. `ProtokitePlaytest.Status` says whether the playtest can run, and if not, why. Each status that stops it is
logged once, as a warning that says what to change.

| Status | Meaning |
|---|---|
| `TurnedOff` | Playtesting is switched off, or the project has no playtest settings |
| `ProtokiteApiUrlMissing` | The Protokite API URL is empty |
| `ProtokiteApiUrlUnusable` | The Protokite API URL is not an http or https address, or has a space in it |
| `WaitingForFlock` | Everything is set; the Flock SDK has not started yet |
| `FetchingPlaytestConfig` | Asking Protokite for this build's playtest |
| `PlaytestNotLinked` | No playtest is linked to this build's Game Version ID. Set the Game Version to the playtest's (`pt-...`) |
| `ProtokiteRefusedApiKey` | Protokite did not accept the Flock API key |
| `PlaytestConfigUnavailable` | Protokite could not be reached. The game carries on, and it is asked again when the next Flock session starts |
| `PlaytestConfigForAnotherVersion` | Protokite answered with another version's playtest (a proxy dropping the version header does this) |
| `Ready` | The playtest's config is loaded |
| `PlaytestNoLongerCollecting` | The playtest has closed and takes no more sessions, so playtesting is off until the game is launched again |

A refusal (`PlaytestNotLinked`, `ProtokiteRefusedApiKey`, `PlaytestConfigForAnotherVersion`) is not asked again until the Flock SDK
is started again (or the game relaunched), since the answer would be the same.

## What the playtest turns on

`ProtokitePlaytest.Config` is the loaded config (null until `Ready`): the playtest's `TestId`, its feature switches and
its feedback form (`Form`, null when the playtest has none). Check a feature with
`ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording)`; a feature the config does not mention is off.

## Sessions

Each launch runs **one** Protokite session. It starts once the playtest's config is loaded and a Flock session has reached
the server, which happens when a player signs in with **Analytics Enabled** and **Analytics Auto Start Session** on in
**Flock > Settings** (or when you call `StartSessionAsync`), and consent given when **Analytics Require Explicit Consent** is
on. The playtest never signs a player in. Signing out and in, a new Flock session or restarting Flock never start a second
one. The session ends when the game quits; quitting waits up to 3 seconds for Protokite to take the end.
`ProtokitePlaytest.PlaytestSessionId` is its id once it has started.

**Who is playing.** The package makes a device id once and keeps it in `ProtokitePlaytest/device_id.txt` under the game's
persistent data folder. A game with Steam sends the Steam id instead, before the session starts:

```csharp
ProtokitePlaytest.SetSteamId(SteamUser.GetSteamID().ToString(), SteamFriends.GetPersonaName());
```

An id that is empty, longer than 64 characters or holds whitespace is refused (not trimmed), and the device id is sent.

## Heavy analytics

When the playtest's config turns **heavy_analytics** on, the playtest sends events through the Flock SDK's `TrackEvent`,
filed under the category `playtest` (`ProtokitePlaytestEvents.Category`):

- **`performance_window`**, for every ten seconds of play: `window_seconds`, `frames`, `median_frame_time_ms`,
  `frame_time_95th_percentile_ms`, `frame_time_99th_percentile_ms`, `hitches` (frames that took the hitch threshold or longer),
  `hitch_threshold_ms`, `memory_used_mb` and `memory_peak_mb` (the process's memory now, and the most it used since measuring
  started; left out where the platform does not report them), and `map` (the active scene, however it became active). A frame
  time is the real time from one frame's Update to the next, so slow motion or a paused game is measured as the frames the
  player saw.
- **`level_loaded`**, for every scene the game loads in place of the one before: `map`, `previous_map` (the scene active before
  it), and `load_seconds`, how long the load held the game up.

A window is ten seconds of play, never of the clock: time in the background and the frame a scene load holds up (a scene added
beside the current one included) are left out. A window a stop cuts short is dropped, not sent. Scenes loaded before the config
turned heavy analytics on are not reported.

**The game's own events** go on the same timeline, with the same category:

```csharp
ProtokitePlaytest.RecordPlaytestEvent("boss_fight_started", new Dictionary<string, object> { { "boss", "hydra" } });
```

It answers true when the event was queued, can be called from any thread, and sends only while heavy analytics runs, which
starts once the playtest's config has loaded: an event recorded earlier in the launch answers false and is not kept. The two
names the playtest sends itself are refused.

| Setting | Default | |
|---|---|---|
| Hitch Frame Time Ms | 60 | A frame that takes this many milliseconds or longer is counted as a hitch |

Heavy analytics needs the Flock SDK's **Analytics Enabled** (Flock > Settings): with it off, nothing is measured and the log
says so once. The events are Flock events like any other, so they wait for a signed-in player and follow the Flock SDK's
analytics consent. They also share its offline queue (**Analytics Max Cached Events**, 1000 by default, oldest dropped first):
six windows a minute fill it in under three hours offline, so raise it for playtests played offline. The playtest calls the
Flock SDK's analytics, so it cannot be used with a Flock SDK exported without Analytics, and the Playtesting tab will not
install it there.

## Video

When the playtest's config turns **video_recording** on, the game's screen is recorded from the moment the config loads,
before anyone signs in: one recording a launch, written as it records as a WebM file a browser plays with nothing installed.
It stops for good at the length or size limit, or when the game quits; quitting waits for the file within the same 3
seconds as the session end, and a file not finished by then stays as its `.part` file. Time the game spends in the
background is left out.

### Uploading

A recording is uploaded to its Protokite session once its file is finished (it reached its length or size limit, or the game
stopped it) and the session has started, whichever comes second. It is sent straight from disk, never whole in memory, and
counts as uploaded only when the storage accepts the file itself: Protokite marks a session as having a recording as soon as
it hands out a link, which is not the same thing. A failed upload is tried once more with a fresh link. An uploaded
recording is deleted from disk; one that was not is kept, with its session, and the log says why.

Nothing is uploaded while the game quits: a recording still going then, or an upload the quit interrupts, is kept and sent by
the next launch. When a launch starts, the recordings earlier launches kept go once the folders below are gone through and
the Flock SDK is running, one at a time and the oldest first, with this build's API key and the Game Version ID each
recording's session started with. **This happens with Playtesting Enabled off too**, so a release build of the game never
strands what a playtest build recorded; with it off, nothing else is recorded or sent.

Every refusal keeps the recording, including Protokite saying the session belongs to another playtest (403) or no longer
exists (404): a later launch asks again. Such a recording goes only when a launch that records needs its room in the disk
budget, so a build that never records keeps asking for it, and says so in the log each launch.

### Where recordings are kept

Each recording has a folder of its own under the game's persistent data folder,
`ProtokitePlaytest/Recordings/Playtest/<UTC time>-<8 hex digits>/`, holding the video, `session.json` (the Protokite session
it belongs to: the session id, the Protokite API URL and the Game Version ID the session started with, never the API key),
`reserved-bytes.txt` (the most it may take) and `in-use.lock`, which the game keeps open, shared with nobody, until it
closes (in the Editor, until Play Mode ends). The session is saved whenever it starts, before or after the recording, and
quitting saves one it waited for.

When a later launch starts, a thread of its own goes through the folders whose game has closed, however it closed:

- A video cut off by a crash, a power cut or a killed game is finished with every whole frame it holds.
- A recording with a saved session is kept, to be uploaded to that session.
- A recording no Protokite session started for is deleted: there is nothing to upload it to.
- A folder left with no video is deleted.
- A folder whose game still runs is never touched. Anything that cannot be finished or deleted (another program has it
  open) is named in a warning and tried again by the next launch.

This runs in every launch, Playtesting Enabled or not. Files an earlier version left straight in `ProtokitePlaytest/Recordings/`
are left where they are and not counted below.

**Recordings Disk Budget Mb** is the most every recording kept on the machine may take together. Before a recording starts,
recordings whose game has closed are deleted, the oldest first, until it fits. A recording starts once the folders above have
been gone through (usually milliseconds, at most 10 seconds), so nothing is deleted for room a cut-off file only seems to
take. A recording makes room only for what its length limit records at its bitrate, with a quarter to spare (about 845 MB
at the defaults), so a short recording never deletes one waiting to be uploaded that it would fit beside; it may then grow
into all the room left, up to Max Recording Size Mb, and the log says so when the budget cuts it shorter. A recording still
being written counts at the most it may grow to and is never deleted, so two copies of the game recording at once never
take each other's room. One that is finished but in use (being uploaded, or its game still running) is never deleted
either, and counts at what it takes. With less than 1 MB left, that launch records no video, and a warning names the
setting to raise.

The frame is copied, scaled and converted on the graphics card and read back without waiting for it; encoding and writing
each run on a thread of their own, and a frame that cannot keep up is dropped rather than stalling the game. The log says
where the recording goes when it starts, and what it holds, and how many frames were dropped and why, when it ends.

The settings are in **Protokite > Playtest > Settings**, under **Video recording**:

| Setting | Default | |
|---|---|---|
| Video Codec | VP8 | VP8 costs a slow PC least; VP9 makes smaller files for more processor time |
| Video Width, Video Height | 1280, 720 | The largest the video is. The screen's shape is kept, a smaller window is not enlarged, and each side is rounded down to a multiple of 16 |
| Video Frames Per Second | 15 | |
| Video Bitrate Kbps | 1500 | |
| Encoder Threads | 1 | |
| Use Codec Default Speed, Encoder Speed | on, 12 | Off: the speed you set (VP8 -16 to 16, VP9 -9 to 9; higher is faster and looks worse) |
| Encoder Below Game Priority | on | The encoder gives way to the game when the processor is busy |
| Max Recording Minutes | 60 | |
| Max Recording Size Mb | 1536 | |
| Recordings Disk Budget Mb | 4096 | The most every recording kept on the machine may take together (above) |

## Platforms

Everything above runs wherever the Flock SDK runs. **Video is recorded on 64-bit Windows only** (the Editor and players,
Mono and IL2CPP): the package carries its encoder there as `Runtime/Plugins/x86_64/protokite_vpx.dll`. Any other build
leaves the DLL out, records no video, and says so once in the log. That includes 32-bit and ARM64 Windows builds, which
are told video is for 64-bit Windows rather than that the DLL is missing. On 64-bit Windows, recording runs on Direct3D 11
and 12, Vulkan and OpenGL, in the Built-in Render Pipeline and URP, in Linear and Gamma colour. It needs a graphics card
that runs compute shaders; a game that draws nothing (a server build, or one started with `-nographics` or `-batchmode`)
records nothing.

## Third-party software

The Windows video encoder contains **libvpx 1.17.0** (VP8 and VP9), © The WebM Project authors, under a BSD licence with an
additional patent grant: see `Runtime/Plugins/x86_64/libvpx-LICENSE.txt` and `libvpx-PATENTS.txt`, which ship with it.

## Remove it

Flock's **Playtesting** tab has a **Remove** button, or remove it as you installed it (delete
`Assets/ProtokitePlaytest`, or remove it in Package Manager). Your settings asset stays until you delete it.
