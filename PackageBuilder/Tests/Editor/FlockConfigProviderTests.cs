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
        public void GetAllConfigAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockConfigProvider).GetMethod("GetAllConfigAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<List<GameConfig>>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(CancellationToken), parameters[0].ParameterType);
        }

        [Test]
        public void GetConfigByIdAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockConfigProvider).GetMethod("GetConfigByIdAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<GameConfig>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType);
        }
    }
}
