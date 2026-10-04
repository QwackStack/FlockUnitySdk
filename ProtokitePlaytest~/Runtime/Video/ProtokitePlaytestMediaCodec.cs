using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>What one encoder hands back with a buffer: where its bytes are, when the frame shows, and what the buffer is.</summary>
    internal struct ProtokitePlaytestCodecBuffer
    {
        public int Offset;
        public int Size;
        public long PresentationTimeUs;
        public uint Flags;
    }

    /// <summary>Makes an Android codec for a recording, or null with why.</summary>
    internal delegate IProtokitePlaytestAndroidCodec ProtokitePlaytestMakeAndroidCodec(out string whyNot);

    /// <summary>What the Android encoder asks of the phone's codec, so tests can stand in for it. Every call on the recording's encoding thread.</summary>
    internal interface IProtokitePlaytestAndroidCodec : IDisposable
    {
        /// <summary>Configures it to encode H.264 from NV12 frames; 0 when it took the settings, the media library's status otherwise.</summary>
        int Configure(int width, int height, int framesPerSecond, int bitsPerSecond, int keyframeIntervalSeconds, bool constantBitrate);

        /// <summary>The input rows' stride and the rows the first plane takes, as the codec asks for them; 0 for either it does not say.</summary>
        void ReadInputLayout(out int stride, out int sliceHeight);

        int Start();

        /// <summary>A free input buffer's index, waiting up to the timeout; a negative status when none is free.</summary>
        long DequeueInputBuffer(long timeoutMicroseconds);

        IntPtr GetInputBuffer(long index, out long capacity);

        int QueueInputBuffer(long index, int size, long presentationTimeUs, uint flags);

        /// <summary>A finished output buffer's index, waiting up to the timeout; a negative status otherwise (try again, format changed, buffers changed).</summary>
        long DequeueOutputBuffer(long timeoutMicroseconds, out ProtokitePlaytestCodecBuffer buffer);

        IntPtr GetOutputBuffer(long index, out long capacity);

        void ReleaseOutputBuffer(long index);

        /// <summary>The stream's settings the output format carries (each after a start code), in order; empty when it carries none.</summary>
        List<byte[]> SettingsOnTheOutputFormat();

        void Stop();
    }

    /// <summary>Android's MediaCodec, called from C# through the system's own media library, and the phone's H.264 encoders, read through Java.</summary>
    internal sealed class ProtokitePlaytestMediaCodec : IProtokitePlaytestAndroidCodec
    {
        internal const string H264 = "video/avc";
        internal const long TryAgainLater = -1;
        internal const long OutputFormatChanged = -2;
        internal const long OutputBuffersChanged = -3;
        internal const uint KeyframeFlag = 1;
        internal const uint SettingsOnlyFlag = 2;
        internal const uint EndOfStreamFlag = 4;

        // MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar: NV12, the layout the capture makes.
        internal const int SemiPlanarYuv = 21;
        // MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR.
        private const int ConstantBitrateMode = 2;
        private const uint ConfigureAsEncoder = 1;
        private const string Library = "mediandk";

        private IntPtr _codec;

        private ProtokitePlaytestMediaCodec(IntPtr codec)
        {
            _codec = codec;
        }

        /// <summary>The encoder with this name, or null with why; a phone whose media library cannot be reached says so.</summary>
        public static IProtokitePlaytestAndroidCodec Create(string name, out string whyNot)
        {
            try
            {
                IntPtr codec = AMediaCodec_createCodecByName(name);
                whyNot = codec == IntPtr.Zero ? $"the phone would not create its encoder {name}." : null;
                return codec == IntPtr.Zero ? null : new ProtokitePlaytestMediaCodec(codec);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                whyNot = "Android's media library, which encodes the video, could not be reached on this phone: " + ex.Message;
                return null;
            }
        }

        public int Configure(int width, int height, int framesPerSecond, int bitsPerSecond, int keyframeIntervalSeconds, bool constantBitrate)
        {
            IntPtr format = AMediaFormat_new();
            try
            {
                AMediaFormat_setString(format, "mime", H264);
                AMediaFormat_setInt32(format, "width", width);
                AMediaFormat_setInt32(format, "height", height);
                AMediaFormat_setInt32(format, "color-format", SemiPlanarYuv);
                AMediaFormat_setInt32(format, "bitrate", bitsPerSecond);
                AMediaFormat_setInt32(format, "frame-rate", framesPerSecond);
                AMediaFormat_setInt32(format, "i-frame-interval", keyframeIntervalSeconds);
                // A variable bitrate came out 26% over on a busy scene, a constant one 1% (measured on a phone).
                if (constantBitrate)
                    AMediaFormat_setInt32(format, "bitrate-mode", ConstantBitrateMode);
                // Frames must come out in the order they went in: the file takes each later than the one before.
                AMediaFormat_setInt32(format, "max-bframes", 0);
                // What the capture makes (BT.709, limited range), written into the stream so a player reads the colours right.
                AMediaFormat_setInt32(format, "color-standard", 1);
                AMediaFormat_setInt32(format, "color-range", 2);
                AMediaFormat_setInt32(format, "color-transfer", 3);
                return AMediaCodec_configure(_codec, format, IntPtr.Zero, IntPtr.Zero, ConfigureAsEncoder);
            }
            finally
            {
                AMediaFormat_delete(format);
            }
        }

        public void ReadInputLayout(out int stride, out int sliceHeight)
        {
            stride = 0;
            sliceHeight = 0;
            IntPtr format;
            try
            {
                format = AMediaCodec_getInputFormat(_codec);
            }
            catch (EntryPointNotFoundException)
            {
                // Before Android 9 the codec does not say, and a packed frame is what it takes.
                return;
            }
            if (format == IntPtr.Zero)
                return;
            try
            {
                if (AMediaFormat_getInt32(format, "stride", out int rowStride))
                    stride = rowStride;
                if (AMediaFormat_getInt32(format, "slice-height", out int rows))
                    sliceHeight = rows;
            }
            finally
            {
                AMediaFormat_delete(format);
            }
        }

        public int Start() => AMediaCodec_start(_codec);

        public long DequeueInputBuffer(long timeoutMicroseconds) => AMediaCodec_dequeueInputBuffer(_codec, timeoutMicroseconds).ToInt64();

        public IntPtr GetInputBuffer(long index, out long capacity)
        {
            IntPtr buffer = AMediaCodec_getInputBuffer(_codec, (UIntPtr)(ulong)index, out UIntPtr size);
            capacity = (long)size.ToUInt64();
            return buffer;
        }

        public int QueueInputBuffer(long index, int size, long presentationTimeUs, uint flags)
            => AMediaCodec_queueInputBuffer(_codec, (UIntPtr)(ulong)index, IntPtr.Zero, (UIntPtr)(uint)size, (ulong)Math.Max(0L, presentationTimeUs), flags);

        public long DequeueOutputBuffer(long timeoutMicroseconds, out ProtokitePlaytestCodecBuffer buffer)
        {
            long index = AMediaCodec_dequeueOutputBuffer(_codec, out BufferInfo info, timeoutMicroseconds).ToInt64();
            buffer = new ProtokitePlaytestCodecBuffer { Offset = info.Offset, Size = info.Size, PresentationTimeUs = info.PresentationTimeUs, Flags = info.Flags };
            return index;
        }

        public IntPtr GetOutputBuffer(long index, out long capacity)
        {
            IntPtr buffer = AMediaCodec_getOutputBuffer(_codec, (UIntPtr)(ulong)index, out UIntPtr size);
            capacity = (long)size.ToUInt64();
            return buffer;
        }

        public void ReleaseOutputBuffer(long index) => AMediaCodec_releaseOutputBuffer(_codec, (UIntPtr)(ulong)index, false);

        public List<byte[]> SettingsOnTheOutputFormat()
        {
            List<byte[]> settings = new List<byte[]>();
            IntPtr format = AMediaCodec_getOutputFormat(_codec);
            if (format == IntPtr.Zero)
                return settings;
            try
            {
                foreach (string key in new[] { "csd-0", "csd-1" })
                {
                    if (!AMediaFormat_getBuffer(format, key, out IntPtr data, out UIntPtr size) || data == IntPtr.Zero)
                        continue;
                    byte[] bytes = new byte[(int)size.ToUInt64()];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    settings.Add(bytes);
                }
            }
            finally
            {
                AMediaFormat_delete(format);
            }
            return settings;
        }

        public void Stop() => AMediaCodec_stop(_codec);

        public void Dispose()
        {
            if (_codec == IntPtr.Zero)
                return;
            AMediaCodec_delete(_codec);
            _codec = IntPtr.Zero;
        }

        /// <summary>The phone's H.264 encoders in Android's order, or null with why; on a thread of its own, attached to Java (37 ms measured).</summary>
        // A thread not attached to Java fails every call (measured), with a misleading "Field SDK_INT not found".
        public static List<ProtokitePlaytestEncoderFound> H264EncodersOnThisThread(out string whyNone)
        {
            int attached = AndroidJNI.AttachCurrentThread();
            if (attached != 0)
            {
                whyNone = $"Android would not let the playtest ask which video encoders this phone has (attaching to Java answered {attached}).";
                return null;
            }
            try
            {
                return H264Encoders(out whyNone);
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
        }

        private static List<ProtokitePlaytestEncoderFound> H264Encoders(out string whyNone)
        {
            int api;
            using (AndroidJavaClass version = new AndroidJavaClass("android.os.Build$VERSION"))
                api = version.GetStatic<int>("SDK_INT");
            List<ProtokitePlaytestEncoderFound> found = new List<ProtokitePlaytestEncoderFound>();
            // MediaCodecList.REGULAR_CODECS: the codecs a game may use.
            using (AndroidJavaObject list = new AndroidJavaObject("android.media.MediaCodecList", 0))
            {
                foreach (AndroidJavaObject info in list.Call<AndroidJavaObject[]>("getCodecInfos"))
                {
                    using (info)
                    {
                        // A codec Java cannot describe is left out, so one odd entry never costs the phone its other encoders.
                        try
                        {
                            if (info.Call<bool>("isEncoder") && TakesH264(info.Call<string[]>("getSupportedTypes")))
                                found.Add(Describe(info, api));
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            whyNone = null;
            return found;
        }

        private static bool TakesH264(string[] types)
        {
            foreach (string type in types)
            {
                if (string.Equals(type, H264, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static ProtokitePlaytestEncoderFound Describe(AndroidJavaObject info, int api)
        {
            string name = info.Call<string>("getName");
            ProtokitePlaytestEncoderFound encoder = new ProtokitePlaytestEncoderFound
            {
                Name = name,
                OnAPhone = true,
                // Before Android 10 the list does not say; Android's own software encoders are known by name.
                InHardware = api >= 29 ? info.Call<bool>("isHardwareAccelerated")
                    : !name.StartsWith("OMX.google.", StringComparison.Ordinal) && !name.StartsWith("c2.android.", StringComparison.Ordinal)
            };
            using (AndroidJavaObject capabilities = info.Call<AndroidJavaObject>("getCapabilitiesForType", H264))
            {
                encoder.TakesNv12 = Array.IndexOf(capabilities.Get<int[]>("colorFormats"), SemiPlanarYuv) >= 0;
                using (AndroidJavaObject encoderCapabilities = capabilities.Call<AndroidJavaObject>("getEncoderCapabilities"))
                    encoder.TakesConstantBitrate = encoderCapabilities != null && encoderCapabilities.Call<bool>("isBitrateModeSupported", ConstantBitrateMode);
                // Taken last, so nothing above can fail with it held: kept for the recording's start, on the main thread (0.1 ms a question, measured).
                AndroidJavaObject video = capabilities.Call<AndroidJavaObject>("getVideoCapabilities");
                if (video != null)
                {
                    encoder.TakesSizeAndRate = (width, height, framesPerSecond) => TakesSizeAndRate(video, width, height, framesPerSecond);
                    encoder.HeldFromThePlatform = video;
                }
            }
            return encoder;
        }

        // A question Java cannot answer counts as a yes: the encoder's own configure is the last word, and says why.
        private static bool TakesSizeAndRate(AndroidJavaObject video, int width, int height, int framesPerSecond)
        {
            try
            {
                return video.Call<bool>("areSizeAndRateSupported", width, height, (double)framesPerSecond);
            }
            catch (Exception)
            {
                return true;
            }
        }

        // AMediaCodecBufferInfo.
        [StructLayout(LayoutKind.Sequential)]
        private struct BufferInfo
        {
            public int Offset;
            public int Size;
            public long PresentationTimeUs;
            public uint Flags;
        }

        [DllImport(Library)] private static extern IntPtr AMediaCodec_createCodecByName(string name);
        [DllImport(Library)] private static extern int AMediaCodec_delete(IntPtr codec);
        [DllImport(Library)] private static extern int AMediaCodec_configure(IntPtr codec, IntPtr format, IntPtr surface, IntPtr crypto, uint flags);
        [DllImport(Library)] private static extern int AMediaCodec_start(IntPtr codec);
        [DllImport(Library)] private static extern int AMediaCodec_stop(IntPtr codec);
        [DllImport(Library)] private static extern IntPtr AMediaCodec_dequeueInputBuffer(IntPtr codec, long timeoutUs);
        [DllImport(Library)] private static extern IntPtr AMediaCodec_getInputBuffer(IntPtr codec, UIntPtr index, out UIntPtr size);
        [DllImport(Library)] private static extern int AMediaCodec_queueInputBuffer(IntPtr codec, UIntPtr index, IntPtr offset, UIntPtr size, ulong timeUs, uint flags);
        [DllImport(Library)] private static extern IntPtr AMediaCodec_dequeueOutputBuffer(IntPtr codec, out BufferInfo info, long timeoutUs);
        [DllImport(Library)] private static extern IntPtr AMediaCodec_getOutputBuffer(IntPtr codec, UIntPtr index, out UIntPtr size);
        [DllImport(Library)] private static extern int AMediaCodec_releaseOutputBuffer(IntPtr codec, UIntPtr index, [MarshalAs(UnmanagedType.I1)] bool render);
        [DllImport(Library)] private static extern IntPtr AMediaCodec_getOutputFormat(IntPtr codec);
        [DllImport(Library)] private static extern IntPtr AMediaCodec_getInputFormat(IntPtr codec);
        [DllImport(Library)] private static extern IntPtr AMediaFormat_new();
        [DllImport(Library)] private static extern int AMediaFormat_delete(IntPtr format);
        [DllImport(Library)] private static extern void AMediaFormat_setInt32(IntPtr format, string name, int value);
        [DllImport(Library)] private static extern void AMediaFormat_setString(IntPtr format, string name, string value);
        [DllImport(Library)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AMediaFormat_getInt32(IntPtr format, string name, out int value);
        [DllImport(Library)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AMediaFormat_getBuffer(IntPtr format, string name, out IntPtr data, out UIntPtr size);
    }
}
