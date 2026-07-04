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

// Config schemas are an internal/codegen concern — the Schema getters are not part of the public API.

// Game info
var game = await FlockClient.Instance.Game.GetGameAsync();
var version = await FlockClient.Instance.Game.GetGameVersionAsync();
var versionByName = await FlockClient.Instance.Game.GetGameVersionByNameAsync("v1.0.0");
```

See also: [Codegen](codegen.md) for how the typed config accessors are generated and kept in sync.
