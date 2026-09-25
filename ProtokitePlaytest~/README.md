# Protokite Playtest

Playtesting for games built with the Flock SDK: each play session, gameplay recording and in-game feedback,
reported to your Protokite playtest.

> **Early version.** This release loads your playtest's config and runs one Protokite session per launch. Recording and
> the feedback form arrive in the releases that follow.

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

## Platforms

Everything above runs wherever the Flock SDK runs. **Video is recorded on 64-bit Windows only** (the Editor and players,
Mono and IL2CPP): the package carries its encoder there as `Runtime/Plugins/x86_64/protokite_vpx.dll`. Any other build
leaves the DLL out, records no video, and says so once in the log. That includes 32-bit and ARM64 Windows builds, which
are told video is for 64-bit Windows rather than that the DLL is missing.

## Third-party software

The Windows video encoder contains **libvpx 1.17.0** (VP8 and VP9), © The WebM Project authors, under a BSD licence with an
additional patent grant: see `Runtime/Plugins/x86_64/libvpx-LICENSE.txt` and `libvpx-PATENTS.txt`, which ship with it.

## Remove it

Flock's **Playtesting** tab has a **Remove** button, or remove it as you installed it (delete
`Assets/ProtokitePlaytest`, or remove it in Package Manager). Your settings asset stays until you delete it.
