using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Protokite.Playtest
{
    /// <summary>
    /// Frames of the game's screen: copied into a texture at the end of a frame, scaled to the video's size, converted to the
    /// encoder's pixel layout by a compute shader, and read back without waiting for the graphics card. A frame arrives a few
    /// frames after it was asked for. Main thread only, except <see cref="ReturnBlock"/>.
    /// </summary>
    internal sealed class ProtokitePlaytestScreenFrameSource : IProtokitePlaytestFrameSource
    {
        /// <summary>Frames on their way back from the graphics card at once; a capture time that finds this many is dropped.</summary>
        internal const int MostFramesOnTheirWay = 3;

        /// <summary>The compute shader, in the package's Resources so every build carries it.</summary>
        internal const string ConversionShaderName = "ProtokitePlaytestRgbaToNv12";

        private readonly ComputeShader _convert;
        private readonly int _lumaKernel;
        private readonly int _chromaKernel;
        private readonly ComputeBuffer _pixels;
        private readonly RenderTexture _video;
        private readonly ProtokitePlaytestFrameBlocks _blocks;
        private readonly int _frameBytes;
        private readonly List<ProtokitePlaytestCapturedFrame> _arrived = new List<ProtokitePlaytestCapturedFrame>();
        private RenderTexture _screen;
        private int _pictureLeft;
        private int _pictureTop;
        private int _framesOnTheirWay;
        private bool _stopped;

        public int Width { get; }
        public int Height { get; }
        public int FramesLostOnTheGraphicsCard { get; private set; }
        public int FramesDroppedForWantOfABlock { get; private set; }
        public bool IsReadyForAnotherFrame => !_stopped && _framesOnTheirWay < MostFramesOnTheirWay;

        /// <summary>A source for the screen, or null with why this device cannot record it.</summary>
        public static ProtokitePlaytestScreenFrameSource Create(ProtokitePlaytestVideoSettings settings, ProtokitePlaytestPixelFormat format, int blocks,
            out string whyNot)
        {
            whyNot = WhyThisDeviceCannotRecord(format);
            if (whyNot != null)
                return null;
            if (!ProtokitePlaytestVideoSettings.FitVideoSize(Screen.width, Screen.height, settings.MaxVideoWidth, settings.MaxVideoHeight, out int width, out int height))
            {
                whyNot = "the screen has no size.";
                return null;
            }
            return Create(width, height, blocks, out whyNot);
        }

        /// <summary>A source for frames of exactly this size, each side a multiple of 16; tests hand it textures of their own.</summary>
        internal static ProtokitePlaytestScreenFrameSource Create(int width, int height, int blocks, out string whyNot)
        {
            whyNot = null;
            if (width % ProtokitePlaytestVideoSettings.SideMultiple != 0 || height % ProtokitePlaytestVideoSettings.SideMultiple != 0 || width <= 0 || height <= 0)
            {
                whyNot = $"a {width}x{height} video cannot be converted: each side must be a multiple of {ProtokitePlaytestVideoSettings.SideMultiple}.";
                return null;
            }
            ComputeShader shader = Resources.Load<ComputeShader>(ConversionShaderName);
            if (shader == null)
            {
                whyNot = $"the compute shader {ConversionShaderName} is missing from the build.";
                return null;
            }
            return new ProtokitePlaytestScreenFrameSource(shader, width, height, blocks);
        }

        /// <summary>Why this device cannot capture frames for an encoder taking <paramref name="format"/>, or null when it can.</summary>
        internal static string WhyThisDeviceCannotRecord(ProtokitePlaytestPixelFormat format)
        {
            if (format != ProtokitePlaytestPixelFormat.Nv12)
                return $"the encoder takes {format} frames, which this capture cannot make.";
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return "this process draws nothing (a server build, or one started with -nographics).";
            if (!SystemInfo.supportsComputeShaders)
                return "this graphics card cannot run compute shaders, which the capture converts frames with.";
            if (!SystemInfo.supportsAsyncGPUReadback)
                return "this graphics card cannot hand frames back without making the game wait.";
            return null;
        }

        private ProtokitePlaytestScreenFrameSource(ComputeShader shader, int width, int height, int blocks)
        {
            Width = width;
            Height = height;
            _frameBytes = (int)ProtokitePlaytestNv12.FrameLength(width, height);
            _blocks = new ProtokitePlaytestFrameBlocks(blocks, _frameBytes);
            _video = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "Protokite Playtest video" };
            _video.Create();
            _pixels = new ComputeBuffer(_frameBytes / 4, 4);

            // Instantiated so the settings below belong to this source alone.
            _convert = Object.Instantiate(shader);
            _lumaKernel = _convert.FindKernel("LumaKernel");
            _chromaKernel = _convert.FindKernel("ChromaKernel");
            _convert.SetInt("Width", width);
            _convert.SetInt("Height", height);
            // Rows come back top first where a texture's first row is the top (D3D11 and D3D12 measured); OpenGL keeps it at the bottom.
            _convert.SetInt("FlipVertically", SystemInfo.graphicsUVStartsAtTop ? 0 : 1);
            _convert.SetInt("SourceIsLinear", QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
            _convert.SetTexture(_lumaKernel, "Source", _video);
            _convert.SetTexture(_chromaKernel, "Source", _video);
            _convert.SetBuffer(_lumaKernel, "Output", _pixels);
            _convert.SetBuffer(_chromaKernel, "Output", _pixels);
            PlaceThePicture(0, 0);
        }

        public void CaptureFrame(long timestampMs)
        {
            if (!IsReadyForAnotherFrame)
                return;
            // A window resized or a phone turned mid-recording is recorded in the same video size, at its own shape (see CaptureFromTexture).
            if (_screen == null || _screen.width != Screen.width || _screen.height != Screen.height)
            {
                if (Screen.width <= 0 || Screen.height <= 0)
                    return;
                ReleaseScreenTexture();
                _screen = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "Protokite Playtest screen" };
                _screen.Create();
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_screen);
            CaptureFromTexture(_screen, timestampMs);
        }

        /// <summary>Scales a texture to the video's size, converts it and asks for it back. The screen's frames come this way.</summary>
        internal void CaptureFromTexture(Texture source, long timestampMs)
        {
            if (!IsReadyForAnotherFrame)
                return;
            PictureMargins(source.width, source.height, Width, Height, out int left, out int top);
            if (left != _pictureLeft || top != _pictureTop)
                PlaceThePicture(left, top);
            // The picture fills the space between the margins; the shader makes the margins black, so what the scale puts there never shows.
            Vector2 scale = new Vector2((float)Width / (Width - 2 * left), (float)Height / (Height - 2 * top));
            Graphics.Blit(source, _video, scale, new Vector2(-left * scale.x / Width, -top * scale.y / Height));
            _convert.Dispatch(_lumaKernel, (Width * Height / 4 + 63) / 64, 1, 1);
            // Two chroma samples a thread: a quarter of the pixels each have one, so an eighth of them is the count of threads.
            _convert.Dispatch(_chromaKernel, (Width * Height / 8 + 63) / 64, 1, 1);
            _framesOnTheirWay++;
            AsyncGPUReadback.Request(_pixels, request => HandleReadback(timestampMs, request));
        }

        /// <summary>The black margins (each side alike, even) that keep a picture at its own shape in the video; none for a difference under 16 pixels.</summary>
        internal static void PictureMargins(int pictureWidth, int pictureHeight, int videoWidth, int videoHeight, out int left, out int top)
        {
            left = 0;
            top = 0;
            if (pictureWidth <= 0 || pictureHeight <= 0)
                return;
            double scale = System.Math.Min((double)videoWidth / pictureWidth, (double)videoHeight / pictureHeight);
            int fittedWidth = (int)System.Math.Round(pictureWidth * scale);
            int fittedHeight = (int)System.Math.Round(pictureHeight * scale);
            if (videoWidth - fittedWidth >= ProtokitePlaytestVideoSettings.SideMultiple)
                left = (videoWidth - fittedWidth) / 4 * 2;
            if (videoHeight - fittedHeight >= ProtokitePlaytestVideoSettings.SideMultiple)
                top = (videoHeight - fittedHeight) / 4 * 2;
        }

        private void PlaceThePicture(int left, int top)
        {
            _pictureLeft = left;
            _pictureTop = top;
            _convert.SetInt("PictureLeft", left);
            _convert.SetInt("PictureTop", top);
            _convert.SetInt("PictureRight", Width - left);
            _convert.SetInt("PictureBottom", Height - top);
        }

        private void HandleReadback(long timestampMs, AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
                FinishReadback(timestampMs, false, default);
            else
                FinishReadback(timestampMs, true, request.GetData<byte>());
        }

        /// <summary>What becomes of one frame back from the graphics card: its slot freed whatever happened, and its pixels kept when a block is free.</summary>
        internal void FinishReadback(long timestampMs, bool arrived, NativeArray<byte> pixels)
        {
            _framesOnTheirWay--;
            if (!arrived || pixels.Length != _frameBytes)
            {
                FramesLostOnTheGraphicsCard++;
                return;
            }
            byte[] block = _blocks.Take();
            if (block == null)
            {
                FramesDroppedForWantOfABlock++;
                return;
            }
            pixels.CopyTo(block);
            _arrived.Add(new ProtokitePlaytestCapturedFrame(block, timestampMs));
        }

        public void TakeCapturedFrames(List<ProtokitePlaytestCapturedFrame> frames)
        {
            frames.AddRange(_arrived);
            _arrived.Clear();
        }

        public void ReturnBlock(byte[] pixels) => _blocks.Return(pixels);

        internal int FreeBlocks => _blocks.FreeCount;

        public void Stop(List<ProtokitePlaytestCapturedFrame> frames)
        {
            if (!_stopped)
            {
                _stopped = true;
                // Hands back every frame still on its way, running their callbacks, so the last captures are not lost.
                AsyncGPUReadback.WaitAllRequests();
            }
            TakeCapturedFrames(frames);
        }

        public void TakeFramesOnTheirWay(List<ProtokitePlaytestCapturedFrame> frames)
        {
            // The wait finishes the game's own readbacks too, so it is made only when frames of ours are on their way.
            if (!_stopped && _framesOnTheirWay > 0)
                AsyncGPUReadback.WaitAllRequests();
            TakeCapturedFrames(frames);
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                _stopped = true;
                AsyncGPUReadback.WaitAllRequests();
            }
            ReleaseScreenTexture();
            if (_video != null)
                DestroyNow(_video);
            _pixels?.Release();
            if (_convert != null)
                DestroyNow(_convert);
        }

        // Outside Play Mode an object can only be destroyed at once.
        private static void DestroyNow(Object thing)
        {
            if (Application.isPlaying)
                Object.Destroy(thing);
            else
                Object.DestroyImmediate(thing);
        }

        private void ReleaseScreenTexture()
        {
            if (_screen == null)
                return;
            _screen.Release();
            DestroyNow(_screen);
            _screen = null;
        }
    }
}
