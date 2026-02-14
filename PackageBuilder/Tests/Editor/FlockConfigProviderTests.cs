using NUnit.Framework;
using Flock;
using Flock.Config;
using Flock.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class FlockConfigProviderTests
    {
        private FlockConfigProvider _configProvider;
        private FlockClient _mockClient;

        [SetUp]
        public void Setup()
        {
            var config = new FlockInitConfig(
                apiUrl: "https://api.test.com",
                apiKey: "test-api-key",
                gameId: "test-game-id",
                gameVersionId: "test-game-version-id",
                environment: FlockEnvironment.Production
            );
            _mockClient = new FlockClient(config);
            _configProvider = new FlockConfigProvider(_mockClient);
        }

        [Test]
        public void Constructor_ShouldInitializeWithFlockClient()
        {
            Assert.IsNotNull(_configProvider);
        }

        [Test]
        public void Constructor_WithNullClient_ShouldThrowArgumentNullException()
        {
            Assert.Throws<System.ArgumentNullException>(() => new FlockConfigProvider(null));
        }

        [Test]
        public void GetAllConfigsAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockConfigProvider).GetMethod("GetAllConfigsAsync");

            Assert.IsNotNull(method);
        }

        [Test]
        public void GetConfigByIdAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockConfigProvider).GetMethod("GetConfigByIdAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<GameConfigSchema>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.GreaterOrEqual(parameters.Length, 1);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void GetConfigsByVersionAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockConfigProvider).GetMethod("GetConfigsByVersionAsync");

            Assert.IsNotNull(method);
        }

        [Test]
        public void GetConfigPatchesAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockConfigProvider).GetMethod("GetConfigPatchesAsync");

            Assert.IsNotNull(method);

            var parameters = method.GetParameters();
            Assert.GreaterOrEqual(parameters.Length, 1);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }
    }
}
