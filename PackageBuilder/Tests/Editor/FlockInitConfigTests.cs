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
                FlockEnvironment.Development,
                true
            );

            Assert.AreEqual("https://api.test.com", config.ApiUrl);
            Assert.AreEqual("test-api-key", config.ApiKey);
            Assert.AreEqual(FlockEnvironment.Development, config.Environment);
            Assert.IsTrue(config.EnableDebugLogs);
        }

        [Test]
        public void Constructor_WithDefaultEnvironment_ShouldSetProduction()
        {
            var config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key"
            );

            Assert.AreEqual("https://api.test.com", config.ApiUrl);
            Assert.AreEqual("test-api-key", config.ApiKey);
            Assert.AreEqual(FlockEnvironment.Production, config.Environment);
            Assert.IsFalse(config.EnableDebugLogs);
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
