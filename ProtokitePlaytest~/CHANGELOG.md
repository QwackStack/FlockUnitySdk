# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).
It is released with the Flock SDK, at the Flock SDK's version.


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
