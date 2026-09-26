using System;
using System.Collections.Generic;

namespace Protokite.Playtest
{
    /// <summary>The video codecs a recording can use.</summary>
    public enum ProtokitePlaytestVideoCodec
    {
        /// <summary>The cheaper to encode, and the default: a slow PC can afford it.</summary>
        Vp8 = 8,
        /// <summary>Smaller files for the same picture, at a higher cost to the processor.</summary>
        Vp9 = 9
    }

    /// <summary>How the pixels of a frame are laid out when they are handed to an encoder.</summary>
    internal enum ProtokitePlaytestPixelFormat
    {
        /// <summary>Tightly packed Y, then U, then V, each chroma plane half the width and half the height.</summary>
        I420
    }

    /// <summary>How a recording is encoded. Every value is a studio setting; <see cref="Defaults"/> is what a slow PC can afford.</summary>
    internal sealed class ProtokitePlaytestVideoEncoderSettings
    {
        public ProtokitePlaytestVideoCodec Codec = ProtokitePlaytestVideoCodec.Vp8;
        public int Width = 1280;
        public int Height = 720;
        public int FramesPerSecond = 15;
        public int BitrateKbps = 1500;
        public int Threads = 1;

        /// <summary>
        /// Higher is faster and looks worse (VP8 -16 to 16, VP9 -9 to 9). Unset means the codec's own default, so switching the
        /// codec alone never asks for a speed the other codec cannot take.
        /// </summary>
        public int? Speed;

        /// <summary>The speed the encoder is started with.</summary>
        public int SpeedToUse => Speed ?? (Codec == ProtokitePlaytestVideoCodec.Vp9 ? 8 : 12);

        /// <summary>A keyframe at least this often, so a player can seek and a cut-off file plays from a recent point.</summary>
        public int KeyframeIntervalSeconds = 10;

        /// <summary>VP8, one thread, speed 12, 15 frames a second, 1280×720 at 1.5 Mbps: 10–13% of a slow laptop's frame rate.</summary>
        public static ProtokitePlaytestVideoEncoderSettings Defaults => new ProtokitePlaytestVideoEncoderSettings();
    }

    /// <summary>One frame the encoder produced, ready to write.</summary>
    internal readonly struct ProtokitePlaytestEncodedFrame
    {
        public ProtokitePlaytestEncodedFrame(byte[] data, long timestampMs, bool isKeyframe)
        {
            Data = data;
            TimestampMs = timestampMs;
            IsKeyframe = isKeyframe;
        }

        public byte[] Data { get; }
        public long TimestampMs { get; }
        public bool IsKeyframe { get; }
    }

    /// <summary>
    /// Turns tightly packed I420 frames into compressed video. Each call blocks while it encodes, so it belongs on a worker
    /// thread, one thread at a time.
    /// </summary>
    internal interface IProtokitePlaytestVideoEncoder : IDisposable
    {
        /// <summary>The pixel layout this encoder takes, which the capture converts each frame to.</summary>
        ProtokitePlaytestPixelFormat InputPixelFormat { get; }

        /// <summary>Gets ready for frames of the settings' size. False, with why, when it cannot.</summary>
        bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error);

        /// <summary>Encodes one frame shown at <paramref name="timestampMs"/> for <paramref name="durationMs"/>; adds what came out, which can be nothing.</summary>
        bool Encode(byte[] i420, long timestampMs, long durationMs, bool forceKeyframe, List<ProtokitePlaytestEncodedFrame> output, out string error);

        /// <summary>Hands over anything the encoder still holds. Nothing can be encoded afterwards.</summary>
        bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error);
    }

    /// <summary>Checks the frame layout both sides of the encoder agree on.</summary>
    internal static class ProtokitePlaytestI420
    {
        /// <summary>The bytes one tightly packed I420 frame of this size takes.</summary>
        public static long FrameLength(int width, int height)
        {
            long chromaWidth = (width + 1) / 2;
            long chromaHeight = (height + 1) / 2;
            return (long)width * height + 2 * chromaWidth * chromaHeight;
        }
    }
}
