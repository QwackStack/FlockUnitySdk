using System.Runtime.CompilerServices;

// The tests drive the store rules through a store connection of their own.
[assembly: InternalsVisibleTo("Flock.Tests.Editor")]
[assembly: InternalsVisibleTo("Flock.Tests.PlayMode")]
