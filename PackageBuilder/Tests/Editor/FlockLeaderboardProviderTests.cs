using NUnit.Framework;
using Flock.Leaderboard;
using Flock.Models;
using Flock;
using Flock.Config;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class FlockLeaderboardProviderTests
    {
        private FlockClient _client;
        private FlockLeaderboardProvider _leaderboardProvider;

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
            _leaderboardProvider = new FlockLeaderboardProvider(_client);
        }

        [Test]
        public void Constructor_ShouldInitializeWithClient()
        {
            Assert.IsNotNull(_leaderboardProvider);
        }

        [Test]
        public void GetAllLeaderboardsAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("GetAllLeaderboardsAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<List<LeaderboardInfo>>), method.ReturnType);
        }

        [Test]
        public void GetLeaderboardByIdAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("GetLeaderboardByIdAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<LeaderboardInfo>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        }

        [Test]
        public void GetLeaderboardEntriesAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("GetLeaderboardEntriesAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<PaginatedResponse<LeaderboardEntry>>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(3, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(int), parameters[1].ParameterType);
            Assert.AreEqual(typeof(int), parameters[2].ParameterType);
        }

        [Test]
        public void GetTopEntriesAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("GetTopEntriesAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<List<LeaderboardEntry>>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(int), parameters[1].ParameterType);
        }

        [Test]
        public void GetPlayerEntryAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("GetPlayerEntryAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<LeaderboardEntry>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
        }

        [Test]
        public void SubmitScoreAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("SubmitScoreAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<LeaderboardEntry>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(4, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(long), parameters[2].ParameterType);
            Assert.AreEqual(typeof(Dictionary<string, object>), parameters[3].ParameterType);
        }

        [Test]
        public void GetEntriesAroundPlayerAsync_ShouldHaveCorrectSignature()
        {
            var method = typeof(FlockLeaderboardProvider).GetMethod("GetEntriesAroundPlayerAsync");

            Assert.IsNotNull(method);
            Assert.AreEqual(typeof(Task<List<LeaderboardEntry>>), method.ReturnType);

            var parameters = method.GetParameters();
            Assert.AreEqual(3, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(int), parameters[2].ParameterType);
        }
    }
}
