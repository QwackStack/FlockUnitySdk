using System.Collections.Generic;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // PlayerProvider reads: by-id happy/not-found/validation, template-scoped fetch, and the never-cached
    // ban path (SNAP-15). CRUD writes go through FlockCommandProvider (covered in FlockCommandProviderTests).
    public class FlockPlayerProviderTests
    {
        // ---- PLYR-01 ----
        [Test]
        public void GetDataById_Success_ReturnsRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerDataById("pd-1"),
                FlockFakeTransport.Ok("{\"result\":{\"id\":\"pd-1\",\"player_id\":\"player-a\"}}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                PlayerData r = h.Run(() => h.Client.Player.GetDataByIdAsync("pd-1"));

                Assert.AreEqual("pd-1", r.Id);
            }
        }

        // ---- PLYR-02 ----
        [Test]
        public void GetDataById_NotFound_Throws()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerDataById("pd-x"), FlockFakeTransport.Coded(404, "player.not_found"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Catch<FlockException>(() => h.Run(() => h.Client.Player.GetDataByIdAsync("pd-x")));
            }
        }

        // ---- PLYR (validation) ----
        [Test]
        public void GetDataById_EmptyId_ThrowsValidation_NoNetwork()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Player.GetDataByIdAsync("")));
                Assert.AreEqual(0, transport.Requests.Count, "Validation short-circuits before any request.");
            }
        }

        // ---- PLYR-06 ----
        [Test]
        public void GetMyDataByTemplate_Success_ReturnsRow()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On("player_data?page=",
                FlockFakeTransport.Ok("{\"items\":[{\"id\":\"pd-1\",\"player_template_id\":\"tpl-1\",\"player_id\":\"player-a\",\"data\":[]}],\"total\":1,\"page\":1,\"limit\":100}"));
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                PlayerData r = h.Run(() => h.Client.Player.GetMyDataByTemplateAsync("tpl-1"));

                Assert.IsNotNull(r);
                Assert.AreEqual("pd-1", r.Id);
            }
        }

        // ---- SNAP-15 (ban never cached): offline -> throws, never served from a snapshot ----
        [Test]
        public void GetBan_Offline_Throws_NeverServedFromCache()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");
                transport.GoOffline();

                Assert.Catch<FlockNetworkException>(() => h.Run(() => h.Client.Player.GetBanAsync("player-a")));
            }
        }

        // ---- PLYR (validation) ----
        [Test]
        public void GetBan_EmptyPlayerId_ThrowsValidation()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            using (FlockTestClient h = FlockTestClient.Create(transport))
            {
                h.LoginAs("player-a");

                Assert.Throws<FlockValidationException>(() => h.Run(() => h.Client.Player.GetBanAsync("")));
            }
        }
    }
}
