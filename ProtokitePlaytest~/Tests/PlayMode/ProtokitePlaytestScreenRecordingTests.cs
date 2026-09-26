#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Flock;
using Flock.Http;
using Flock.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// The whole recording, as a game makes it: the real screen, the real encoder and the real file, with nothing standing in.
    /// Needs a window: a batchmode editor draws nothing and never reaches the end of a frame.
    /// </summary>
    public class ProtokitePlaytestScreenRecordingTests
    {
        private const string ConfigRoute = "/game/sdk/playtest-config";
        private const string VideoAnswer =
            "{\"result\":{\"session_started_event\":\"session_started\",\"test_id\":\"screen\",\"flock_game_version_id\":\"test-gvid\",\"features\":{\"video_recording\":true},\"form\":null}}";

        private static readonly Color Background = new Color(0.2f, 0.4f, 0.6f);
        private static readonly Color Band = Color.red;

        private readonly List<Object> _made = new List<Object>();
        private ProtokitePlaytestSettings _settings;
        private string _settingsAsTheyWere;
        private string _folder;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (Application.isBatchMode)
                Assert.Ignore("A batchmode editor draws nothing, so there is no screen to record; run the PlayMode tests in a windowed editor.");
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            yield return null;

            _settings = ProtokitePlaytestSettings.Load();
            if (_settings == null)
                Assert.Ignore("This project has no playtest settings; create them with Protokite > Playtest > Settings.");
            _settingsAsTheyWere = JsonUtility.ToJson(_settings);
            _settings.PlaytestingEnabled = true;
            _settings.ProtokiteApiUrl = "http://protokite.test";
            _settings.VideoFramesPerSecond = 15;
            ProtokitePlaytest.ResetForNewLaunch();
            _folder = Path.Combine(Path.GetTempPath(), "protokite_screen_" + Guid.NewGuid().ToString("N"));
            ProtokitePlaytest.RecordingsFolderForTesting = Path.Combine(_folder, "Recordings");
            ProtokitePlaytest.DeviceIdFilePathForTesting = Path.Combine(_folder, "device_id.txt");
        }

        [TearDown]
        public void TearDown()
        {
            ProtokitePlaytest.ResetForNewLaunch();
            foreach (ProtokitePlaytestDriver driver in Resources.FindObjectsOfTypeAll<ProtokitePlaytestDriver>())
                Object.Destroy(driver.gameObject);
            if (FlockClient.IsInitialized)
                FlockClient.Shutdown();
            foreach (Object made in _made)
                Object.Destroy(made);
            _made.Clear();
            ProtokitePlaytest.RecordingsFolderForTesting = null;
            ProtokitePlaytest.DeviceIdFilePathForTesting = null;
            if (_settings != null && _settingsAsTheyWere != null)
                JsonUtility.FromJsonOverwrite(_settingsAsTheyWere, _settings);
            if (_folder != null && Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        // A still picture: a red band across the top half, over a plain background, so every frame of the video is the same.
        private void BuildTheScene()
        {
            foreach (Camera existing in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                existing.enabled = false;
            GameObject cameraObject = new GameObject("Screen recording camera");
            _made.Add(cameraObject);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            camera.transform.position = new Vector3(0f, 0f, -10f);

            GameObject band = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _made.Add(band);
            band.transform.position = new Vector3(0f, 2.5f, 0f);
            band.transform.localScale = new Vector3(100f, 5f, 1f);
            Shader unlit = Shader.Find(GraphicsSettings.currentRenderPipeline != null ? "Universal Render Pipeline/Unlit" : "Unlit/Color");
            Assert.IsNotNull(unlit, "An unlit shader for this render pipeline");
            Material material = new Material(unlit);
            _made.Add(material);
            material.color = Band;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Band);
            band.GetComponent<Renderer>().sharedMaterial = material;
        }

        [UnityTest]
        public IEnumerator TheRecordingIsTheScreenUprightAndInItsOwnColours()
        {
            BuildTheScene();
            ProtokitePlaytestDriver.StartWhenPlaytestingIsOn();
            Texture2D screen;
            using (FlockTestClient.Create(new FlockFakeTransport().On(ConfigRoute, FlockFakeTransport.Ok(VideoAnswer))))
            {
                for (int frame = 0; frame < 90; frame++)
                    yield return null;
                Assert.IsTrue(ProtokitePlaytest.IsRecordingVideo, "Recording the screen, with nothing standing in");

                yield return new WaitForEndOfFrame();
                screen = ScreenCapture.CaptureScreenshotAsTexture();
                _made.Add(screen);
                for (int frame = 0; frame < 10; frame++)
                    yield return null;
                ProtokitePlaytest.HandleGameQuitting();
            }

            ProtokitePlaytestVideoRecordingSummary summary = ProtokitePlaytest.FinishedVideo;
            Assert.IsNotNull(summary, "The file was finished at quit");
            Assert.IsNotNull(summary.FilePath, summary.Error);
            Assert.Greater(summary.FramesWritten, 10, summary.Error);

            List<byte[]> frames = ReadFrames(File.ReadAllBytes(summary.FilePath));
            Assert.AreEqual(summary.FramesWritten, frames.Count, "Every frame is in the file");
            ProtokitePlaytestVideoSettings video = ProtokitePlaytestVideoSettings.From(_settings);
            Assert.IsTrue(ProtokitePlaytestVideoSettings.FitVideoSize(Screen.width, Screen.height, video.MaxVideoWidth, video.MaxVideoHeight, out int width, out int height));
            byte[] last = null;
            using (ProtokitePlaytestLibVpx.Decoder decoder = new ProtokitePlaytestLibVpx.Decoder(video.Codec))
            {
                foreach (byte[] frame in frames)
                {
                    last = decoder.Decode(frame, width, height, out string error);
                    Assert.IsNotNull(last, error);
                }
            }

            // Looked at by eye while this test was written; kept beside the test's own temporary files.
            string looked = Environment.GetEnvironmentVariable("PROTOKITE_SCREEN_TEST_IMAGES");
            if (!string.IsNullOrEmpty(looked))
            {
                Directory.CreateDirectory(looked);
                File.WriteAllBytes(Path.Combine(looked, "screen.png"), screen.EncodeToPNG());
                Texture2D decoded = new Texture2D(width, height, TextureFormat.RGBA32, false);
                _made.Add(decoded);
                Color32[] pixels = new Color32[width * height];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Vector3 c = MeanOfVideoBox(last, width, height, x, y);
                    pixels[(height - 1 - y) * width + x] = new Color32((byte)c.x, (byte)c.y, (byte)c.z, 255);
                }
                decoded.SetPixels32(pixels);
                File.WriteAllBytes(Path.Combine(looked, "video.png"), decoded.EncodeToPNG());
            }

            string context = $"{(GraphicsSettings.currentRenderPipeline != null ? "URP" : "Built-in")}, {QualitySettings.activeColorSpace}, {SystemInfo.graphicsDeviceType}, " +
                $"screen {Screen.width}x{Screen.height}, video {width}x{height}";
            // Judged against the screen, as a player sees it. The screenshot itself is first checked against what the scene draws,
            // since one taken in the editor once came back black where the band was: an unusable screenshot fails as that, not as
            // a wrong recording. (URP in Gamma draws the background's blue 2 below what was asked for; the screen is the truth.)
            // Unity's texture rows run from the bottom; the video's from the top.
            AssertColour(ColourOnScreen(Band), MeanOfScreen(screen, 0.60f, 0.90f), MeanOfVideo(last, width, height, 0.10f, 0.40f), "The top of the picture, the red band: " + context);
            AssertColour(ColourOnScreen(Background), MeanOfScreen(screen, 0.10f, 0.40f), MeanOfVideo(last, width, height, 0.60f, 0.90f), "The bottom, the background: " + context);
        }

        // What a camera clearing to this colour, or an unlit shader drawing it, puts on the screen: its sRGB bytes.
        private static Vector3 ColourOnScreen(Color colour) => new Vector3(Mathf.Round(colour.r * 255f), Mathf.Round(colour.g * 255f), Mathf.Round(colour.b * 255f));

        private static void AssertColour(Vector3 drawn, Vector3 screenshot, Vector3 video, string where)
        {
            string all = $"{where}. Drawn {drawn}, screenshot {screenshot}, video {video}";
            Assert.IsTrue(Vector3.Distance(drawn, screenshot) <= 3f, "Precondition: the screenshot shows the scene. " + all);
            Assert.AreEqual(screenshot.x, video.x, 1.0, all + ": red");
            Assert.AreEqual(screenshot.y, video.y, 1.0, all + ": green");
            Assert.AreEqual(screenshot.z, video.z, 1.0, all + ": blue");
        }

        // The mean colour, 0 to 255, of the rows between two heights measured from the bottom, away from the sides.
        private static Vector3 MeanOfScreen(Texture2D screen, float fromBottom, float toBottom)
        {
            Color32[] pixels = screen.GetPixels32();
            double r = 0, g = 0, b = 0;
            int count = 0;
            for (int y = (int)(screen.height * fromBottom); y < (int)(screen.height * toBottom); y++)
            for (int x = screen.width / 10; x < screen.width * 9 / 10; x++)
            {
                Color32 c = pixels[y * screen.width + x];
                r += c.r;
                g += c.g;
                b += c.b;
                count++;
            }
            return new Vector3((float)(r / count), (float)(g / count), (float)(b / count));
        }

        // The same for a decoded frame, whose rows run from the top, turned back to colour the way a player does (BT.601, limited range).
        private static Vector3 MeanOfVideo(byte[] i420, int width, int height, float fromTop, float toTop)
        {
            double r = 0, g = 0, b = 0;
            int count = 0;
            int uPlane = width * height;
            int vPlane = uPlane + (width / 2) * (height / 2);
            for (int y = (int)(height * fromTop); y < (int)(height * toTop); y++)
            for (int x = width / 10; x < width * 9 / 10; x++)
            {
                double luma = 1.164 * (i420[y * width + x] - 16);
                int chroma = (y / 2) * (width / 2) + x / 2;
                double u = i420[uPlane + chroma] - 128;
                double v = i420[vPlane + chroma] - 128;
                r += Clamp(luma + 1.596 * v);
                g += Clamp(luma - 0.392 * u - 0.813 * v);
                b += Clamp(luma + 2.017 * u);
                count++;
            }
            return new Vector3((float)(r / count), (float)(g / count), (float)(b / count));
        }

        private static Vector3 MeanOfVideoBox(byte[] i420, int width, int height, int x, int y)
        {
            int uPlane = width * height;
            int vPlane = uPlane + (width / 2) * (height / 2);
            double luma = 1.164 * (i420[y * width + x] - 16);
            int chroma = (y / 2) * (width / 2) + x / 2;
            double u = i420[uPlane + chroma] - 128;
            double v = i420[vPlane + chroma] - 128;
            return new Vector3((float)Clamp(luma + 1.596 * v), (float)Clamp(luma - 0.392 * u - 0.813 * v), (float)Clamp(luma + 2.017 * u));
        }

        private static double Clamp(double value) => value < 0 ? 0 : value > 255 ? 255 : value;

        // Each frame's bytes, walked cluster by cluster from the first; written apart from the package's own reader.
        private static List<byte[]> ReadFrames(byte[] file)
        {
            List<byte[]> frames = new List<byte[]>();
            int position = IndexOf(file, new byte[] { 0x1F, 0x43, 0xB6, 0x75 });
            while (position >= 0 && position + 23 <= file.Length && file[position] == 0x1F && file[position + 1] == 0x43)
            {
                int clusterBytes = ((file[position + 4] & 0x0F) << 24) | (file[position + 5] << 16) | (file[position + 6] << 8) | file[position + 7];
                int frameBytes = clusterBytes - 15;
                byte[] frame = new byte[frameBytes];
                Array.Copy(file, position + 23, frame, 0, frameBytes);
                frames.Add(frame);
                position += 8 + clusterBytes;
            }
            return frames;
        }

        private static int IndexOf(byte[] bytes, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= bytes.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && bytes[i + j] == pattern[j])
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }
    }
}
#endif
