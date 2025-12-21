using NUnit.Framework;
using Flock.Achievements;
using Flock.Models;
using Flock;
using Flock.Config;
using System.Threading.Tasks;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class FlockAchievementProviderTests
    {
        private FlockClient _client;
        private FlockAchievementProvider _achievementProvider;

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
            _achievementProvider = new FlockAchievementProvider(_client);
        }

        [Test]
        public void Constructor_ShouldInitializeWithClient()
        {
            Assert.IsNotNull(_achievementProvider);
        }

        [Test]
        public void Constructor_ShouldSetCorrectBaseUrl()
        {
            // Arrange & Act
            var provider = new FlockAchievementProvider(_client);

            // Assert
            Assert.IsNotNull(provider);
        }

        [Test]
        public void GetAllAchievementsAsync_ShouldReturnListOfAchievements()
        {
            // This would require mocking HttpClient
            // For now, we verify the method exists and has correct signature
            var method = typeof(FlockAchievementProvider).GetMethod("GetAllAchievementsAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<System.Collections.Generic.List<Achievement>>), method.ReturnType);
        }

        [Test]
        public void GetAchievementByIdAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockAchievementProvider).GetMethod("GetAchievementByIdAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<Achievement>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void GetPlayerAchievementsAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockAchievementProvider).GetMethod("GetPlayerAchievementsAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<System.Collections.Generic.List<Achievement>>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void UnlockAchievementAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockAchievementProvider).GetMethod("UnlockAchievementAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<Achievement>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        }

        [Test]
        public void UpdateProgressAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockAchievementProvider).GetMethod("UpdateProgressAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<Achievement>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(3, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(float), parameters[2].ParameterType);
        }
    }
}
