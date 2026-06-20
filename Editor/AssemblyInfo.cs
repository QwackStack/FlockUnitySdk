using System.Runtime.CompilerServices;

// Exposes Flock.Editor internals (codegen helpers, build guard, schema hasher) to the EditMode test assembly.
[assembly: InternalsVisibleTo("Flock.Tests.Editor")]
