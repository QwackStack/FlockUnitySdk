# Flock Quick-Start Sample

A one-script demo of the shortest path to "it works": sign in, see your player, send a test
analytics event, and read your player data.

## Setup (about a minute)

1. **Configure Flock** — open **Flock > Settings** and, on the **Configuration** tab, fill in **Api Url**, **Api Key**,
   **Game Name** and **Game Version** (from your Flock dashboard). The window resolves the Game Version by itself; wait
   for its **Resolved Version ID** to fill in.
2. **Add the sample** — create an empty GameObject and add the **FlockQuickStartSample** component (the script in this
   folder). No bootstrap is needed: with **Auto-Initialize On Load** on (the default, on the **Advanced Settings** tab) the
   SDK starts itself when you press Play. Only with it off, click **Add to Scene** on the **Scene bootstrap** row of the
   **Configuration** tab's **Setup** card.
3. **Press Play**, then use the on-screen buttons: **Log in (device)**, then **Fire test event** and **Read my player data**.

The first time you press **Log in (device)** on a computer, Flock does not know the device yet: the sample registers it
as a new player, which also signs it in. Later runs log in as that player.

The sample passes no device id, so both calls use the one Flock makes on the first launch and keeps for this install
(`Authentication.ThisDeviceId`), as the [README's Quick Start](../../README.md#quick-start) does.

## What it shows

- Signing in with a device — `FlockClient.Instance.Authentication.LoginWithDeviceAsync()`, and on
  `FlockErrorCode.PlayerInvalidLoginCredentials` (a device Flock has not seen) `RegisterWithDeviceAsync()`
- The signed-in player — `CurrentPlayerId`, `CurrentSessionId`
- A test analytics event — `Analytics.TrackEvent(...)`
- Reading player data — `Player.GetAllDataAsync()`

The [README's Quick Start](../../README.md#quick-start) has the same steps as one script to copy.

## What it doesn't show

Player-data **writes**. Those go through the methods codegen writes for your templates (click **Sync Schemas** on the
**Code Generation** tab of **Flock > Settings**), which depend on your own dashboard; see the
[Codegen guide](../../Docs~/codegen.md). The multiplayer samples are in `Samples/Multiplayer`, the shop sample in
`Samples/Shop`.
