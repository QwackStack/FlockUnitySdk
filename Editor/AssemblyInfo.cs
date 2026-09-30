using System.Runtime.CompilerServices;

// Exposes Flock.Editor internals (codegen helpers, build guard, schema hasher) to the EditMode test assembly.
[assembly: InternalsVisibleTo("Flock.Tests.Editor")]
// The maintainers' own project puts the release builders under Qwacks Dev; a studio's project never sees that menu.
[assembly: InternalsVisibleTo("FlockTestRun.Editor")]
