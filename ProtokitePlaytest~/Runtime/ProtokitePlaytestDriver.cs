using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Keeps the playtest following the Flock SDK, once a frame, for as long as the game runs with playtesting on.</summary>
    [AddComponentMenu("")]
    internal sealed class ProtokitePlaytestDriver : MonoBehaviour
    {
        private static ProtokitePlaytestDriver _running;

        /// <summary>Starts the driver when the project has playtesting on. With it off, nothing runs and nothing is sent.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void StartWhenPlaytestingIsOn()
        {
            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            if (_running != null || settings == null || !settings.PlaytestingEnabled)
                return;

            GameObject driver = new GameObject("Protokite Playtest") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(driver);
            _running = driver.AddComponent<ProtokitePlaytestDriver>();
        }

        private void Update() => ProtokitePlaytest.Refresh();

        private void OnDestroy()
        {
            if (_running != this)
                return;
            _running = null;
            ProtokitePlaytest.Stop();
        }
    }
}
