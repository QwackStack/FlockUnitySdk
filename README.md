# Flock Unity SDK

The Flock Unity SDK provides access to Flock's game backend services from Unity games.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Requirements](#requirements)
- [Setup](#setup)
  - [Editor Configuration](#editor-configuration)
  - [Automatic Initialization](#automatic-initialization-default)
  - [Drop-in Initialization](#drop-in-initialization)
  - [Code-Based Configuration](#code-based-configuration)
- [Quick Start](#quick-start)
- [Feature guides](#feature-guides)
- [Offline caching](#offline-caching)
- [Platform notes](#platform-notes)

## Features

- Player authentication (email, device, Google, Apple, Steam, Facebook, Discord)
- Token refresh with automatic silent retry, plus a session-expired event
- Game configuration — typed accessors generated from your schemas (via codegen)
- Game and game version metadata (lookup by ID or name)
- Player data — read with pagination; typed player-data and template accessors via codegen
- Shop (browse shops, items, purchase, inventory)
- Game commands (server-side operations: update player data, add funds)
- Player ban lookup
- Asset listing and lookup by ID
- Analytics (session tracking, events, transactions — no-op safe when disabled)
- Automatic retry with exponential backoff
- Offline-safe init (no network at startup) — plus disk-cached static content that keeps serving without network after one online session
- JWT token management
- Strongly typed codegen for player templates, game configs, and game commands (via the Codegen tab in **Flock > Settings**)
- Hands-off startup — the SDK auto-initializes from your config at launch (or use the drop-in `FlockBootstrap` component, or call `Create` yourself)

## Installation

The SDK is distributed through the Flock website — download the latest release, then add it to your Unity project one of two ways:

**Download: [github.com/QwackStack/FlockUnitySdk/releases](https://github.com/QwackStack/FlockUnitySdk/releases)**

- **Import the `.unitypackage`** — double-click the downloaded file (or use **Assets > Import Package > Custom Package**) and import all items.

## Requirements

- **Unity 2020.3 or newer.**
- **Newtonsoft Json for Unity** (`com.unity.nuget.newtonsoft-json`, 3.0.2+) — the SDK depends on it for serialization. Installing through the Package Manager git URL resolves this automatically; if you import the `.unitypackage` instead, add it first via **Window > Package Manager > + → Add package by name** using `com.unity.nuget.newtonsoft-json`.

## Setup

### Editor Configuration

Open **Flock > Settings** in the Unity menu bar. The window is a view of the `FlockConfig` ScriptableObject — edits save straight into the asset (no separate Save step). Required values:

- **API URL** — Flock API endpoint (default: `https://api-flock.qwacks.com`)
- **API Key** — Your Flock API key
- **Game ID** — Your game ID from the Flock dashboard
- **Game Version** — Your game version name (the matching ID is resolved from the backend at edit time and baked into the asset, so init makes no network call)

The asset is saved to `Assets/Resources/FlockConfig.asset` so it loads in builds via `Resources.Load<FlockConfigAsset>("FlockConfig")`. The same window has a Codegen tab that runs Sync Schemas (see the [Codegen guide](Docs~/codegen.md)).

### Automatic Initialization (default)

The SDK starts itself — no component, no init code. With `FlockConfig` set up (above), **Auto-Initialize On Load** is on by default, so the SDK initializes from `Assets/Resources/FlockConfig.asset` before the first scene loads and restores a saved session in the background. Just use `FlockClient.Instance` when you need it.

```csharp
using Flock;

// Nothing to call at startup. Optionally react to the events (safe to subscribe anytime):
FlockEvents.OnInitialized     += ()       => Debug.Log("Flock SDK ready");
FlockEvents.OnSessionRestored += signedIn => Debug.Log(signedIn ? "Session resumed" : "Show login");
```

To drive init yourself — e.g. to defer past a splash screen or EULA — turn **Auto-Initialize On Load** off (Flock > Settings → Advanced Settings → Tools), then use one of the options below.

> **Init is fail-fast.** A bad config makes `Create` throw (the auto-init path catches and logs it instead of crashing startup). Either way the SDK stays uninitialized and `FlockClient.Instance` throws until a successful init — so guard with `FlockClient.IsInitialized`, inspect `FlockClient.InitializationError`, or handle `FlockEvents.OnInitializationFailed`.

### Drop-in Initialization

Prefer a scene component (or turned auto-init off)? Click **Add Flock Bootstrap to Scene** in the editor window (or add a `FlockBootstrap` component to a GameObject yourself). The component holds a reference to your `FlockConfig` asset and calls `FlockClient.Create(asset.ToInitConfig())` in `Awake`.

```csharp
// Optional: gate init or react to result
var bootstrap = FindObjectOfType<FlockBootstrap>();
bootstrap.OnInitialized += () => Debug.Log("Flock SDK ready");
bootstrap.OnInitializationFailed += ex => Debug.LogError(ex);

// Or trigger init manually if you turned off "Initialize On Awake"
bootstrap.Initialize();
```

The component never copies values from the asset — it always reads through the reference, so editing the asset (here or in the inspector) is enough to change runtime behavior. Recommended pattern: place the bootstrap in a Boot scene loaded once at startup, leave **Don't Destroy On Load** on.

### Code-Based Configuration

For full manual control, turn **Auto-Initialize On Load** off and create the client yourself:

> **Important:** the SDK must be initialized **once** at startup before any
> feature accesses it. After `Create` returns, call everything via
> `FlockClient.Instance` from anywhere in your project. Accessing
> `FlockClient.Instance` before init throws a `FlockException`.

```csharp
var configAsset = Resources.Load<FlockConfigAsset>("FlockConfig");

// Create is synchronous and makes no network call. The Game Version ID was
// resolved at edit time (Flock > Settings) and baked into FlockConfig, applied
// to every request; init stores the singleton in FlockClient.Instance.
FlockClient.Create(configAsset.ToInitConfig());

// Raw Create does NOT resume a saved session — unlike Auto-Initialize On Load and
// FlockBootstrap, which restore it for you. If you persist sessions, do it yourself
// (this drives FlockClient.IsRestoringSession and FlockEvents.OnSessionRestored):
bool signedIn = await FlockClient.Instance.Authentication.TryRestoreSessionAsync();

// Use anywhere — no need to pass the client around.
var response = await FlockClient.Instance.Authentication.LoginWithEmailAsync(email, password);
```

## Quick Start

The shortest path from zero to reading data — initialize once at startup, log a player in, then read something back. Every feature follows the same pattern; see the [Feature guides](#feature-guides) for the full surface.

```csharp
using Flock;

// 1. With Auto-Initialize On Load on (the default), the SDK is already running by the time
//    your scene starts — just use FlockClient.Instance. (Opted out? Call
//    FlockClient.Create(Resources.Load<FlockConfigAsset>("FlockConfig").ToInitConfig()) once first.)

// 2. Log the player in. Auth methods throw on failure.
await FlockClient.Instance.Authentication.LoginWithDeviceAsync("device-uuid");

// 3. Read something back — call any service via FlockClient.Instance.
var game = await FlockClient.Instance.Game.GetGameAsync();
Debug.Log($"Signed in as {FlockClient.Instance.CurrentPlayerId}, playing {game.Name}");
```

## Feature guides

Per-feature usage and examples live in their own guides:

| Guide | Covers |
|-------|--------|
| [Authentication](Docs~/authentication.md) | Login/register for every provider, logout, token refresh, session expiry |
| [Game & Config](Docs~/game-config.md) | Typed game-config accessors, game/version metadata |
| [Player Data & Game Commands](Docs~/player-data.md) | Reads, typed template accessors, update commands, funds, achievements, bans |
| [Shop](Docs~/shop.md) | Shops, items, purchase (money-safety contract), player inventory |
| [Assets](Docs~/assets.md) | Listing/lookup, typed downloads, disk cache, preloading |
| [Analytics](Docs~/analytics.md) | Sessions, logs/events, transactions, consent, unexpected-termination detection |
| [SDK Events](Docs~/events.md) | The `FlockEvents` hub — lifecycle, auth, and session events |
| [Codegen](Docs~/codegen.md) | Sync Schemas, generated templates/configs/shops/achievements, content catalog |

## Offline caching

Reads are snapshotted to disk and served when the server is unreachable, after at least one online session — the server is always tried first, with no TTLs. Toggle with `EnableOfflineCache` (default `true`; set `false` on WebGL — see [Platform notes](#platform-notes)). Each service exposes `ClearCache()`; bans, inventory, and purchases are never cached. See [ARCHITECTURE.md](ARCHITECTURE.md) for what refreshes when.

## Platform notes

- **WebGL**: SDK HTTP automatically switches to `UnityWebRequest` on WebGL builds
  (`System.Net.Http.HttpClient` has no WebGL transport), so auth, read APIs, and
  analytics work online with no caller changes. The asset cache and the offline
  snapshot cache, however, are backed by `Application.persistentDataPath`, which on
  WebGL is IndexedDB-backed and does not support synchronous file writes
  (`File.WriteAllBytes` will fail). Set `FlockInitConfig.EnableAssetCache = false`
  and `FlockInitConfig.EnableOfflineCache = false` on WebGL builds — everything else
  works online, just without the disk caches. See the "Offline caching" section.
