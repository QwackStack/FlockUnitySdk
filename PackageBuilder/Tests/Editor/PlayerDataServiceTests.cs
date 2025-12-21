using NUnit.Framework;
using Flock.Services;
using Flock.Models;
using Flock;
using Flock.Config;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class PlayerDataServiceTests
    {
        private FlockClient _client;
        private PlayerDataService _playerDataService;

        [SetUp]
        public void Setup()
        {
            var config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key",
                FlockEnvironment.Development,
                true
            );
            _client = new FlockClient(config);
            _playerDataService = new PlayerDataService( _client);
        }

        [Test]
        public void Constructor_ShouldInitializeWithParameters()
        {
            Assert.IsNotNull(_playerDataService);
        }

        [Test]
        public void GetPlayerDataAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(PlayerDataService).GetMethod("GetPlayerDataAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerData>), method.ReturnType);
            Assert.AreEqual(0, method.GetParameters().Length);
        }

        [Test]
        public void UpdatePlayerDataAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(PlayerDataService).GetMethod("UpdatePlayerDataAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerData>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(PlayerData), parameters[0].ParameterType);
        }

        [Test]
        public void CreateAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(PlayerDataService).GetMethod("CreateAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerData>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(Dictionary<string, object>), parameters[1].ParameterType);
        }

        [Test]
        public void GetByIdAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(PlayerDataService).GetMethod("GetByIdAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerData>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void GetAllAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(PlayerDataService).GetMethod("GetAllAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PaginatedResponse<PlayerData>>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(3, parameters.Length);
            Assert.AreEqual(typeof(int), parameters[0].ParameterType);
            Assert.AreEqual(typeof(int), parameters[1].ParameterType);
            Assert.AreEqual(typeof(string), parameters[2].ParameterType);
        }

        [Test]
        public void UpdateAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(PlayerDataService).GetMethod("UpdateAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerData>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(Dictionary<string, object>), parameters[1].ParameterType);
        }
    }
}
