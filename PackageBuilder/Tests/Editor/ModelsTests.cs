using NUnit.Framework;
using Flock.Models;
using System;
using System.Collections.Generic;

namespace FlockSDK.Tests
{
    [TestFixture]
    public class ModelsTests
    {
        [Test]
        public void LoginRequest_ShouldSetProperties()
        {
            var request = new LoginRequest
            {
                Email = "test@example.com",
                Password = "password123",
                Otp = "123456"
            };

            Assert.AreEqual("test@example.com", request.Email);
            Assert.AreEqual("password123", request.Password);
            Assert.AreEqual("123456", request.Otp);
        }

        [Test]
        public void LoginResponse_ShouldSetProperties()
        {
            var response = new LoginResponse
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token"
            };

            Assert.AreEqual("access-token", response.AccessToken);
            Assert.AreEqual("refresh-token", response.RefreshToken);
        }

        [Test]
        public void Achievement_ShouldSetProperties()
        {
            var achievement = new Achievement
            {
                Id = "ach-1",
                Name = "First Win",
                Description = "Win your first game",
                Points = 100,
                IconUrl = "https://example.com/icon.png",
                IsUnlocked = true,
                UnlockedAt = DateTime.UtcNow,
                Progress = 1.0f,
                Metadata = new Dictionary<string, object> { { "rarity", "common" } }
            };

            Assert.AreEqual("ach-1", achievement.Id);
            Assert.AreEqual("First Win", achievement.Name);
            Assert.AreEqual("Win your first game", achievement.Description);
            Assert.AreEqual(100, achievement.Points);
            Assert.AreEqual("https://example.com/icon.png", achievement.IconUrl);
            Assert.IsTrue(achievement.IsUnlocked);
            Assert.IsNotNull(achievement.UnlockedAt);
            Assert.AreEqual(1.0f, achievement.Progress);
            Assert.IsNotNull(achievement.Metadata);
        }

        [Test]
        public void LeaderboardEntry_ShouldSetProperties()
        {
            var entry = new LeaderboardEntry
            {
                Id = "entry-1",
                PlayerId = "player-1",
                PlayerName = "TestPlayer",
                Score = 1000,
                Rank = 1,
                Metadata = new Dictionary<string, object> { { "level", 5 } },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            Assert.AreEqual("entry-1", entry.Id);
            Assert.AreEqual("player-1", entry.PlayerId);
            Assert.AreEqual("TestPlayer", entry.PlayerName);
            Assert.AreEqual(1000, entry.Score);
            Assert.AreEqual(1, entry.Rank);
            Assert.IsNotNull(entry.Metadata);
            Assert.IsNotNull(entry.CreatedAt);
            Assert.IsNotNull(entry.UpdatedAt);
        }

        [Test]
        public void PlayerData_ShouldSetProperties()
        {
            var playerData = new PlayerData
            {
                Id = "pd-1",
                PlayerId = "player-1",
                Username = "TestUser",
                Level = 10,
                Experience = 5000,
                CustomData = new Dictionary<string, object> { { "coins", 100 } },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            Assert.AreEqual("pd-1", playerData.Id);
            Assert.AreEqual("player-1", playerData.PlayerId);
            Assert.AreEqual("TestUser", playerData.Username);
            Assert.AreEqual(10, playerData.Level);
            Assert.AreEqual(5000, playerData.Experience);
            Assert.IsNotNull(playerData.CustomData);
            Assert.IsNotNull(playerData.CreatedAt);
            Assert.IsNotNull(playerData.UpdatedAt);
        }

        [Test]
        public void GameConfig_ShouldSetProperties()
        {
            var config = new GameConfig
            {
                Id = "config-1",
                Name = "MainConfig",
                Data = new Dictionary<string, object> { { "maxPlayers", 4 } },
                CreatedAt = "2025-01-01",
                UpdatedAt = "2025-01-02",
                Version = "1.0.0"
            };

            Assert.AreEqual("config-1", config.Id);
            Assert.AreEqual("MainConfig", config.Name);
            Assert.IsNotNull(config.Data);
            Assert.AreEqual("2025-01-01", config.CreatedAt);
            Assert.AreEqual("2025-01-02", config.UpdatedAt);
            Assert.AreEqual("1.0.0", config.Version);
        }

        [Test]
        public void PaginatedResponse_ShouldSetProperties()
        {
            var response = new PaginatedResponse<PlayerData>
            {
                Items = new PlayerData[] { new PlayerData { Id = "1" } },
                Total = 100,
                Page = 1,
                PageSize = 10
            };

            Assert.IsNotNull(response.Items);
            Assert.AreEqual(1, response.Items.Length);
            Assert.AreEqual(100, response.Total);
            Assert.AreEqual(1, response.Page);
            Assert.AreEqual(10, response.PageSize);
        }

        [Test]
        public void GenericResponse_ShouldSetProperties()
        {
            var response = new GenericResponse<string>
            {
                Success = true,
                Message = "Operation successful",
                Result = "test-result"
            };

            Assert.IsTrue(response.Success);
            Assert.AreEqual("Operation successful", response.Message);
            Assert.AreEqual("test-result", response.Result);
        }

        [Test]
        public void SubmitScoreRequest_ShouldSetProperties()
        {
            var request = new SubmitScoreRequest
            {
                PlayerId = "player-1",
                LeaderboardId = "lb-1",
                Score = 1000,
                Metadata = new Dictionary<string, object> { { "time", 60 } }
            };

            Assert.AreEqual("player-1", request.PlayerId);
            Assert.AreEqual("lb-1", request.LeaderboardId);
            Assert.AreEqual(1000, request.Score);
            Assert.IsNotNull(request.Metadata);
        }

        [Test]
        public void UnlockAchievementRequest_ShouldSetProperties()
        {
            var request = new UnlockAchievementRequest
            {
                PlayerId = "player-1",
                AchievementId = "ach-1"
            };

            Assert.AreEqual("player-1", request.PlayerId);
            Assert.AreEqual("ach-1", request.AchievementId);
        }

        [Test]
        public void UpdateAchievementProgressRequest_ShouldSetProperties()
        {
            var request = new UpdateAchievementProgressRequest
            {
                PlayerId = "player-1",
                AchievementId = "ach-1",
                Progress = 0.5f
            };

            Assert.AreEqual("player-1", request.PlayerId);
            Assert.AreEqual("ach-1", request.AchievementId);
            Assert.AreEqual(0.5f, request.Progress);
        }
    }
}
