# Protokite Playtest

Playtesting for games built with the Flock SDK: each play session, gameplay recording and in-game feedback,
reported to your Protokite playtest.

> **Early version.** This release asks the player what the playtest may collect, loads your playtest's config, runs one
> Protokite session per launch, records the game's screen on 64-bit Windows and uploads it to that session, sends
> performance and level events through the Flock SDK with heavy analytics on, and shows your playtest's feedback form. In the
> editor it checks your setup and records test videos.

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

1. **Protokite > Playtest > Settings** (or **Open Playtest Settings** on Flock's Playtesting tab). The settings are created
   the first time, at `Assets/Resources/ProtokitePlaytestSettings.asset`, with playtesting **off**.
2. Tick **Playtesting Enabled**.
3. Set your Flock **Game Version** to your playtest's version name (it starts with `pt-`).

The Protokite API URL is filled in for you (`https://api-protokite.qwacks.com`); change it only to point at a local
Protokite, such as `http://localhost:8020`. Your Flock API key and Game Version are used as they are; there is
nothing else to enter.

## Check your setup

**Protokite > Playtest > Setup Checks And Test Video** (or **Check Playtest Setup** on Flock's Playtesting tab) checks what
a build of this project needs for a playtest, and says what to change for each check that fails, with a button that opens
the place to change it:

| Check | Passes when |
|---|---|
| Playtesting | the playtest settings exist and **Playtesting Enabled** is on |
| Protokite API URL | it is an http or https address with a host and no spaces, by the same rule the game uses |
| Game Version | Flock's **Game Version** is a playtest's name, `pt-<test id>`, resolved to the ID a build sends |
| Video | the build target is 64-bit Windows, x64 |

- **Set Game Version by its name, never by pasting an ID.** Protokite's test page shows the playtest version's ID, but Flock
  resolves Game Version by name: an ID pasted into Game Version resolves to nothing, so a build keeps sending the ID resolved
  before, and an ID pasted over the resolved ID is replaced the next time Game Version is resolved. The window asks Flock,
  with your API key, which version the ID is, and when that version is a playtest's whose name resolves back to it in your
  game, offers **Set Game Version To pt-...**, which sets the name and the ID it resolves to. Only a playtest's name is ever
  offered.
- The Game Version is checked with Flock when the window opens and when the Flock settings change; **Check Again** asks again.
  When Flock cannot be asked, the check goes by what the settings hold and says so.
- The Video check reads the active build target: players built for another platform, or for 32-bit or ARM64 Windows, record
  no video, and everything else in the playtest still runs there.
- Game Version is read from the `FlockConfig` asset in a Resources folder, where the Flock SDK's own start-up reads it.

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
| `WaitingForPlayerConsent` | The playtest is loaded, and nothing is collected until the player answers the consent question |
| `PlayerRefusedPlaytest` | The player asked the playtest to collect nothing, so it behaves as with playtesting off, except that a feedback form they send still goes |

A refusal (`PlaytestNotLinked`, `ProtokiteRefusedApiKey`, `PlaytestConfigForAnotherVersion`) is not asked again until the Flock SDK
is started again (or the game relaunched), since the answer would be the same.

## The player's consent

Before a playtest build collects anything, it asks its player what the playtest may collect: **the screen and play data**,
**the screen only**, **play data only**, or **nothing**. Nothing is recorded, measured or sent, and no Protokite session is
started, until they answer, and **nothing** reads like **Playtesting Enabled** off, except that a feedback form the player
sends themselves still goes: it is their own message, not something the playtest collects. The question says in its own words
that it is the playtest's, separate from any privacy or analytics choice the game asks about: it does not change the Flock
SDK's analytics consent, and exceptions the Flock SDK captures are the Flock SDK's, whatever the answer.

The question is drawn over the game once this build's playtest has loaded, with UI Toolkit built from code: nothing to
import, and no EventSystem of the game's needed; it works with the Input Manager, the Input System or both. It is answered
with the mouse: while it is on screen the cursor is shown and free, and it goes back to how the game had it once the player
answers. No answer is selected when it appears, so the game's own Submit key (Space, by default) presses nothing, and a
press in its first half second is ignored. A game that locks the cursor again every frame puts each click at the screen's
centre, so no click is taken as an answer there and a warning says so: stop locking it while `IsConsentQuestionOpen`.

The answer is kept in `ProtokitePlaytest/playtest_consent.json` under the game's persistent data folder and used by every
later launch. **An answer already given counts even in a build that stops asking.** Each session start carries it in its
debug facts as `playtest_consent` (`video_and_play_data`, `video_only`, `play_data_only`, `nothing` or `not_answered`) and
`playtest_consent_asked` (`true` or `false`), so a session with no recording reads as a player who asked for none.

| Setting | Default | |
|---|---|---|
| Ask The Player For Playtest Consent | on | Put the question, and collect nothing until it is answered. Turn it off only where players were asked another way, or for a test run with nobody to answer; everything the playtest turns on is then collected, and each session says nobody was asked |

```csharp
ProtokitePlaytest.PlaytestConsent                 // the answer in force
ProtokitePlaytest.SetPlaytestConsent(choice)      // your own menu answers it; NotAnswered asks again
ProtokitePlaytest.AskForPlaytestConsent()         // put the question on screen again, to change the answer
ProtokitePlaytest.IsConsentQuestionOpen
```

- **Taking the screen back deletes this launch's recording**, rather than keeping it for a later launch to send. One already
  uploading goes on.
- **Recordings earlier launches kept wait** while the question is on screen (and while the config that decides whether it is
  put is on its way), and for as long as the answer is **nothing**, even with Playtesting Enabled off. They are kept, and go
  in the same launch the player changes their mind.
- A game that runs in batch mode or without graphics cannot show the question, so it collects nothing and says so once:
  answer with `SetPlaytestConsent`, or turn asking off.
- In the editor, **Protokite > Playtest > Ask The Player Again** forgets the answer, so the next Play asks again.

## What the playtest turns on

`ProtokitePlaytest.Config` is the loaded config (null until `Ready`): the playtest's `TestId`, its feature switches and
its feedback form (`Form`, null when the playtest has none). Check a feature with
`ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording)`; a feature the config does not mention is off,
and so is one the player's answer does not allow (a feature this build does not know needs the answer that allows everything).

## The feedback form

When your playtest publishes a feedback form in Protokite, the player opens it over the game with **F9** and fills it in.
It is built from what you published, every question, label, help text and option, so editing the form in Protokite needs no
new build: text, many-line text, a rating from 1 to 5, a choice of options, and a checkbox. A question of a kind this
package does not know yet is a text box, which is how Protokite reads it. It is the player's own message, so it opens and
sends **whatever their consent answer**, "nothing" included; it never opens over the consent question.

- **Send checks the answers the way Protokite does**, and shows every problem against its question before anything is sent:
  a required question left empty, a rating out of range, an option the question does not offer. A required checkbox counts
  when it is left unticked, and an empty optional answer is left out rather than sent empty.
- **A sent form is kept on the device first and sent from there**, so neither a closed game nor a lost network loses it: one
  that could not go now is sent when the network comes back, two minutes later, or by a later launch. One Protokite refuses
  for good is deleted, and the log names the question it refused. It names this launch's Protokite session once one has
  started, and is sent with the Game Version ID it was filled in under.
- A form that names no session, whose send failed after Protokite may already have stored it, is sent again: the studio may
  see that report twice rather than lose it. One naming a session is replaced, never added.
- Closing it without sending keeps what was typed for the rest of the launch. Escape in a text box does nothing (Unity's own
  text box would put back what it held before), and a click puts the caret where it lands rather than selecting everything.
- **Upload your recording**: while this launch records the screen and its session has started, the form offers to stop the
  recording and send it now. Opening the form never stops the recording on its own.
- While the form is open the cursor is shown and free. **The game still reads its own keys**: a game that moves on WASD, or
  opens a menu on Escape, should ignore its input while `ProtokitePlaytest.IsFeedbackFormOpen`, or turn on **Pause The Game
  While The Form Is Open**. A click in the form's first half second is ignored, and so is one while the game locks the cursor
  again every frame (a warning says so).
- With the old Input Manager, Unity's runtime UI misses the odd quick click, or takes it where the cursor has moved to (up to
  10 in 100 measured; the Input System missed none). Nothing wrong is sent: the player sees the click did nothing, and clicks again.

| Setting | Default | |
|---|---|---|
| Feedback Form Key | F9 | Opens the form and closes it again, also while the player types in it. Click **Detect Key** and press the key (Escape or **Cancel** keeps the old one), or choose it from the list. Read when the playtest loads; None leaves opening it to your game |
| Pause The Game While The Form Is Open | off | The time scale is 0 while the form is open, and put back when it closes unless the game set another one meanwhile |

```csharp
ProtokitePlaytest.CanOpenFeedbackForm            // a form to open: leave your "give feedback" button out when false
ProtokitePlaytest.OpenFeedbackForm()             // and CloseFeedbackForm(), IsFeedbackFormOpen
ProtokitePlaytest.CanSendTheRecording            // and StopRecordingAndSendIt(), what the form's recording button does
```

A game drawing a form of its own reads the questions from `ProtokitePlaytest.FeedbackForm` and sends the answers through the
same checking and keeping:

```csharp
ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
answers.SetRating("rating", 4);
answers.SetChosenOption("category", "Bug");
answers.SetText("title", "Fell through the floor");
answers.SetChecked("contact", false);
foreach (ProtokitePlaytestFormProblem problem in answers.FindProblems(ProtokitePlaytest.FeedbackForm))
    Debug.Log(problem.FieldId + ": " + problem.Message);
ProtokitePlaytest.SendFeedbackForm(answers);   // false, with a warning, when there are problems
```

Forms waiting to be sent are kept in `ProtokitePlaytest/FeedbackForms` under the game's persistent data folder, one file each,
with no API key in them. In the editor, **Protokite > Playtest > Open The Feedback Form** opens it in Play Mode.

## Sessions

Each launch runs **one** Protokite session. It starts once the playtest's config is loaded, the player's answer lets it collect
something, and a Flock session has reached
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

### Test videos

In Play Mode, **Record Test Video** in **Protokite > Playtest > Setup Checks And Test Video** records the Game view for the
number of seconds you set, with the video settings below and no playtest needed: playtesting off, nobody signed in, whatever
the player answered the consent question, since a test video is never sent. It is written to
`ProtokitePlaytest/Recordings/TestVideos/` under the game's persistent data folder, is never uploaded, and is kept until making
room for a later recording deletes it; the window shows where it went.

- It makes room for what its own length records at its bitrate (at least 1 MB), never more, and never grows past that. Room is
  made by deleting older test videos only, never a recording waiting to upload; with too little left, it is not recorded and
  the window says why.
- One at a time, and never beside the playtest's own recording: it is refused while the playtest records or is about to, and
  one recording when the playtest's config turns video on stops there, keeping what it recorded, so the playtest's recording
  starts as it would have.
- It starts at the end of a frame, once the recordings earlier launches left are gone through. Quitting finishes it, as it does
  the playtest's.
- A playtest collecting play data in the same Play session measures the test video's cost with the game's.
- 64-bit Windows editors only, as video recording is.

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
strands what a playtest build recorded; with it off, nothing else is recorded or sent. They wait while the player's answer is
**nothing**, or while the consent question may still be put (see [The player's consent](#the-players-consent)).

Every refusal keeps the recording, including Protokite saying the session belongs to another playtest (403) or no longer
exists (404): a later launch asks again. Such a recording goes only when a launch that records needs its room in the disk
budget, so a build that never records keeps asking for it, and says so in the log each launch.

### Where recordings are kept

Each recording has a folder of its own under the game's persistent data folder,
`ProtokitePlaytest/Recordings/Playtest/<UTC time>-<8 hex digits>/` (a test video's under `TestVideos/`, with no session), holding the video, `session.json` (the Protokite session
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
recordings whose game has closed are deleted until it fits: test videos first, then recordings waiting to upload, the oldest
first. A recording starts once the folders above have
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

**On WebGL** the playtest's files (the consent answer, the device id and feedback forms waiting to be sent) are copied to the
browser's storage after every change, so they are there on the player's next visit wherever the browser keeps the site's data
(a private window forgets it when closed, and a browser where the player blocked site data keeps none). A change made in the
moment before the tab closes may not finish copying, and two tabs of one game share one storage, where the last to copy wins.

## Third-party software

The Windows video encoder contains **libvpx 1.17.0** (VP8 and VP9), © The WebM Project authors, under a BSD licence with an
additional patent grant: see `Runtime/Plugins/x86_64/libvpx-LICENSE.txt` and `libvpx-PATENTS.txt`, which ship with it.

## Remove it

Flock's **Playtesting** tab has a **Remove** button, or remove it as you installed it (delete
`Assets/ProtokitePlaytest`, or remove it in Package Manager). Your settings asset stays until you delete it.
