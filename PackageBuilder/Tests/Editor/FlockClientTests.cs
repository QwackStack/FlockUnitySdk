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
        public void LoginAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("LoginAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<LoginResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(3, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(string), parameters[2].ParameterType);
        }

        [Test]
        public void RegisterAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("RegisterAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<RegisterResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(4, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(string), parameters[2].ParameterType);
            Assert.AreEqual(typeof(string), parameters[3].ParameterType);
        }

        [Test]
        public void AuthenticateWithSteamAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("AuthenticateWithSteamAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<AuthResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void AuthenticateWithGameCenterAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("AuthenticateWithGameCenterAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<AuthResponse>), method.ReturnType);
            Assert.AreEqual(0, method.GetParameters().Length);
        }

        [Test]
        public void AuthenticateWithPlayStoreAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("AuthenticateWithPlayStoreAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<AuthResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void AuthenticateWithDeviceIdAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockClient).GetMethod("AuthenticateWithDeviceIdAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<AuthResponse>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }
    }
}
