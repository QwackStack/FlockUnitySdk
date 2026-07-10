using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // GameProvider reads: game / game-version info (cached once per launch) and by-name version lookup
    // (BootstrapScope, cached in-memory), plus not-found + validation.
    public class FlockGameProviderTests
    {
        // ---- GAME-01: game info returns and is cached once per launch ----
        [Test]
        public void GetGame_Success_ReturnsAndCachesOncePerLaunch()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Game, FlockFakeTransport.Ok("{\"result\":{\"id\":\"game-1\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                GameSchema first = h.Run(() => h.Client.Game.GetGameAsync());
                GameSchema second = h.Run(() => h.Client.Game.GetGameAsync());

                Assert.IsNotNull(first);
                Assert.AreSame(first, second, "Game info is cached in-memory once per launch.");
                Assert.AreEqual(1, transport.CountTo(FlockEndpoints.Game), "Second call served from cache, not the network.");
            }
        }

        // ---- GAME-03: not-found surfaces ----
        [Test]
        public void GetGame_NotFound_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.Game, FlockFakeTransport.Coded(404, "game.not_found"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Game.GetGameAsync()));
            }
        }

        // ---- GAME-02: by-name version lookup includes the name in the URL and caches ----
        [Test]
        public void GetGameVersionByName_IncludesNameInUrl_AndCaches()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.Default(request => FlockFakeTransport.Ok("{\"result\":{\"id\":\"gv-1\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.SetReachable(true);

                GameVersionSchema first = h.Run(() => h.Client.Game.GetGameVersionByNameAsync("myver"));
                GameVersionSchema second = h.Run(() => h.Client.Game.GetGameVersionByNameAsync("myver"));

                Assert.IsNotNull(first);
                Assert.AreSame(first, second, "By-name version is cached in-memory.");
                Assert.AreEqual(1, transport.CountTo("myver"), "Second by-name call served from cache; the URL carried the name.");
            }
        }

        // ---- GAME (validation) ----
        [Test]
        public void GetGameVersionByName_EmptyName_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Game.GetGameVersionByNameAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }
    }
}
