using System.Threading.Tasks;
using Flock.Models;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // A player's features are kept in memory for the run: one fetch per player, until ClearCache or a new client. Never on disk.
    public class FlockPlayerFeaturesTests
    {
        private const string Features = "{\"result\":{\"id\":\"cfg-f\",\"name\":\"Features\",\"game_id\":\"g\",\"game_version_id\":\"ver-1\"," +
                                        "\"tag\":\"feature_flags\",\"data\":[{\"type\":\"bool\",\"field_name\":\"new_shop\",\"value\":true}]}}";

        [Test]
        public void PlayerFeaturesAreKeptPerPlayerForTheRun()
        {
            FlockFakeTransport transport = new FlockFakeTransport()
                .On("game_config/player/player-a/features", FlockFakeTransport.Ok(Features))
                .On("game_config/player/player-b/features", FlockFakeTransport.Ok(Features));
            using (FlockTestClient sdk = FlockTestClient.Create(transport))
            {
                sdk.LoginAs("player-a");
                for (int read = 0; read < 3; read++)
                {
                    GameConfigSchema features = sdk.Run(() => sdk.Client.Config.GetPlayerFeaturesAsync("player-a"));
                    Assert.AreEqual("cfg-f", features.Id);
                }
                Assert.AreEqual(1, transport.CountTo("player/player-a/features"), "Three reads of one player, one fetch");

                sdk.Run(() => sdk.Client.Config.GetPlayerFeaturesAsync("player-b"));
                Assert.AreEqual(1, transport.CountTo("player/player-b/features"), "Another player fetches their own");
                Assert.AreEqual(1, transport.CountTo("player/player-a/features"), "without touching the first's");

                sdk.Client.Config.ClearCache();
                sdk.Run(() => sdk.Client.Config.GetPlayerFeaturesAsync("player-a"));
                Assert.AreEqual(2, transport.CountTo("player/player-a/features"), "ClearCache forgets them, so the next read fetches");
            }
        }
    }
}
