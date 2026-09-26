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

        public void CaptureFrame(long timestampMs)
        {
            Asked.Add(timestampMs);
            byte[] pixels = new byte[ProtokitePlaytestI420.FrameLength(Width, Height)];
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

    /// <summary>An encoder that makes a VP8-shaped frame of each frame at once, so the real file writer takes what it hands over.</summary>
    internal sealed class FakeVp8Encoder : IProtokitePlaytestVideoEncoder
    {
        private int _encoded;

        public ProtokitePlaytestPixelFormat InputPixelFormat { get; set; } = ProtokitePlaytestPixelFormat.I420;
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

        public bool Encode(byte[] i420, long timestampMs, long durationMs, bool forceKeyframe, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (_encoded == FailAtFrame)
            {
                error = "the encoder refused the frame";
                return false;
            }
            error = i420.LongLength == ProtokitePlaytestI420.FrameLength(Configured.Width, Configured.Height) ? null : "the frame is the wrong size";
            if (error != null)
                return false;
            DurationsMs.Add(durationMs);
            output.Add(new ProtokitePlaytestEncodedFrame(ProtokitePlaytestWebmTestFiles.MakeFrame(ProtokitePlaytestVideoCodec.Vp8, _encoded, FrameBytes, _encoded == 0),
                timestampMs, _encoded == 0));
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
