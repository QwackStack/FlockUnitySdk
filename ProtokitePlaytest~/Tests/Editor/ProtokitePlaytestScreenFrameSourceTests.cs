using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Protokite.Playtest.Tests
{
    /// <summary>The capture's graphics-card half, run on this editor's graphics card with pictures whose colours are known.</summary>
    public class ProtokitePlaytestScreenFrameSourceTests
    {
        private readonly List<Object> _made = new List<Object>();
        private readonly List<ProtokitePlaytestScreenFrameSource> _sources = new List<ProtokitePlaytestScreenFrameSource>();

        [TearDown]
        public void TearDown()
        {
            foreach (ProtokitePlaytestScreenFrameSource source in _sources)
                source.Dispose();
            _sources.Clear();
            foreach (Object made in _made)
                Object.DestroyImmediate(made);
            _made.Clear();
        }

        private ProtokitePlaytestScreenFrameSource Source(int width, int height, int blocks = 4)
        {
            Assume.That(SystemInfo.graphicsUVStartsAtTop, "These pictures are laid out for a graphics API whose textures start at the top");
            Assert.IsNull(ProtokitePlaytestScreenFrameSource.WhyThisDeviceCannotRecord(ProtokitePlaytestPixelFormat.I420), "Precondition: this editor's graphics card can capture");
            ProtokitePlaytestScreenFrameSource source = ProtokitePlaytestScreenFrameSource.Create(width, height, blocks, out string whyNot);
            Assert.IsNotNull(source, whyNot);
            _sources.Add(source);
            return source;
        }

        // A picture in sRGB colours whose first row in memory is its top, the way the screen's copy is where textures start at the
        // top (D3D11 and D3D12, measured); a blit copies rows as they are. Whether the screen's copy really is top first is proven
        // on the recordings of real players, not here.
        private Texture2D Picture(int width, int height, Func<int, int, Color32> colourAtTopDown)
        {
            Texture2D picture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            _made.Add(picture);
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = colourAtTopDown(x, y);
            picture.SetPixels32(pixels);
            picture.Apply();
            return picture;
        }

        private static readonly Color32 Red = new Color32(255, 0, 0, 255);
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);
        private static readonly Color32 Blue = new Color32(0, 0, 255, 255);
        // A colour between the ends: 0 and 255 look the same through the sRGB curve, so only a middle value shows a colour space read wrong.
        private static readonly Color32 SlateBlue = new Color32(51, 102, 153, 255);
        private static readonly Color32 White = new Color32(255, 255, 255, 255);
        private static readonly Color32 Black = new Color32(0, 0, 0, 255);

        private static Color32 Quadrants(int x, int y, int width, int height)
            => y < height / 2 ? (x < width / 2 ? Red : Green) : (x < width / 2 ? SlateBlue : White);

        private static ProtokitePlaytestCapturedFrame CaptureOne(ProtokitePlaytestScreenFrameSource source, Texture picture, long timestampMs = 0)
        {
            source.CaptureFromTexture(picture, timestampMs);
            List<ProtokitePlaytestCapturedFrame> frames = new List<ProtokitePlaytestCapturedFrame>();
            source.Stop(frames);
            Assert.AreEqual(1, frames.Count, "The frame arrives once the graphics card is waited for");
            return frames[0];
        }

        // BT.601, limited range: what an encoder expects of each colour.
        private static void AssertColourAt(byte[] i420, int width, int height, int x, int y, Color32 colour, string where)
        {
            double r = colour.r / 255.0, g = colour.g / 255.0, b = colour.b / 255.0;
            int luma = i420[y * width + x];
            int blue = i420[width * height + (y / 2) * (width / 2) + x / 2];
            int red = i420[width * height + width * height / 4 + (y / 2) * (width / 2) + x / 2];
            Assert.AreEqual(16 + 65.481 * r + 128.553 * g + 24.966 * b, luma, 1.0, where + ": brightness");
            Assert.AreEqual(128 - 37.797 * r - 74.203 * g + 112.0 * b, blue, 1.0, where + ": blue difference");
            Assert.AreEqual(128 + 112.0 * r - 93.786 * g - 18.214 * b, red, 1.0, where + ": red difference");
        }

        [Test]
        public void AFrameIsUprightAndItsColoursAreTheEncodersWithinOne()
        {
            const int width = 256, height = 128;
            ProtokitePlaytestScreenFrameSource source = Source(width, height);
            ProtokitePlaytestCapturedFrame frame = CaptureOne(source, Picture(width, height, (x, y) => Quadrants(x, y, width, height)), 1234);

            Assert.AreEqual(1234, frame.TimestampMs);
            Assert.AreEqual(ProtokitePlaytestI420.FrameLength(width, height), frame.Pixels.Length);
            AssertColourAt(frame.Pixels, width, height, 64, 32, Red, $"Top left ({QualitySettings.activeColorSpace})");
            AssertColourAt(frame.Pixels, width, height, 192, 32, Green, "Top right");
            AssertColourAt(frame.Pixels, width, height, 64, 96, SlateBlue, "Bottom left, a middle colour");
            AssertColourAt(frame.Pixels, width, height, 192, 96, White, "Bottom right");
        }

        [Test]
        public void APictureOfAnotherSizeIsScaledToTheVideo()
        {
            ProtokitePlaytestScreenFrameSource source = Source(256, 128);
            ProtokitePlaytestCapturedFrame frame = CaptureOne(source, Picture(640, 320, (x, y) => Quadrants(x, y, 640, 320)));
            AssertColourAt(frame.Pixels, 256, 128, 64, 32, Red, "Top left");
            AssertColourAt(frame.Pixels, 256, 128, 192, 96, White, "Bottom right");
        }

        [Test]
        public void EveryRowOfAFrameNotAMultipleOf64WideLinesUp()
        {
            // 848 is the width 854 is recorded at. An edge down the middle must sit at the same pixel on every row.
            const int width = 848, height = 480, edge = 424;
            ProtokitePlaytestScreenFrameSource source = Source(width, height);
            ProtokitePlaytestCapturedFrame frame = CaptureOne(source, Picture(width, height, (x, y) => x < edge ? Black : White));
            foreach (int row in new[] { 0, 1, height / 2, height - 2, height - 1 })
            {
                Assert.AreEqual(16, frame.Pixels[row * width + edge - 1], 1.0, $"Row {row}, left of the edge");
                Assert.AreEqual(235, frame.Pixels[row * width + edge], 1.0, $"Row {row}, right of the edge");
            }
        }

        [TestCase(854, 480)]
        [TestCase(848, 470)]
        [TestCase(0, 480)]
        public void ASizeThatIsNotAMultipleOf16IsRefused(int width, int height)
        {
            Assert.IsNull(ProtokitePlaytestScreenFrameSource.Create(width, height, 2, out string whyNot));
            StringAssert.Contains("multiple of 16", whyNot);
        }

        [Test]
        public void AnEncoderTakingAnotherLayoutIsToldTheCaptureCannotMakeIt()
        {
            ProtokitePlaytestPixelFormat unknown = (ProtokitePlaytestPixelFormat)99;
            string whyNot = ProtokitePlaytestScreenFrameSource.WhyThisDeviceCannotRecord(unknown);
            StringAssert.Contains("cannot make", whyNot);
            Assert.IsNull(ProtokitePlaytestScreenFrameSource.Create(new ProtokitePlaytestVideoSettings(), unknown, 2, out whyNot));
            StringAssert.Contains("99", whyNot);
        }

        // Frames on their way

        [Test]
        public void AtMostThreeFramesAreOnTheirWayAtOnce()
        {
            ProtokitePlaytestScreenFrameSource source = Source(64, 32, 8);
            Texture2D picture = Picture(64, 32, (x, y) => Red);
            for (int i = 0; i < ProtokitePlaytestScreenFrameSource.MostFramesOnTheirWay; i++)
            {
                Assert.IsTrue(source.IsReadyForAnotherFrame, "Frame " + i);
                source.CaptureFromTexture(picture, i);
            }
            Assert.IsFalse(source.IsReadyForAnotherFrame, "Full once three are on their way");
            source.CaptureFromTexture(picture, 99);
            List<ProtokitePlaytestCapturedFrame> frames = new List<ProtokitePlaytestCapturedFrame>();
            source.Stop(frames);
            CollectionAssert.AreEqual(new long[] { 0, 1, 2 }, frames.ConvertAll(frame => frame.TimestampMs), "The fourth was never asked for");
        }

        [Test]
        public void AFrameThatFindsNoFreeBlockIsDroppedAndCounted()
        {
            ProtokitePlaytestScreenFrameSource source = Source(64, 32, 1);
            Texture2D picture = Picture(64, 32, (x, y) => Green);
            source.CaptureFromTexture(picture, 0);
            source.CaptureFromTexture(picture, 1);
            List<ProtokitePlaytestCapturedFrame> frames = new List<ProtokitePlaytestCapturedFrame>();
            source.Stop(frames);
            Assert.AreEqual(1, frames.Count, "One block, one frame");
            Assert.AreEqual(1, source.FramesDroppedForWantOfABlock);
            Assert.AreEqual(0, source.FreeBlocks);
            source.ReturnBlock(frames[0].Pixels);
            source.ReturnBlock(frames[0].Pixels);
            Assert.AreEqual(1, source.FreeBlocks, "A block handed back twice is free once");
            source.ReturnBlock(new byte[frames[0].Pixels.Length]);
            Assert.AreEqual(1, source.FreeBlocks, "A block that is not the pool's is not taken in");
        }

        [Test]
        public void AFrameTheGraphicsCardLosesFreesItsPlace()
        {
            ProtokitePlaytestScreenFrameSource source = Source(64, 32, 8);
            Texture2D picture = Picture(64, 32, (x, y) => Blue);
            for (int i = 0; i < ProtokitePlaytestScreenFrameSource.MostFramesOnTheirWay; i++)
                source.CaptureFromTexture(picture, i);
            Assert.IsFalse(source.IsReadyForAnotherFrame, "Precondition: full");

            source.FinishReadback(7, false, default);
            Assert.AreEqual(1, source.FramesLostOnTheGraphicsCard);
            Assert.IsTrue(source.IsReadyForAnotherFrame, "A lost frame's place is free for the next");

            using (NativeArray<byte> tooShort = new NativeArray<byte>(10, Allocator.Temp))
                source.FinishReadback(8, true, tooShort);
            Assert.AreEqual(2, source.FramesLostOnTheGraphicsCard, "A frame of the wrong length counts as lost");
        }

        [Test]
        public void NothingIsCapturedAfterStopping()
        {
            ProtokitePlaytestScreenFrameSource source = Source(64, 32);
            List<ProtokitePlaytestCapturedFrame> frames = new List<ProtokitePlaytestCapturedFrame>();
            source.Stop(frames);
            Assert.IsFalse(source.IsReadyForAnotherFrame);
            source.CaptureFromTexture(Picture(64, 32, (x, y) => White), 0);
            source.Stop(frames);
            Assert.AreEqual(0, frames.Count);
        }
    }
}
