using System.Runtime.CompilerServices;

// No other assembly names this one, so code stripping would drop it, and with it what makes Unity IAP 5 FlockStore's store library.
[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]

// The tests drive the store rules through a store connection of their own.
[assembly: InternalsVisibleTo("Flock.Tests.Editor")]
[assembly: InternalsVisibleTo("Flock.Tests.PlayMode")]
