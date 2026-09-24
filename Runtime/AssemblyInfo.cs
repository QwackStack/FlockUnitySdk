using System.Runtime.CompilerServices;

// Exposes Flock.Runtime internals (FlockAssetCache, etc.) to the test assemblies.
[assembly: InternalsVisibleTo("Flock.Tests.Editor")]
[assembly: InternalsVisibleTo("Flock.Tests.Support")]
[assembly: InternalsVisibleTo("Flock.Tests.PlayMode")]
// The settings window installs the playtest released with this exact version.
[assembly: InternalsVisibleTo("Flock.Editor")]
