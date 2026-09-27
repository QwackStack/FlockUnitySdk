using System.Collections;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Keeps the playtest following the Flock SDK, once a frame, for as long as the game runs.</summary>
    [AddComponentMenu("")]
    internal sealed class ProtokitePlaytestDriver : MonoBehaviour
    {
        private static ProtokitePlaytestDriver _running;

        /// <summary>Starts the driver with every launch. With playtesting off it records nothing and starts no session, but still uploads what earlier launches kept.</summary>
        // Whatever the setting, so a build with playtesting off never strands the recordings a playtest build of the game left.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void StartWithTheGame()
        {
            if (_running != null)
                return;

            GameObject driver = new GameObject("Protokite Playtest") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(driver);
            _running = driver.AddComponent<ProtokitePlaytestDriver>();

            // Quitting, before any object is destroyed, is when the launch's session is ended. Removed first so a second Play
            // with domain reload off does not add it twice.
            Application.quitting -= ProtokitePlaytest.HandleGameQuitting;
            Application.quitting += ProtokitePlaytest.HandleGameQuitting;

            // With the launch, off the main thread: what earlier launches left is finished, kept or deleted.
            ProtokitePlaytest.StartFinishingEarlierRecordings();
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
