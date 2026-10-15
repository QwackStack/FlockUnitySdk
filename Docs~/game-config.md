# Game & Game Configuration

[← Back to README](../README.md)

```csharp
// Game configuration — accessed through codegen. The Codegen tab (Flock > Settings) emits one
// Get<ConfigName>Async() per config returning a generated typed class. Each resolves the current
// game version's patch values, falling back to the config's own data when no patch exists.
var currency = await FlockClient.Instance.Config.GetCurrencyAsync();   // generated
var gameplay = await FlockClient.Instance.Config.GetGameplayAsync();   // generated
// Raw getters (GetAllAsync / GetByIdAsync / GetBySchemaAsync / GetGameConfigsAsync / GetPlayerFeaturesAsync)
// are internal — the server returns a codegen-shaped payload, so generated accessors are the supported path.
// A player's features are kept in memory for the run: the first read of a player fetches, later reads of the
// same player answer from memory until ClearCache() or the next launch. They are never written to disk.

// Config schemas are an internal/codegen concern — the Schema getters are not part of the public API.

// Game info
var game = await FlockClient.Instance.Game.GetGameAsync();
var version = await FlockClient.Instance.Game.GetGameVersionAsync();
var versionByName = await FlockClient.Instance.Game.GetGameVersionByNameAsync("v1.0.0");
```

## This build's game and version

```csharp
string gameId = FlockClient.Instance.GameId;
string versionName = FlockClient.Instance.GameVersion;     // "1.0.0", as set in Flock > Settings
string versionId = FlockClient.Instance.GameVersionId;     // the ID the editor resolved that name to
```

These say which game and Game Version the client was initialized with, so reading them makes no call: `GameVersion` is the
name set in Flock > Settings, and `GameVersionId` the ID the editor resolved it to and baked into the build.
`GetGameVersionAsync()` fetches that version's record from the server.

See also: [Codegen](codegen.md) for how the typed config accessors are generated and kept in sync.
