using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Protokite.Playtest.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Protokite.Playtest.Tests
{
    public class ProtokitePlaytestVideoEncoderTests
    {
        private const int Width = 320;
        private const int Height = 240;
        private const int FramesPerSecond = 15;
        private const long FrameMs = 1000 / FramesPerSecond;

        [SetUp]
        public void SetUp() => ProtokitePlaytestLibVpx.ResetForNewLaunch();

        [TearDown]
        public void TearDown()
        {
            ProtokitePlaytestLibVpx.ReadWrapperVersionForTesting = null;
            ProtokitePlaytestLibVpx.ReadProcessArchitectureForTesting = null;
            ProtokitePlaytestLibVpx.ResetForNewLaunch();
        }

        private static ProtokitePlaytestVideoEncoderSettings Settings(ProtokitePlaytestVideoCodec codec) => new ProtokitePlaytestVideoEncoderSettings
        {
            Codec = codec,
            Width = Width,
            Height = Height,
            FramesPerSecond = FramesPerSecond,
            BitrateKbps = 1500,
            Speed = codec == ProtokitePlaytestVideoCodec.Vp8 ? 12 : 8
        };

        // A picture that moves every frame, so a lost, repeated or reordered frame shows in the comparison.
        private static byte[] Frame(int index)
        {
            byte[] i420 = new byte[ProtokitePlaytestI420.FrameLength(Width, Height)];
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                i420[y * Width + x] = (byte)(((x + index * 7) / 16 % 2 == 0 ? 60 : 190) + (y * 40 / Height));
            for (int i = Width * Height; i < i420.Length; i++)
                i420[i] = (byte)(128 + (index * 3 + i) % 16);
            return i420;
        }

        private static double LumaPsnr(byte[] expected, byte[] actual)
        {
            double squared = 0;
            for (int i = 0; i < Width * Height; i++)
            {
                double d = expected[i] - actual[i];
                squared += d * d;
            }
            double mean = squared / (Width * Height);
            return mean == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mean);
        }

        private static IProtokitePlaytestVideoEncoder Encoder()
        {
            IProtokitePlaytestVideoEncoder encoder = ProtokitePlaytestLibVpx.CreateEncoder(out string whyNot);
            Assert.IsNotNull(encoder, "Precondition: this editor carries the encoder. " + whyNot);
            return encoder;
        }

        // Encoding

        [TestCase(8)]
        [TestCase(9)]
        public void EveryFrameComesOutAndDecodesFrameForFrameWithTheCodecAskedFor(int codecNumber)
        {
            ProtokitePlaytestVideoCodec codec = (ProtokitePlaytestVideoCodec)codecNumber;
            const int frames = 30;
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            using (IProtokitePlaytestVideoEncoder encoder = Encoder())
            {
                Assert.IsTrue(encoder.Configure(Settings(codec), out string error), error);
                for (int i = 0; i < frames; i++)
                    Assert.IsTrue(encoder.Encode(Frame(i), i * FrameMs, FrameMs, i == 20, output, out error), error);
                Assert.IsTrue(encoder.Finish(output, out error), error);
            }

            Assert.AreEqual(frames, output.Count, "One compressed frame per frame sent: none held back, none dropped");
            Assert.IsTrue(output[0].IsKeyframe, "A video starts on a keyframe");
            Assert.IsTrue(output[20].IsKeyframe, "A forced keyframe is honoured");
            Assert.IsFalse(output[1].IsKeyframe, "Frames in between are not all keyframes");
            CollectionAssert.AreEqual(Enumerable.Range(0, frames).Select(i => i * FrameMs).ToArray(), output.Select(f => f.TimestampMs).ToArray(),
                "Timestamps are the milliseconds each frame was sent with");

            // Decoded with the codec asked for: a stream in the other codec would not decode at all.
            using (ProtokitePlaytestLibVpx.Decoder decoder = new ProtokitePlaytestLibVpx.Decoder(codec))
            {
                for (int i = 0; i < frames; i++)
                {
                    byte[] decoded = decoder.Decode(output[i].Data, Width, Height, out string error);
                    Assert.IsNotNull(decoded, $"Frame {i}: {error}");
                    Assert.That(LumaPsnr(Frame(i), decoded), Is.GreaterThan(28), $"Frame {i} decodes to the picture that was sent");
                }
            }
        }

        [Test]
        public void AFrameOfTheWrongLengthIsRefusedBeforeTheEncoderReadsIt()
        {
            using (IProtokitePlaytestVideoEncoder encoder = Encoder())
            {
                Assert.IsTrue(encoder.Configure(Settings(ProtokitePlaytestVideoCodec.Vp8), out string error), error);
                List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
                byte[] tooShort = new byte[ProtokitePlaytestI420.FrameLength(Width, Height) - 1];
                Assert.IsFalse(encoder.Encode(tooShort, 0, FrameMs, false, output, out error));
                StringAssert.Contains($"{tooShort.Length} bytes", error);
                Assert.IsFalse(encoder.Encode(null, 0, FrameMs, false, output, out error), "No frame at all");
                CollectionAssert.IsEmpty(output);

                Assert.IsTrue(encoder.Encode(Frame(0), 0, FrameMs, false, output, out error), "The encoder still works afterwards: " + error);
            }
        }

        [Test]
        public void SettingsTheEncoderCannotTakeAreRefusedWithWhy()
        {
            (ProtokitePlaytestVideoEncoderSettings settings, string why)[] cases =
            {
                (With(s => s.Width = 321), "even"),
                (With(s => s.Height = 0), "even"),
                (With(s => s.FramesPerSecond = 0), "at least 1"),
                (With(s => s.Threads = 0), "at least 1"),
                (With(s => s.KeyframeIntervalSeconds = 0), "keyframe interval must be at least 1"),
                (With(s => s.Codec = (ProtokitePlaytestVideoCodec)7), "does not include the codec"),
                (With(s => { s.Codec = ProtokitePlaytestVideoCodec.Vp9; s.Speed = 20; }), "speed")
            };
            foreach ((ProtokitePlaytestVideoEncoderSettings settings, string why) in cases)
            {
                using (IProtokitePlaytestVideoEncoder encoder = Encoder())
                {
                    Assert.IsFalse(encoder.Configure(settings, out string error), $"Expected a refusal about '{why}'");
                    StringAssert.Contains(why, error);
                }
            }
        }

        [Test]
        public void NothingIsEncodedBeforeConfiguringOrAfterFinishing()
        {
            List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
            using (IProtokitePlaytestVideoEncoder encoder = Encoder())
            {
                Assert.IsFalse(encoder.Encode(Frame(0), 0, FrameMs, false, output, out string error));
                StringAssert.Contains("not configured", error);

                Assert.IsTrue(encoder.Configure(Settings(ProtokitePlaytestVideoCodec.Vp8), out error), error);
                Assert.IsFalse(encoder.Configure(Settings(ProtokitePlaytestVideoCodec.Vp8), out error), "Configured once");
                Assert.IsTrue(encoder.Finish(output, out error), error);
                Assert.IsFalse(encoder.Encode(Frame(0), 0, FrameMs, false, output, out error));
                StringAssert.Contains("finished", error);
            }
        }

        [Test]
        public void TheDefaultsAreTheOnesASlowPcCanAfford()
        {
            ProtokitePlaytestVideoEncoderSettings defaults = ProtokitePlaytestVideoEncoderSettings.Defaults;
            Assert.AreEqual(ProtokitePlaytestVideoCodec.Vp8, defaults.Codec);
            Assert.AreEqual((1280, 720, 15, 1500, 1, 12), (defaults.Width, defaults.Height, defaults.FramesPerSecond, defaults.BitrateKbps, defaults.Threads, defaults.SpeedToUse));
            using (IProtokitePlaytestVideoEncoder encoder = Encoder())
                Assert.IsTrue(encoder.Configure(defaults, out string error), "The defaults start the encoder: " + error);

            // A studio that changes only the codec gets that codec's own default speed, not VP8's.
            ProtokitePlaytestVideoEncoderSettings vp9 = ProtokitePlaytestVideoEncoderSettings.Defaults;
            vp9.Codec = ProtokitePlaytestVideoCodec.Vp9;
            Assert.AreEqual(8, vp9.SpeedToUse);
            using (IProtokitePlaytestVideoEncoder encoder = Encoder())
                Assert.IsTrue(encoder.Configure(vp9, out string error), "VP9 with every other default starts: " + error);
        }

        private static ProtokitePlaytestVideoEncoderSettings With(Action<ProtokitePlaytestVideoEncoderSettings> change)
        {
            ProtokitePlaytestVideoEncoderSettings settings = Settings(ProtokitePlaytestVideoCodec.Vp8);
            change(settings);
            return settings;
        }

        // The library itself

        [Test]
        public void TheDllIsTheOneThisPackageWasWrittenFor()
        {
            Assert.IsTrue(ProtokitePlaytestLibVpx.IsAvailable(out string whyNot), whyNot);
            Assert.AreEqual("v1.17.0", ProtokitePlaytestLibVpx.LibraryVersion);
            Assert.IsTrue(ProtokitePlaytestLibVpx.HasCodec(ProtokitePlaytestVideoCodec.Vp8));
            Assert.IsTrue(ProtokitePlaytestLibVpx.HasCodec(ProtokitePlaytestVideoCodec.Vp9));
        }

        [Test]
        public void AMissingDllMeansNoVideoReportedOnce()
        {
            ProtokitePlaytestLibVpx.ReadWrapperVersionForTesting = () => throw new DllNotFoundException("protokite_vpx");
            LogAssert.Expect(LogType.Warning, new Regex("records no playtest video: protokite_vpx.dll is missing.*Everything else"));
            Assert.IsNull(ProtokitePlaytestLibVpx.CreateEncoder(out string whyNot));
            StringAssert.Contains("missing", whyNot);
            Assert.IsNull(ProtokitePlaytestLibVpx.CreateEncoder(out _), "Still no video");
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase(System.Runtime.InteropServices.Architecture.X86)]
        [TestCase(System.Runtime.InteropServices.Architecture.Arm64)]
        public void A32BitOrArmWindowsBuildIsToldVideoIsFor64BitWindowsWithoutAWarning(System.Runtime.InteropServices.Architecture architecture)
        {
            int dllAsked = 0;
            ProtokitePlaytestLibVpx.ReadProcessArchitectureForTesting = () => architecture;
            ProtokitePlaytestLibVpx.ReadWrapperVersionForTesting = () => { dllAsked++; throw new DllNotFoundException("protokite_vpx"); };
            LogAssert.Expect(LogType.Log, new Regex("records no playtest video: video is recorded on 64-bit Windows only"));
            Assert.IsNull(ProtokitePlaytestLibVpx.CreateEncoder(out string whyNot));
            StringAssert.Contains("64-bit Windows only", whyNot);
            Assert.AreEqual(0, dllAsked, "The DLL is left out of these builds on purpose, so it is not looked for");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AnX64ProcessStillLooksForTheDll()
        {
            ProtokitePlaytestLibVpx.ReadProcessArchitectureForTesting = () => System.Runtime.InteropServices.Architecture.X64;
            Assert.IsTrue(ProtokitePlaytestLibVpx.ShouldHaveVideo);
            Assert.IsTrue(ProtokitePlaytestLibVpx.IsAvailable(out string whyNot), whyNot);
        }

        [Test]
        public void ARuntimeThatCannotNameItsArchitectureStillTriesTheDll()
        {
            ProtokitePlaytestLibVpx.ReadProcessArchitectureForTesting = () => throw new PlatformNotSupportedException();
            Assert.IsTrue(ProtokitePlaytestLibVpx.ShouldHaveVideo);
            Assert.IsTrue(ProtokitePlaytestLibVpx.IsAvailable(out string whyNot), whyNot);
        }

        [Test]
        public void ADllOfAnotherVersionIsRefusedRatherThanCalled()
        {
            ProtokitePlaytestLibVpx.ReadWrapperVersionForTesting = () => ProtokitePlaytestLibVpx.WrapperVersion + 1;
            LogAssert.Expect(LogType.Warning, new Regex("records no playtest video: protokite_vpx.dll is version"));
            Assert.IsNull(ProtokitePlaytestLibVpx.CreateEncoder(out string whyNot));
            StringAssert.Contains($"needs version {ProtokitePlaytestLibVpx.WrapperVersion}", whyNot);
        }

        [Test]
        public void TheDllIsImportedForTheWindowsEditorAndWindowsPlayersOnly()
        {
            string path = PackageFolder().assetPath + "/Runtime/Plugins/x86_64/" + ProtokitePlaytestNativePluginImport.PluginFileName;
            PluginImporter plugin = AssetImporter.GetAtPath(path) as PluginImporter;
            Assert.IsNotNull(plugin, "The DLL is at " + path);
            Assert.IsTrue(ProtokitePlaytestNativePluginImport.HasTheRightPlatforms(plugin));
            Assert.IsFalse(plugin.GetCompatibleWithPlatform(BuildTarget.Android), "Android builds leave it out");
            Assert.IsFalse(plugin.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows), "32-bit Windows builds leave it out");
        }

        [Test]
        public void ACopyImportedWithTheWrongPlatformsIsPutRight()
        {
            string path = PackageFolder().assetPath + "/Runtime/Plugins/x86_64/" + ProtokitePlaytestNativePluginImport.PluginFileName;
            PluginImporter plugin = (PluginImporter)AssetImporter.GetAtPath(path);
            try
            {
                plugin.SetCompatibleWithPlatform(BuildTarget.Android, true);
                plugin.SaveAndReimport();
                Assert.IsFalse(ProtokitePlaytestNativePluginImport.HasTheRightPlatforms((PluginImporter)AssetImporter.GetAtPath(path)), "Precondition: now wrong");

                Assert.AreEqual(1, ProtokitePlaytestNativePluginImport.PutRightWhereWrong());
                Assert.IsTrue(ProtokitePlaytestNativePluginImport.HasTheRightPlatforms((PluginImporter)AssetImporter.GetAtPath(path)));
                Assert.AreEqual(0, ProtokitePlaytestNativePluginImport.PutRightWhereWrong(), "Nothing left to change");
            }
            finally
            {
                ProtokitePlaytestNativePluginImport.PutRightWhereWrong();
            }
        }

        [Test]
        public void TheWrongPlatformsArePutRightEveryTimeTheEditorLoads()
        {
            System.Reflection.MethodInfo onLoad = typeof(ProtokitePlaytestNativePluginImport).GetMethod("PutRightAfterLoading",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(onLoad);
            Assert.IsNotNull(Attribute.GetCustomAttribute(onLoad, typeof(InitializeOnLoadMethodAttribute)), "Unity calls it after every script load");
        }

        [Test]
        public void TheDllShipsWithLibvpxsLicenceAndPatents()
        {
            string folder = Path.Combine(PackageFolder().resolvedPath, "Runtime", "Plugins", "x86_64");
            StringAssert.Contains("Copyright (c) 2010, The WebM Project authors", File.ReadAllText(Path.Combine(folder, "libvpx-LICENSE.txt")));
            StringAssert.Contains("Additional IP Rights Grant (Patents)", File.ReadAllText(Path.Combine(folder, "libvpx-PATENTS.txt")));
        }

        [Test]
        public void NothingButTheLibvpxFileNamesLibvpx()
        {
            string runtime = Path.Combine(PackageFolder().resolvedPath, "Runtime");
            string[] naming = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) != "ProtokitePlaytestLibVpx.cs")
                .Where(file => Regex.IsMatch(File.ReadAllText(file), "libvpx|pk_vpx|protokite_vpx|LibVpx", RegexOptions.IgnoreCase))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.IsTrue(File.Exists(Path.Combine(runtime, "Video", "ProtokitePlaytestLibVpx.cs")), "Positive control: the libvpx file is where this scan expects");
            CollectionAssert.IsEmpty(naming, "Everything else goes through IProtokitePlaytestVideoEncoder");
        }

        // The seam a second encoder plugs into

        [Test]
        public void TheFakeAndTheRealEncoderKeepTheSameContract()
        {
            foreach (IProtokitePlaytestVideoEncoder encoder in new[] { new FakeVideoEncoder(), Encoder() })
            {
                using (encoder)
                {
                    List<ProtokitePlaytestEncodedFrame> output = new List<ProtokitePlaytestEncodedFrame>();
                    Assert.IsTrue(encoder.Configure(Settings(ProtokitePlaytestVideoCodec.Vp8), out string error), error);
                    for (int i = 0; i < 3; i++)
                        Assert.IsTrue(encoder.Encode(Frame(i), i * FrameMs, FrameMs, false, output, out error), error);
                    Assert.IsTrue(encoder.Finish(output, out error), error);
                    Assert.IsFalse(encoder.Encode(Frame(3), 3 * FrameMs, FrameMs, false, output, out _), encoder.GetType().Name + " refuses frames after finishing");
                    Assert.AreEqual(3, output.Count, encoder.GetType().Name);
                    Assert.IsTrue(output[0].IsKeyframe, encoder.GetType().Name);
                }
            }
        }

        private static UnityEditor.PackageManager.PackageInfo PackageFolder()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly);
            Assert.IsNotNull(package, "The playtest is installed as a package in this project");
            return package;
        }
    }

    /// <summary>An encoder that keeps the contract and does no work, for code that records without needing real video.</summary>
    internal sealed class FakeVideoEncoder : IProtokitePlaytestVideoEncoder
    {
        private ProtokitePlaytestVideoEncoderSettings _settings;
        private bool _finished;

        public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error)
        {
            error = _settings != null ? "The encoder is already configured." : null;
            _settings ??= settings;
            return error == null;
        }

        public bool Encode(byte[] i420, long timestampMs, long durationMs, bool forceKeyframe, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            error = _settings == null ? "The encoder is not configured." : _finished ? "The encoder has finished." :
                i420 == null || i420.LongLength != ProtokitePlaytestI420.FrameLength(_settings.Width, _settings.Height) ? "The frame is the wrong size." : null;
            if (error != null)
                return false;
            output.Add(new ProtokitePlaytestEncodedFrame(new byte[] { 1 }, timestampMs, output.Count == 0 || forceKeyframe));
            return true;
        }

        public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            error = _settings == null ? "The encoder is not configured." : null;
            _finished = true;
            return error == null;
        }

        public void Dispose() { }
    }
}
