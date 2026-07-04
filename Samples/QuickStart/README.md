# Flock Quick-Start Sample

A one-script demo of the shortest path to "it works": log in, see your player, fire a test
analytics event, and read your player data.

## Setup (about a minute)

1. **Configure Flock** — open **Flock > Settings** and fill in API URL / API Key / Game ID / Game Version (from your Flock dashboard).
2. **Add the bootstrap** — in the same window, click **Add Flock Bootstrap to Scene**. This initializes the SDK when you press Play.
3. **Add the sample** — create an empty GameObject and add the **FlockQuickStartSample** component (the script in this folder).
4. **Press Play**, then use the on-screen buttons: **Log in (device)**, then **Fire test event** and **Read my player data**.

## What it shows

- Device login — `FlockClient.Instance.Authentication.LoginWithDeviceAsync(...)`
- The authenticated player — `CurrentPlayerId`, `CurrentSessionId`
- A test analytics event — `Analytics.LogEvent(...)`
- Reading player data — `Player.GetAllDataAsync()`

## What it doesn't show

Player-data **writes**. Those go through your project's generated commands (run **Flock > Sync
Schemas** to produce typed accessors), which are specific to your backend schema — a dedicated
per-feature sample will cover that.
