using System.Collections.Generic;

namespace Flock.Editor
{
    // Declarative list of optional SDK providers used by the Package Builder.
    //
    // To add a new provider:
    //   1. Add an entry below with its Id, DisplayName, the runtime files it owns,
    //      and any provider Ids it hard-depends on.
    //   2. Wrap its field/property/init in FlockClient.cs and its property in
    //      IFlockClient.cs with #if !FLOCK_NO_<ID> ... #endif (matching the Id).
    // This might needs revisitng 
    // Models are intentionally not partitioned here being tiny
    // keeping them always-shipped avoids cross-provider model coupling issues.
    internal static class FlockProviderManifest
    {
        public sealed class Entry
        {
            public string Id;
            public string DisplayName;
            public string Description;
            public string[] Files;
            // Whole-tree exclusions — every file under any of these prefixes is dropped from
            // the build. Use `Folders` when a provider owns a directory whose contents change
            // over time (e.g. the codegen folder) and listing every file in `Files` would rot.
            public string[] Folders;
            public string[] DependsOn;
        }

        public static readonly IReadOnlyList<Entry> Providers = new[]
        {
            new Entry
            {
                Id = "CONFIG",
                DisplayName = "Config",
                Description = "Game patches and feature configs.",
                Files = new[]
                {
                    "Runtime/Providers/FlockConfigProvider.cs",
                    "Runtime/Interfaces/IConfigProvider.cs",
                },
                // FlockConfigProvider / IConfigProvider expose SchemaTag-typed methods,
                // so Config can't ship without the Schema enum.
                DependsOn = new[] { "SCHEMA" },
            },
            new Entry
            {
                Id = "SCHEMA",
                DisplayName = "Schema",
                Description = "Read-only access to game config schemas.",
                Files = new[]
                {
                    "Runtime/Providers/FlockSchemaProvider.cs",
                    "Runtime/Interfaces/ISchemaProvider.cs",
                },
                // Codegen consumes SchemaTag from ISchemaProvider, so the editor codegen
                // tree only ships when Schema does.
                Folders = new[] { "Editor/Codegen/" },
                DependsOn = new string[0],
            },
            new Entry
            {
                Id = "GAME",
                DisplayName = "Game",
                Description = "Game and game-version lookups.",
                Files = new[]
                {
                    "Runtime/Providers/FlockGameProvider.cs",
                },
                DependsOn = new string[0],
            },
            new Entry
            {
                Id = "PLAYER",
                DisplayName = "Player",
                Description = "Player data, templates, and ban lookups.",
                Files = new[]
                {
                    "Runtime/Providers/PlayerProvider.cs",
                    "Runtime/Interfaces/IPlayerService.cs",
                },
                DependsOn = new string[0],
            },
            new Entry
            {
                Id = "COMMANDS",
                DisplayName = "Commands",
                Description = "Server-side game commands.",
                Files = new[]
                {
                    "Runtime/Providers/FlockCommandProvider.cs",
                },
                DependsOn = new string[0],
            },
            new Entry
            {
                Id = "ANALYTICS",
                DisplayName = "Analytics",
                Description = "Sessions, events, transactions, log events.",
                Files = new[]
                {
                    "Runtime/Providers/Analytics/FlockAnalyticsProvider.cs",
                    "Runtime/Providers/Analytics/NullAnlayticProvider.cs",
                    "Runtime/Interfaces/IAnalyticProvider.cs",
                },
                DependsOn = new string[0],
            },
            new Entry
            {
                Id = "SHOP",
                DisplayName = "Shop",
                Description = "Shops, items, purchases, inventory.",
                Files = new[]
                {
                    "Runtime/Providers/FlockShopProvider.cs",
                },
                // Shop's purchase flow records analytics transactions unconditionally.
                DependsOn = new[] { "ANALYTICS" },
            },
            new Entry
            {
                Id = "ASSET",
                DisplayName = "Asset",
                Description = "Remote asset metadata, downloads, and cache.",
                Files = new[]
                {
                    "Runtime/Providers/FlockAssetProvider.cs",
                    "Runtime/Providers/FlockAssetCache.cs",
                    "Runtime/Interfaces/IAssetProvider.cs",
                },
                DependsOn = new string[0],
            },
        };

        public static Entry Find(string id)
        {
            foreach (Entry e in Providers)
            {
                if (e.Id == id) return e;
            }
            return null;
        }

        // Walks DependsOn transitively from `seed` and adds every reached id to `result`.
        public static void CollectDependenciesOf(string id, HashSet<string> result)
        {
            Entry entry = Find(id);
            if (entry == null) return;
            foreach (string dep in entry.DependsOn)
            {
                if (result.Add(dep))
                    CollectDependenciesOf(dep, result);
            }
        }

        // Walks the reverse-dep graph from `id` and adds every provider that
        // (transitively) depends on it to `result`.
        public static void CollectDependentsOf(string id, HashSet<string> result)
        {
            foreach (Entry e in Providers)
            {
                foreach (string dep in e.DependsOn)
                {
                    if (dep == id && result.Add(e.Id))
                        CollectDependentsOf(e.Id, result);
                }
            }
        }
    }
}
