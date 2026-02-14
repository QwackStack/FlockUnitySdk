using NUnit.Framework;
using Flock.Config;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class FlockInitConfigTests
    {
        [Test]
        public void Constructor_ShouldSetAllProperties()
        {
            var config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key",
                "test-game-id",
                "test-game-version-id",
                FlockEnvironment.Development,
                true
            );

            Assert.AreEqual("https://api.test.com", config.ApiUrl);
            Assert.AreEqual("test-api-key", config.ApiKey);
            Assert.AreEqual("test-game-id", config.GameId);
            Assert.AreEqual("test-game-version-id", config.GameVersionId);
            Assert.AreEqual(FlockEnvironment.Development, config.Environment);
            Assert.IsTrue(config.EnableDebugLogs);
        }

        [Test]
        public void Constructor_WithDefaultEnvironment_ShouldSetProduction()
        {
            var config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key",
                "test-game-id",
                "test-game-version-id"
            );

            Assert.AreEqual("https://api.test.com", config.ApiUrl);
            Assert.AreEqual("test-api-key", config.ApiKey);
            Assert.AreEqual("test-game-id", config.GameId);
            Assert.AreEqual("test-game-version-id", config.GameVersionId);
            Assert.AreEqual(FlockEnvironment.Production, config.Environment);
            Assert.IsFalse(config.EnableDebugLogs);
        }

        [Test]
        public void GetBaseHeaders_ShouldContainApiKeyAndGameVersionId()
        {
            var config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key",
                "test-game-id",
                "test-game-version-id"
            );

            var headers = config.GetBaseHeaders();
            Assert.IsTrue(headers.ContainsKey("X-Flock-API-Key"));
            Assert.AreEqual("test-api-key", headers["X-Flock-API-Key"]);
            Assert.IsTrue(headers.ContainsKey("X-Game-Version-ID"));
            Assert.AreEqual("test-game-version-id", headers["X-Game-Version-ID"]);
        }

        [Test]
        public void GetAuthenticatedHeaders_ShouldContainBearerToken()
        {
            var config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key",
                "test-game-id",
                "test-game-version-id"
            );

            var headers = config.GetAuthenticatedHeaders("my-token");
            Assert.IsTrue(headers.ContainsKey("Authorization"));
            Assert.AreEqual("Bearer my-token", headers["Authorization"]);
            Assert.IsTrue(headers.ContainsKey("X-Flock-API-Key"));
            Assert.IsTrue(headers.ContainsKey("X-Game-Version-ID"));
        }

        [Test]
        public void Environment_Production_ShouldBeAvailable()
        {
            var env = FlockEnvironment.Production;
            Assert.IsNotNull(env);
        }

        [Test]
        public void Environment_Preprod_ShouldBeAvailable()
        {
            var env = FlockEnvironment.Preprod;
            Assert.IsNotNull(env);
        }

        [Test]
        public void Environment_Development_ShouldBeAvailable()
        {
            var env = FlockEnvironment.Development;
            Assert.IsNotNull(env);
        }
    }
}
