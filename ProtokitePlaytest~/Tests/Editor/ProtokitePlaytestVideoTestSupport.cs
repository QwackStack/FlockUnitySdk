using System;
using System.Collections.Generic;
using System.Threading;

namespace Protokite.Playtest.Tests
{
    /// <summary>Frames a test makes itself, each arriving when the test says: straight away, or held until it lets them go.</summary>
    internal sealed class FakeFrameSource : IProtokitePlaytestFrameSource
    {
        private readonly List<ProtokitePlaytestCapturedFrame> _onTheirWay = new List<ProtokitePlaytestCapturedFrame>();
        private readonly List<ProtokitePlaytestCapturedFrame> _arrived = new List<ProtokitePlaytestCapturedFrame>();
        private int _blocksReturned;

        public FakeFrameSource(int width = 64, int height = 48)
        {
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>False stands in for as many frames on their way as the source can hold.</summary>
        public bool Ready = true;

        /// <summary>True keeps each frame asked for on its way until <see cref="LetFramesArrive"/> or <see cref="Stop"/>.</summary>
        public bool HoldFrames;

        public readonly List<long> Asked = new List<long>();
        public bool Stopped;
        public bool Disposed;
        public int FramesLostOnTheGraphicsCard { get; set; }
        public int FramesDroppedForWantOfABlock { get; set; }
        public int BlocksReturned => Volatile.Read(ref _blocksReturned);
        public bool IsReadyForAnotherFrame => Ready && !Stopped;

        /// <summary>Waits, up to the bound, until every frame asked for but the newest has been through the encoder, as the time before a game's next frame would.</summary>
        public void WaitForTheEncoderToCatchUp(int mostMilliseconds = 500)
            => SpinWait.SpinUntil(() => BlocksReturned >= Asked.Count - 1, mostMilliseconds);

        public void CaptureFrame(long timestampMs)
        {
            Asked.Add(timestampMs);
            byte[] pixels = new byte[ProtokitePlaytestNv12.FrameLength(Width, Height)];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(timestampMs + i);
            (HoldFrames ? _onTheirWay : _arrived).Add(new ProtokitePlaytestCapturedFrame(pixels, timestampMs));
        }

        public void LetFramesArrive()
        {
            _arrived.AddRange(_onTheirWay);
            _onTheirWay.Clear();
        }

        public void TakeCapturedFrames(List<ProtokitePlaytestCapturedFrame> frames)
        {
            frames.AddRange(_arrived);
            _arrived.Clear();
        }

        public void ReturnBlock(byte[] pixels) => Interlocked.Increment(ref _blocksReturned);

        public void Stop(List<ProtokitePlaytestCapturedFrame> frames)
        {
            Stopped = true;
            LetFramesArrive();
            TakeCapturedFrames(frames);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>What tests of the real encoder share: the thread it works on, and a picture worth encoding.</summary>
    internal static class RealEncoding
    {
        /// <summary>The smallest size measured to start a graphics card encoder here: NVIDIA's refused 128x72 and took 256x144.</summary>
        public const int Width = 256;
        public const int Height = 144;

        /// <summary>Whether Windows offers an H.264 encoder on this PC's graphics card, which a recording uses by default.</summary>
        public static bool GraphicsCardEncoderOffered(out string whatIsOffered)
        {
            List<ProtokitePlaytestEncoderFound> found = ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out string whyNone);
            whatIsOffered = found == null ? whyNone : "Windows offers " + (found.Count == 0 ? "no H.264 encoder" : string.Join(", ", found));
            return found != null && found.Exists(encoder => encoder.OnGraphicsCard);
        }

        /// <summary>Leaves a test inconclusive on a PC whose graphics card has no H.264 encoder: what it checks is the encoder, not this PC.</summary>
        public static void AssumeAGraphicsCardEncoder()
        {
            bool offered = GraphicsCardEncoderOffered(out string whatIsOffered);
            NUnit.Framework.Assume.That(offered, "This test drives a graphics card's H.264 encoder, and this PC has none: " + whatIsOffered);
        }

        /// <summary>Runs the work on a thread of its own, as a recording's encoding thread does, and throws here what it threw there.</summary>
        public static void OnItsOwnThread(Action work)
        {
            Exception thrown = null;
            Thread thread = new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            }) { IsBackground = true, Name = "Test encoding thread" };
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("The encoding thread did not finish within a minute");
            if (thrown != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown).Throw();
        }

        /// <summary>An NV12 picture that moves every frame, so a lost, repeated or reordered frame shows when it is decoded.</summary>
        public static byte[] MovingPicture(int index, int width = Width, int height = Height)
        {
            byte[] nv12 = new byte[ProtokitePlaytestNv12.FrameLength(width, height)];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                nv12[y * width + x] = (byte)(((x + index * 7) / 16 % 2 == 0 ? 60 : 190) + (y * 40 / height));
            for (int i = width * height; i < nv12.Length; i++)
                nv12[i] = (byte)(128 + (index * 3 + i) % 16);
            return nv12;
        }

        /// <summary>How close a decoded picture's brightness is to the one sent, in decibels; above 28 is the same picture.</summary>
        public static double LumaPsnr(byte[] expected, byte[] actual, int width = Width, int height = Height)
        {
            double squared = 0;
            for (int i = 0; i < width * height; i++)
            {
                double difference = expected[i] - actual[i];
                squared += difference * difference;
            }
            double mean = squared / (width * height);
            return mean == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mean);
        }
    }

    /// <summary>An encoder that makes an H.264-shaped frame of each frame at once, so the real file writer takes what it hands over.</summary>
    internal sealed class FakeH264Encoder : IProtokitePlaytestVideoEncoder
    {
        private int _encoded;

        public ProtokitePlaytestPixelFormat InputPixelFormat { get; set; } = ProtokitePlaytestPixelFormat.Nv12;
        public string Description => _encoded > 0 ? "the test's own encoder" : null;
        public ProtokitePlaytestVideoEncoderSettings Configured;
        public int FrameBytes = 40;

        /// <summary>The frame, counting from 0, whose encode fails; -1 for none.</summary>
        public int FailAtFrame = -1;

        public bool Finished;
        public bool Disposed;
        public readonly List<long> DurationsMs = new List<long>();

        public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error)
        {
            Configured = settings;
            error = null;
            return true;
        }

        /// <summary>Set to make the encoder fail to start, the way a graphics card's that is busy elsewhere does.</summary>
        public string RefuseToStart;
        public bool Started;

        public bool Start(out string error)
        {
            error = RefuseToStart;
            Started = error == null;
            return Started;
        }

        public bool Encode(byte[] pixels, long timestampMs, long durationMs, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (_encoded == FailAtFrame)
            {
                error = "the encoder refused the frame";
                return false;
            }
            error = Finished ? "the encoder has finished."
                : pixels == null || pixels.LongLength != ProtokitePlaytestNv12.FrameLength(Configured.Width, Configured.Height) ? "the frame is the wrong size"
                : null;
            if (error != null)
                return false;
            DurationsMs.Add(durationMs);
            output.Add(new ProtokitePlaytestEncodedFrame(ProtokitePlaytestMp4TestFiles.MakeFrame(_encoded, FrameBytes, _encoded == 0), timestampMs, _encoded == 0,
                _encoded == 0 ? ProtokitePlaytestMp4TestFiles.DecoderSettings : null));
            _encoded++;
            return true;
        }

        public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            Finished = true;
            error = null;
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
