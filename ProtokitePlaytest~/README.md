# Protokite Playtest

Playtesting for games built with the Flock SDK: each play session, gameplay recording and in-game feedback,
reported to your Protokite playtest.

> **Early version.** This release sets the package up: install it, switch it on, point it at Protokite. Sessions,
> recording and the feedback form arrive in the releases that follow.

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

`ProtokitePlaytest.Status` says whether the playtest can run, and if not, why:

| Status | Meaning |
|---|---|
| `TurnedOff` | Playtesting is switched off, or the project has no playtest settings |
| `ProtokiteApiUrlMissing` | The Protokite API URL is empty |
| `ProtokiteApiUrlUnusable` | The Protokite API URL is not an http or https address |
| `WaitingForFlock` | Everything is set; the Flock SDK has not started yet |
| `Ready` | The playtest can run |

## Remove it

Flock's **Playtesting** tab has a **Remove** button, or remove it as you installed it (delete
`Assets/ProtokitePlaytest`, or remove it in Package Manager). Your settings asset stays until you delete it.
