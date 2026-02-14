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
        public void PlayerLoginRequest_ShouldSetProperties()
        {
            var request = new PlayerLoginRequest
            {
                LoginType = "email",
                Email = "test@example.com",
                Password = "password123"
            };

            Assert.AreEqual("email", request.LoginType);
            Assert.AreEqual("test@example.com", request.Email);
            Assert.AreEqual("password123", request.Password);
        }

        [Test]
        public void PlayerEmailRegistrationRequest_ShouldSetProperties()
        {
            var request = new PlayerEmailRegistrationRequest
            {
                Email = "test@example.com",
                Password = "password123",
                Name = "TestPlayer"
            };

            Assert.AreEqual("test@example.com", request.Email);
            Assert.AreEqual("password123", request.Password);
            Assert.AreEqual("TestPlayer", request.Name);
        }

        [Test]
        public void PlayerDeviceLoginRequest_ShouldSetProperties()
        {
            var request = new PlayerDeviceLoginRequest
            {
                DeviceType = "android",
                DeviceId = "device-123"
            };

            Assert.AreEqual("android", request.DeviceType);
            Assert.AreEqual("device-123", request.DeviceId);
        }

        [Test]
        public void PlayerDeviceRegistrationRequest_ShouldSetProperties()
        {
            var request = new PlayerDeviceRegistrationRequest
            {
                DeviceType = "ios",
                DeviceId = "device-456",
                Name = "MyDevice"
            };

            Assert.AreEqual("ios", request.DeviceType);
            Assert.AreEqual("device-456", request.DeviceId);
            Assert.AreEqual("MyDevice", request.Name);
        }

        [Test]
        public void PlayerLoginResponse_ShouldSetProperties()
        {
            var response = new PlayerLoginResponse
            {
                AccessToken = "access-token",
                RefreshToken = "refresh-token"
            };

            Assert.AreEqual("access-token", response.AccessToken);
            Assert.AreEqual("refresh-token", response.RefreshToken);
        }

        [Test]
        public void GameSchema_ShouldSetProperties()
        {
            var game = new GameSchema
            {
                Id = "game-1",
                Name = "TestGame",
                Stage = "production",
                StudioId = "studio-1"
            };

            Assert.AreEqual("game-1", game.Id);
            Assert.AreEqual("TestGame", game.Name);
            Assert.AreEqual("production", game.Stage);
            Assert.AreEqual("studio-1", game.StudioId);
        }

        [Test]
        public void GameVersionSchema_ShouldSetProperties()
        {
            var version = new GameVersionSchema
            {
                Id = "ver-1",
                Name = "1.0.0",
                ReleaseType = "release",
                Env = "production"
            };

            Assert.AreEqual("ver-1", version.Id);
            Assert.AreEqual("1.0.0", version.Name);
            Assert.AreEqual("release", version.ReleaseType);
            Assert.AreEqual("production", version.Env);
        }

        [Test]
        public void GameConfigSchema_ShouldSetProperties()
        {
            var config = new GameConfigSchema
            {
                Id = "config-1",
                Name = "MainConfig",
                GameId = "game-1",
                GameVersionId = "ver-1",
                Schema = new Dictionary<string, object> { { "maxPlayers", "integer" } },
                Data = new Dictionary<string, object> { { "maxPlayers", 4 } },
                Tag = "default"
            };

            Assert.AreEqual("config-1", config.Id);
            Assert.AreEqual("MainConfig", config.Name);
            Assert.AreEqual("game-1", config.GameId);
            Assert.AreEqual("ver-1", config.GameVersionId);
            Assert.IsNotNull(config.Schema);
            Assert.IsNotNull(config.Data);
            Assert.AreEqual("default", config.Tag);
        }

        [Test]
        public void GamePatchSchema_ShouldSetProperties()
        {
            var patch = new GamePatchSchema
            {
                Id = "patch-1",
                Name = "FirstPatch",
                GameConfigId = "config-1",
                Data = new Dictionary<string, object> { { "maxPlayers", 8 } }
            };

            Assert.AreEqual("patch-1", patch.Id);
            Assert.AreEqual("FirstPatch", patch.Name);
            Assert.AreEqual("config-1", patch.GameConfigId);
            Assert.IsNotNull(patch.Data);
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
                Error = null,
                Response = new ResponseSchema { Message = "Operation successful", Code = "200" },
                Result = "test-result"
            };

            Assert.IsNull(response.Error);
            Assert.AreEqual("Operation successful", response.Response.Message);
            Assert.AreEqual("test-result", response.Result);
        }

        [Test]
        public void ErrorSchema_ShouldSetProperties()
        {
            var error = new ErrorSchema
            {
                Code = "AUTH_FAILED"
            };

            Assert.AreEqual("AUTH_FAILED", error.Code);
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
