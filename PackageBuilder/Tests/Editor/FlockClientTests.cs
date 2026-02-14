using NUnit.Framework;
using Flock;
using Flock.Config;
using Flock.Models;
using Flock.Achievements;
using Flock.Leaderboard;
using Flock.Services;
using System;
using System.Threading.Tasks;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class FlockClientTests
    {
        private FlockInitConfig _config;
        private FlockClient _client;

        [SetUp]
        public void Setup()
        {
            _config = new FlockInitConfig(
                "https://api.test.com",
                "test-api-key",
                "test-game-id",
                "test-game-version-id",
                FlockEnvironment.Development,
                true
            );
            _client = new FlockClient(_config);
        }

        [Test]
        public void Constructor_WithValidConfig_ShouldInitialize()
        {
            Assert.IsNotNull(_client);
        }

        [Test]
        public void Constructor_WithNullConfig_ShouldThrowArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new FlockClient(null));
        }

        [Test]
        public void Achievements_ShouldReturnFlockAchievementProvider()
        {
            Assert.IsNotNull(_client.Achievements);
            Assert.IsInstanceOf<FlockAchievementProvider>(_client.Achievements);
        }

        [Test]
        public void Leaderboards_ShouldReturnFlockLeaderboardProvider()
        {
            Assert.IsNotNull(_client.Leaderboards);
            Assert.IsInstanceOf<FlockLeaderboardProvider>(_client.Leaderboards);
        }

        [Test]
        public void Config_ShouldReturnFlockConfigProvider()
        {
            Assert.IsNotNull(_client.Config);
            Assert.IsInstanceOf<FlockConfigProvider>(_client.Config);
        }

        [Test]
        public void PlayerData_ShouldReturnPlayerDataService()
        {
            Assert.IsNotNull(_client.PlayerData);
            Assert.IsInstanceOf<PlayerDataService>(_client.PlayerData);
        }

        [Test]
        public void Patches_ShouldReturnFlockGamePatchProvider()
        {
            Assert.IsNotNull(_client.Patches);
            Assert.IsInstanceOf<FlockGamePatchProvider>(_client.Patches);
        }

        [Test]
        public void Game_ShouldReturnFlockGameService()
        {
            Assert.IsNotNull(_client.Game);
            Assert.IsInstanceOf<FlockGameService>(_client.Game);
        }

        [Test]
        public void GetAccessToken_InitiallyReturnsNull()
        {
            Assert.IsNull(_client.GetAccessToken());
        }

        [Test]
        public void ClearTokens_ShouldClearAccessToken()
        {
            _client.ClearTokens();
            Assert.IsNull(_client.GetAccessToken());
        }

        [Test]
        public void GetApiUrl_ShouldReturnConfiguredApiUrl()
        {
            var apiUrl = _client.GetApiUrl();
            Assert.AreEqual("https://api.test.com", apiUrl);
        }

        [Test]
        public void GameId_ShouldReturnConfiguredGameId()
        {
            Assert.AreEqual("test-game-id", _client.GameId);
        }

        [Test]
        public void GameVersionId_ShouldReturnConfiguredGameVersionId()
        {
            Assert.AreEqual("test-game-version-id", _client.GameVersionId);
        }

        [Test]
        public void LoginWithEmailAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("LoginWithEmailAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerLoginResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.GreaterOrEqual(parameters.Length, 2);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        }

        [Test]
        public void RegisterWithEmailAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("RegisterWithEmailAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerLoginResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.GreaterOrEqual(parameters.Length, 2);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        }

        [Test]
        public void LoginWithDeviceAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("LoginWithDeviceAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerLoginResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.GreaterOrEqual(parameters.Length, 2);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        }

        [Test]
        public void RegisterWithDeviceAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("RegisterWithDeviceAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PlayerLoginResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.GreaterOrEqual(parameters.Length, 2);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        }
    }
}
