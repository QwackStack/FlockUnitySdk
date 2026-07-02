using UnityEngine;

namespace Flock.Analytics
{
    // Persists the player's analytics consent decision across launches — same PlayerPrefs
    // pattern FlockSession already uses for flock_session_number.
    internal class FlockConsentStore
    {
        private const string PrefKeyConsentGranted = "flock_analytics_consent";
        private const string PrefKeyConsentSet = "flock_analytics_consent_set";

        // Null = no decision has ever been recorded (first launch, or SetConsent never called).
        internal bool? Load()
        {
            if (PlayerPrefs.GetInt(PrefKeyConsentSet, 0) == 0)
                return null;

            return PlayerPrefs.GetInt(PrefKeyConsentGranted, 0) == 1;
        }

        internal void Save(bool granted)
        {
            PlayerPrefs.SetInt(PrefKeyConsentSet, 1);
            PlayerPrefs.SetInt(PrefKeyConsentGranted, granted ? 1 : 0);
            PlayerPrefs.Save();
        }

        internal void Clear()
        {
            PlayerPrefs.DeleteKey(PrefKeyConsentGranted);
            PlayerPrefs.DeleteKey(PrefKeyConsentSet);
            PlayerPrefs.Save();
        }
    }
}
