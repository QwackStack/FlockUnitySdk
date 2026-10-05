using System.Runtime.CompilerServices;

// The status rules are checked directly by the package's own tests.
[assembly: InternalsVisibleTo("Protokite.Playtest.Tests.Editor")]
// The package's editor window checks setup with the runtime's own rules and records test videos through it.
[assembly: InternalsVisibleTo("Protokite.Playtest.Editor")]
[assembly: InternalsVisibleTo("Protokite.Playtest.Tests.PlayMode")]
