using System.Collections;
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

            // Quitting, before any object is destroyed, is when the launch's session is ended. Removed first so a second Play
            // with domain reload off does not add it twice.
            Application.quitting -= ProtokitePlaytest.HandleGameQuitting;
            Application.quitting += ProtokitePlaytest.HandleGameQuitting;
        }

        private void Update() => ProtokitePlaytest.Refresh();

        // A frame is captured after it is drawn, so the video work runs at the end of every frame.
        private IEnumerator Start()
        {
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                ProtokitePlaytest.UpdateVideo(Time.unscaledDeltaTime);
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                ProtokitePlaytest.HandleGameLeftOrCameBack();
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused)
                ProtokitePlaytest.HandleGameLeftOrCameBack();
        }

        private void OnDestroy()
        {
            if (_running != this)
                return;
            _running = null;
            ProtokitePlaytest.Stop();
        }
    }
}
