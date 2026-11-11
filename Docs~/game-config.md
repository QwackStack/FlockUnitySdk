# Game & Game Configuration

[← Back to README](../README.md)

## Game configuration

Game configs (difficulty numbers, texts, feature switches) are read through the typed methods codegen writes: run
**Sync Schemas** on the **Code Generation** tab of **Flock > Settings**, and each config gets a `Get<Config>Async()`
method on `FlockClient.Instance.Config` returning a class of its own. The names below (`GetGameplayAsync`,
`GameplayConfig`) are for a config named `Gameplay`; they exist only after a sync, and yours follow your dashboard.

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Generated.Configs;
using UnityEngine;

public static class GameplaySettings
{
    public static async Task ApplyAsync()
    {
        GameplayConfig gameplay = await FlockClient.Instance.Config.GetGameplayAsync();
        Debug.Log($"Lives: {gameplay.MaxLives}, spawn rate: {gameplay.SpawnRate}");
    }
}
```

The untyped reads are internal: the server answers in the shape codegen expects, so the generated methods are the way to
read configs.

### Values for the signed-in player

What a read gives depends on whether a player is signed in:

- **Nobody signed in**: the game-wide values, this build's Game Version's newest patch for the config, or the config's own
  values when no patch changes it.
- **A player signed in**: for a gameplay or feature config, the values the server resolves for that player: the newest
  patch, then the overrides of the segments the player is in, then the player's own overrides. Overrides apply to those two
  kinds only, so any other config (currencies, achievements, assets) reads as it does with nobody signed in. Once the
  server has answered, the SDK holds the player's values for that sign-in, so a later read makes no call; a new sign-in
  (another player, or the same one again) or `ClearCache()` reads them afresh. When the player's read gets no answer, the
  read returns the player's saved values (see Offline below), else the game-wide ones, and the player's read is sent
  again on a read 30 seconds or more later.

So a game that reads a config before the player signs in (at startup, on a title screen) should read it again once they
have, or it keeps showing the game-wide values:

```csharp
using Flock;
using Flock.Exceptions;
using Flock.Generated.Configs;
using UnityEngine;

public class GameplaySettingsForThePlayer : MonoBehaviour
{
    private void OnEnable()
    {
        FlockEvents.OnAuthenticated += HandleAuthenticated;
    }

    private void OnDisable()
    {
        FlockEvents.OnAuthenticated -= HandleAuthenticated;
    }

    // A read before the sign-in gave the game-wide values: read again for this player.
    private async void HandleAuthenticated(FlockAuthInfo info)
    {
        try
        {
            GameplayConfig gameplay = await FlockClient.Instance.Config.GetGameplayAsync();
            Debug.Log($"Lives for {info.PlayerId}: {gameplay.MaxLives}");
        }
        catch (FlockException ex)
        {
            Debug.LogError(ex.Message);
        }
    }
}
```

**Offline**: with the offline cache on (**Flock > Settings**, **Advanced Settings** tab, **Offline Cache** card), each
player's values are kept on disk for that player once one online read has fetched them: a signed-in player gets their own
last values, never another player's, and a player whose values were never read online gets the game-wide ones. A
refusal saying the player or the config is not found, or that there is no Game Version to read, deletes that player's
saved copy, and the game-wide values answer instead.

**No Game Version**: the server resolves a player's values for the Game Version the request names, else the one the player
last signed in with. Only when it has neither (the build sends none, as no Game Version is resolved in **Flock >
Settings**, and none is recorded for the player) does the read return the game-wide values, with one warning in the
Console for the sign-in. Resolve the Game Version in **Flock > Settings > Configuration**; the player then gets their own
values from their next signed-in read.

## Game info

```csharp
using System.Threading.Tasks;
using Flock;
using Flock.Models;
using UnityEngine;

public static class GameInfo
{
    public static async Task ShowAsync()
    {
        GameSchema game = await FlockClient.Instance.Game.GetGameAsync();
        GameVersionSchema version = await FlockClient.Instance.Game.GetGameVersionAsync();
        Debug.Log($"{game.Name}, version {version.Name}");
    }

    // Another Game Version of the same game, by its name.
    public static Task<GameVersionSchema> FindVersionAsync(string versionName)
    {
        return FlockClient.Instance.Game.GetGameVersionByNameAsync(versionName);
    }
}
```

## This build's game and version

```csharp
using Flock;
using UnityEngine;

public static class BuildInfo
{
    public static void Log()
    {
        string gameName = FlockClient.Instance.GameId;             // the Game Name set in Flock > Settings
        string versionName = FlockClient.Instance.GameVersion;     // "1.0.0", as set in Flock > Settings
        string versionId = FlockClient.Instance.GameVersionId;     // the ID the editor resolved that name to
        Debug.Log($"{gameName} {versionName} ({versionId})");
    }
}
```

These say which game and Game Version the client was initialized with, so reading them makes no call: `GameVersion` is the
name set in Flock > Settings, and `GameVersionId` the ID the editor resolved it to and saved into the build.
`GetGameVersionAsync()` fetches that version's record from the server.

See also: [Codegen](codegen.md) for how the typed config accessors are generated and kept in sync.
