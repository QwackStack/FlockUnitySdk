using System;
using System.Collections.Generic;

namespace Protokite.Playtest
{
    /// <summary>How the pixels of a frame are laid out when they are handed to an encoder.</summary>
    internal enum ProtokitePlaytestPixelFormat
    {
        /// <summary>The Y plane, then one plane of U and V interleaved, each half the width and half the height of the picture.</summary>
        Nv12
    }

    /// <summary>How a recording is encoded. <see cref="Defaults"/> is what the playtest records with unless a studio changes it.</summary>
    internal sealed class ProtokitePlaytestVideoEncoderSettings
    {
        public int Width = 1280;
        public int Height = 720;
        public int FramesPerSecond = 15;
        public int BitrateKbps = 1500;

        /// <summary>A keyframe at least this often, so a player can seek and a cut-off file plays from a recent point.</summary>
        public int KeyframeIntervalSeconds = 10;

        /// <summary>Whether Windows' own software encoder may be used where the graphics card has none; it costs the game processor time.</summary>
        public bool AllowSoftwareEncoder;

        /// <summary>The game's graphics card maker (PCI vendor ID), whose encoder is tried first; 0 when unknown.</summary>
        public int GraphicsCardVendorId;

        /// <summary>1280×720 at 15 frames a second and 1.5 Mbps, on the graphics card's encoder only.</summary>
        public static ProtokitePlaytestVideoEncoderSettings Defaults => new ProtokitePlaytestVideoEncoderSettings();
    }

    /// <summary>One frame the encoder produced, ready to write.</summary>
    internal readonly struct ProtokitePlaytestEncodedFrame
    {
        public ProtokitePlaytestEncodedFrame(byte[] data, long timestampMs, bool isKeyframe, byte[] decoderSettings = null)
        {
            Data = data;
            TimestampMs = timestampMs;
            IsKeyframe = isKeyframe;
            DecoderSettings = decoderSettings;
        }

        /// <summary>The frame as the file stores it: for H.264, each of its units after its length in four bytes.</summary>
        public byte[] Data { get; }
        public long TimestampMs { get; }
        public bool IsKeyframe { get; }

        /// <summary>What a player needs before the first frame (H.264's sequence and picture settings), carried by the first frame only.</summary>
        public byte[] DecoderSettings { get; }
    }

    /// <summary>Turns frames of <see cref="InputPixelFormat"/> into compressed video; every call after <see cref="Configure"/> blocks and belongs to one worker thread, which also disposes of it.</summary>
    internal interface IProtokitePlaytestVideoEncoder : IDisposable
    {
        /// <summary>The pixel layout this encoder takes, which the capture converts each frame to.</summary>
        ProtokitePlaytestPixelFormat InputPixelFormat { get; }

        /// <summary>What encodes the video, for the log, once the first frame has been encoded; null before.</summary>
        string Description { get; }

        /// <summary>Takes the settings for frames of their size. False, with why, when they cannot be recorded. Any thread.</summary>
        bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error);

        /// <summary>Starts the encoder before its first frame, on the thread that will encode; false, with why. Encode starts it when this was not called.</summary>
        bool Start(out string error);

        /// <summary>Encodes one frame shown at <paramref name="timestampMs"/> for <paramref name="durationMs"/>; adds what came out, which can be nothing.</summary>
        bool Encode(byte[] pixels, long timestampMs, long durationMs, List<ProtokitePlaytestEncodedFrame> output, out string error);

        /// <summary>Hands over anything the encoder still holds. Nothing can be encoded afterwards.</summary>
        bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error);

        /// <summary>The game went to the background: hands over every frame it holds, and may let the platform's encoder go until the next frame.</summary>
        bool HandOverEverythingAndLetGo(List<ProtokitePlaytestEncodedFrame> output, out string error);
    }

    /// <summary>The size of the frames both sides of the encoder agree on.</summary>
    internal static class ProtokitePlaytestNv12
    {
        /// <summary>The bytes one tightly packed NV12 frame of this size takes.</summary>
        public static long FrameLength(int width, int height)
        {
            long chromaWidth = (width + 1) / 2;
            long chromaHeight = (height + 1) / 2;
            return (long)width * height + 2 * chromaWidth * chromaHeight;
        }
    }
}
