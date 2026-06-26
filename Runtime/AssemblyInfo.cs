using System.Runtime.CompilerServices;

// Exposes Flock.Runtime internals (FlockAssetCache, etc.) to the EditMode test assembly.
[assembly: InternalsVisibleTo("Flock.Tests.Editor")]
