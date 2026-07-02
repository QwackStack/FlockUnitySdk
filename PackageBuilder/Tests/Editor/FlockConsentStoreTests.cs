using UnityEngine;
using NUnit.Framework;
using Flock.Analytics;

namespace Flock.Tests.Editor
{
    // Locks the PlayerPrefs round-trip: no decision recorded -> null, and a saved
    // decision survives a fresh store instance (simulating a relaunch).
    public class FlockConsentStoreTests
    {
        private const string KeyGranted = "flock_analytics_consent";
        private const string KeySet = "flock_analytics_consent_set";

        [SetUp]
        public void SetUp()
        {
            PlayerPrefs.DeleteKey(KeyGranted);
            PlayerPrefs.DeleteKey(KeySet);
        }

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey(KeyGranted);
            PlayerPrefs.DeleteKey(KeySet);
        }

        [Test]
        public void Load_NoDecisionRecorded_ReturnsNull()
        {
            FlockConsentStore store = new FlockConsentStore();
            Assert.IsNull(store.Load());
        }

        [Test]
        public void Save_Granted_ThenLoad_ReturnsTrue()
        {
            FlockConsentStore store = new FlockConsentStore();
            store.Save(true);

            Assert.AreEqual(true, new FlockConsentStore().Load());
        }

        [Test]
        public void Save_Revoked_ThenLoad_ReturnsFalse()
        {
            FlockConsentStore store = new FlockConsentStore();
            store.Save(false);

            Assert.AreEqual(false, new FlockConsentStore().Load());
        }

        [Test]
        public void Clear_RemovesDecision_LoadReturnsNull()
        {
            FlockConsentStore store = new FlockConsentStore();
            store.Save(true);
            store.Clear();

            Assert.IsNull(new FlockConsentStore().Load());
        }
    }
}
