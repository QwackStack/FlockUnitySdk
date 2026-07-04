# Contributing to the Flock Unity SDK

Thanks for your interest in the SDK. Bug reports and pull requests are welcome —
here's how to make them land smoothly.

## Reporting bugs and requesting features

Use the [issue templates](https://github.com/QwackStack/FlockUnitySdk/issues/new/choose).
For security problems, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Before writing code

For anything beyond a small fix, **open an issue first** and wait for a maintainer
to confirm the direction. The SDK is kept at feature parity with a separate Unreal
SDK, so API changes are weighed against that — an early heads-up avoids wasted work.

## Development setup

This repo **is** the UPM package `com.flock.sdk` (min Unity 2020.3) — there's no
standalone build. To work on it:

1. Create (or open) a Unity project.
2. Clone this repo into the project's `Packages/` folder so it becomes an
   [embedded package](https://docs.unity3d.com/Manual/upm-embed.html) and compiles
   in-place.
3. Run the EditMode tests via **Window > General > Test Runner** (assembly
   `Flock.Tests.Editor`).

## Pull requests

`main` is protected — all changes go through a fork + pull request, reviewed by a
maintainer. Keep each PR scoped to one change; the template checklist is enforced
in review. House rules that trip people up most often:

- **No `var`** — explicit types everywhere.
- **Comments are short one-liners** — no banners, no paragraph blocks; add a *why*
  only where the logic genuinely needs it.
- **Auth methods throw on failure** — they never return error/result states.
- **Never hand-edit `Assets/Flock/Generated/`** (in a consuming project) — that
  tree is codegen output and is wiped on every sync. Generation logic lives in
  `Editor/Codegen/`.

## Releases

Maintainers cut releases by tagging `v<version>` (matching `package.json`); CI
attaches a UPM tarball to the [GitHub release](https://github.com/QwackStack/FlockUnitySdk/releases).
