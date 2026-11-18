using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Flock;
using Flock.Http;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Flock.Tests.PlayMode
{
    // Local players while the game runs: their writes flush on the game's frames and on their own sign-in, and a removed one is
    // let go by the game's frames too.
    public class FlockLocalPlayersPlayModeTests
    {
        private FlockTestClient _h;

        [SetUp]
        public void SetUp()
        {
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            _h?.Dispose();
            _h = null;
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
        }

        private static FlockHttpResponse SignedInAs(string playerId)
            => FlockFakeTransport.Ok("{\"player_id\":\"" + playerId + "\",\"access_token\":\"" + FlockTestClient.MakeJwt(playerId, 3600)
                + "\",\"refresh_token\":\"refresh-" + playerId + "\"}");

        [UnityTest]
        public IEnumerator ALocalPlayersQueuedWritesGoOutOnTheirOwnSignIn()
        {
            FlockFakeTransport transport = new FlockFakeTransport();
            transport.On(FlockEndpoints.PlayerLoginDevice, SignedInAs("player-one"));
            transport.On(FlockEndpoints.CommandUpdatePlayerData, FlockFakeTransport.Ok("{\"id\":\"pd-1\"}"));
            _h = FlockTestClient.Create(transport);
            FlockLocalPlayer player = _h.Client.AddLocalPlayer();
            bool reachable = false;
            player.Client.ReachabilityProbe = () => reachable;
            _h.Run(() => player.Authentication.LoginWithDeviceAsync("device-one"));
            _h.Run(() => player.Commands.UpdatePlayerDataAsync("pd-1", new List<DataField> { new DataField { FieldName = "Score", Value = 1 } }));
            Assert.AreEqual(0, transport.CountTo(FlockEndpoints.CommandUpdatePlayerData), "Queued while offline.");

            reachable = true;
            _h.Run(() => player.Authentication.LoginWithDeviceAsync("device-one"));
            yield return FlockTestWait.Until(() => transport.CountTo(FlockEndpoints.CommandUpdatePlayerData) == 1,
                "The local player's own sign-in sent their queued write.");
        }

        [UnityTest]
        public IEnumerator ARemovedLocalPlayerIsLetGoByTheGamesFrames_AndOneStillAddedIsKept()
        {
            _h = FlockTestClient.Create(new FlockFakeTransport());
            WeakReference removed = AddThenMaybeRemove(_h, remove: true);
            WeakReference kept = AddThenMaybeRemove(_h, remove: false);
            yield return null;
            // A few rounds: the collector can keep an object one round longer through a stale stack slot.
            for (int round = 0; round < 5 && removed.IsAlive; round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsFalse(removed.IsAlive, "The game's frames no longer hold a removed player's write queue.");
            Assert.IsTrue(kept.IsAlive, "Control: a player still added is held.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AddThenMaybeRemove(FlockTestClient h, bool remove)
        {
            FlockLocalPlayer player = h.Client.AddLocalPlayer();
            if (remove)
                h.Client.RemoveLocalPlayer(player);
            return new WeakReference(player);
        }
    }
}
