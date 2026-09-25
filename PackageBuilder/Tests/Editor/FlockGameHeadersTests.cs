using System;
using System.Collections.Generic;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // The public reads a separate Qwacks service uses to call its own API as this game.
    public class FlockGameHeadersTests
    {
        [Test]
        public void GameHeadersCarryTheKeyAndVersionAndNeverTheSignIn()
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()))
            {
                flock.LoginAs("player-1");
                Dictionary<string, string> headers = flock.Client.GetGameHeaders();

                Assert.AreEqual("test-key", headers["X-Flock-API-Key"]);
                Assert.AreEqual("test-gvid", headers["X-Game-Version-ID"]);
                Assert.IsFalse(headers.ContainsKey("Authorization"), "A service that identifies the game never sees the player's sign-in");
                Assert.AreEqual(2, headers.Count);
            }
        }

        [Test]
        public void GameHeadersAreACopy()
        {
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport()))
            {
                flock.Client.GetGameHeaders()["X-Flock-API-Key"] = "changed";
                Assert.AreEqual("test-key", flock.Client.GetGameHeaders()["X-Flock-API-Key"]);
            }
        }

        [Test]
        public void TheRetryPolicyIsACopyOfTheOneTheClientWasInitializedWith()
        {
            RetryPolicy given = new RetryPolicy
            {
                MaxRetries = 7,
                InitialDelay = TimeSpan.FromMilliseconds(250),
                MaxDelay = TimeSpan.FromSeconds(9),
                BackoffMultiplier = 3.5,
                UseJitter = false
            };
            using (FlockTestClient flock = FlockTestClient.Create(new FlockFakeTransport(), config => config.RetryPolicy = given))
            {
                RetryPolicy read = flock.Client.RetryPolicy;
                Assert.AreNotSame(given, read);
                Assert.AreEqual(7, read.MaxRetries);
                Assert.AreEqual(TimeSpan.FromMilliseconds(250), read.InitialDelay);
                Assert.AreEqual(TimeSpan.FromSeconds(9), read.MaxDelay);
                Assert.AreEqual(3.5, read.BackoffMultiplier);
                Assert.IsFalse(read.UseJitter);

                read.MaxRetries = 0;
                Assert.AreEqual(7, flock.Client.RetryPolicy.MaxRetries, "Changing the copy changes nothing in the client");
            }
        }
    }
}
