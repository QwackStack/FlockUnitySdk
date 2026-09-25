# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).
It is released with the Flock SDK, at the Flock SDK's version.


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
