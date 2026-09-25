using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>The one place that knows libvpx (protokite_vpx.dll, 64-bit Windows); every other platform reports no video.</summary>
    internal static class ProtokitePlaytestLibVpx
    {
        /// <summary>The wrapper this code was written against; a DLL with another version is refused rather than called.</summary>
        internal const int WrapperVersion = 1;

        private const string LogPrefix = "[Protokite Playtest] ";
        private const string OnlyOn64BitWindows = "video is recorded on 64-bit Windows only.";
        private static string _whyNoVideo;
        private static bool _checked;
        private static bool _reportedNoVideo;
        private static readonly object CheckLock = new object();

        /// <summary>Stands in for reading the DLL's version, so a test can act out a missing or stale DLL.</summary>
        internal static Func<int> ReadWrapperVersionForTesting;

        /// <summary>Stands in for the process's architecture, so a test can act out a 32-bit or ARM64 Windows build.</summary>
        internal static Func<Architecture> ReadProcessArchitectureForTesting;

        /// <summary>Whether this build and this process should have video: built for Windows, and not running as 32-bit or ARM.</summary>
        internal static bool ShouldHaveVideo
        {
            get
            {
                if (!IsBuiltWithVideo)
                    return false;
                Architecture architecture;
                try
                {
                    architecture = ReadProcessArchitectureForTesting != null ? ReadProcessArchitectureForTesting()
                        : IntPtr.Size == 8 ? RuntimeInformation.ProcessArchitecture : Architecture.X86;
                }
                catch (Exception)
                {
                    return true;
                }
                // Only a known other architecture is turned away, so an unexpected answer still tries the DLL.
                return architecture != Architecture.X86 && architecture != Architecture.Arm && architecture != Architecture.Arm64;
            }
        }

        /// <summary>Whether this build can encode video; checked once, then remembered.</summary>
        internal static bool IsAvailable(out string whyNot)
        {
            lock (CheckLock)
            {
                if (!_checked)
                {
                    _whyNoVideo = CheckTheLibrary();
                    _checked = true;
                }
                whyNot = _whyNoVideo;
                return _whyNoVideo == null;
            }
        }

        /// <summary>An encoder, or null with why when this build has none. The first "no video" of a launch is logged.</summary>
        internal static IProtokitePlaytestVideoEncoder CreateEncoder(out string whyNot)
        {
            if (IsAvailable(out whyNot))
                return new Encoder();
            lock (CheckLock)
            {
                if (_reportedNoVideo)
                    return null;
                _reportedNoVideo = true;
            }
            string line = LogPrefix + "This build records no playtest video: " + whyNot + " Everything else in the playtest still runs.";
            // Expected where video is not built at all; a 64-bit Windows build that should have it says so louder.
            if (ShouldHaveVideo)
                Debug.LogWarning(line);
            else
                Debug.Log(line);
            return null;
        }

        /// <summary>Forgets what was checked and reported, as a new launch does; statics outlive Play Mode with domain reload off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForNewLaunch()
        {
            lock (CheckLock)
            {
                _checked = false;
                _whyNoVideo = null;
                _reportedNoVideo = false;
            }
        }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        internal static readonly bool IsBuiltWithVideo = true;

        private static string CheckTheLibrary()
        {
            // A 32-bit or ARM64 Windows build leaves the DLL out on purpose; that is not a missing DLL.
            if (!ShouldHaveVideo)
                return OnlyOn64BitWindows;
            int version;
            try
            {
                version = ReadWrapperVersionForTesting != null ? ReadWrapperVersionForTesting() : Native.pk_vpx_wrapper_version();
            }
            catch (DllNotFoundException)
            {
                return "protokite_vpx.dll is missing from the build.";
            }
            catch (BadImageFormatException)
            {
                return "protokite_vpx.dll is for 64-bit Windows, and this is not a 64-bit build.";
            }
            catch (EntryPointNotFoundException)
            {
                return "protokite_vpx.dll is not one this package can use.";
            }
            return version == WrapperVersion
                ? null
                : $"protokite_vpx.dll is version {version}, and this package needs version {WrapperVersion}.";
        }

        /// <summary>The libvpx release inside the DLL.</summary>
        internal static string LibraryVersion => Marshal.PtrToStringAnsi(Native.pk_vpx_version());

        /// <summary>Whether the DLL can encode this codec.</summary>
        internal static bool HasCodec(ProtokitePlaytestVideoCodec codec) => Native.pk_vpx_has_codec((int)codec) == 1;

        private static string ReadError(byte[] buffer)
        {
            int length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }

        private static string ReadError(IntPtr text) => Marshal.PtrToStringAnsi(text) ?? "";

        /// <summary>libvpx: one pass, no frames held back, a steady bitrate, timestamps in milliseconds.</summary>
        private sealed class Encoder : IProtokitePlaytestVideoEncoder
        {
            private IntPtr _encoder;
            private bool _finished;

            public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error)
            {
                if (_encoder != IntPtr.Zero)
                {
                    error = "The encoder is already configured.";
                    return false;
                }
                byte[] why = new byte[256];
                long keyframeInterval = (long)settings.KeyframeIntervalSeconds * settings.FramesPerSecond;
                _encoder = Native.pk_vpx_encoder_create((int)settings.Codec, settings.Width, settings.Height, settings.FramesPerSecond,
                    settings.BitrateKbps, settings.Threads, (int)Math.Min(keyframeInterval, int.MaxValue), settings.SpeedToUse, why, why.Length);
                error = _encoder == IntPtr.Zero ? $"The {settings.Codec} encoder could not start: {ReadError(why)}." : null;
                return _encoder != IntPtr.Zero;
            }

            public bool Encode(byte[] i420, long timestampMs, long durationMs, bool forceKeyframe, List<ProtokitePlaytestEncodedFrame> output, out string error)
            {
                if (!Usable(out error))
                    return false;
                if (Native.pk_vpx_encode_i420(_encoder, i420, i420?.LongLength ?? 0, timestampMs, durationMs, forceKeyframe ? 1 : 0) != 0)
                {
                    error = ReadError(Native.pk_vpx_encoder_error(_encoder));
                    return false;
                }
                TakePackets(output);
                return true;
            }

            public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error)
            {
                if (!Usable(out error))
                    return false;
                _finished = true;
                if (Native.pk_vpx_flush(_encoder) != 0)
                {
                    error = ReadError(Native.pk_vpx_encoder_error(_encoder));
                    return false;
                }
                TakePackets(output);
                return true;
            }

            public void Dispose()
            {
                if (_encoder != IntPtr.Zero)
                    Native.pk_vpx_encoder_destroy(_encoder);
                _encoder = IntPtr.Zero;
                _finished = true;
                GC.SuppressFinalize(this);
            }

            ~Encoder()
            {
                if (_encoder != IntPtr.Zero)
                    Native.pk_vpx_encoder_destroy(_encoder);
            }

            private bool Usable(out string error)
            {
                error = _encoder == IntPtr.Zero ? "The encoder is not configured." : _finished ? "The encoder has finished." : null;
                return error == null;
            }

            // The packet's bytes belong to libvpx until the next call, so each is copied out.
            private void TakePackets(List<ProtokitePlaytestEncodedFrame> output)
            {
                while (Native.pk_vpx_next_packet(_encoder, out IntPtr data, out int size, out long timestampMs, out int isKeyframe) == 1)
                {
                    byte[] copy = new byte[size];
                    Marshal.Copy(data, copy, 0, size);
                    output.Add(new ProtokitePlaytestEncodedFrame(copy, timestampMs, isKeyframe == 1));
                }
            }
        }

        /// <summary>Decodes what the encoder wrote, so a recording can be checked frame for frame.</summary>
        internal sealed class Decoder : IDisposable
        {
            private IntPtr _decoder;

            public Decoder(ProtokitePlaytestVideoCodec codec)
            {
                _decoder = Native.pk_vpx_decoder_create((int)codec);
                if (_decoder == IntPtr.Zero)
                    throw new InvalidOperationException($"protokite_vpx.dll cannot decode {codec}.");
            }

            /// <summary>Decodes one frame into a new tightly packed I420 buffer, or null with why.</summary>
            public byte[] Decode(byte[] frame, int width, int height, out string error)
            {
                byte[] i420 = new byte[ProtokitePlaytestI420.FrameLength(width, height)];
                if (Native.pk_vpx_decode_to_i420(_decoder, frame, frame.Length, i420, i420.LongLength, width, height) != 0)
                {
                    error = ReadError(Native.pk_vpx_decoder_error(_decoder));
                    return null;
                }
                error = null;
                return i420;
            }

            public void Dispose()
            {
                if (_decoder != IntPtr.Zero)
                    Native.pk_vpx_decoder_destroy(_decoder);
                _decoder = IntPtr.Zero;
            }
        }

        // Strings come back as IntPtr: a string return would make the marshaller free memory the DLL owns.
        private static class Native
        {
            private const string Library = "protokite_vpx";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pk_vpx_wrapper_version();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr pk_vpx_version();
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pk_vpx_has_codec(int codec);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr pk_vpx_encoder_create(int codec, int width, int height, int fps,
                int bitrateKbps, int threads, int keyframeInterval, int speed, byte[] error, int errorSize);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pk_vpx_encode_i420(IntPtr encoder, byte[] i420, long i420Length,
                long timestampMs, long durationMs, int forceKeyframe);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pk_vpx_flush(IntPtr encoder);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pk_vpx_next_packet(IntPtr encoder, out IntPtr data, out int size,
                out long timestampMs, out int isKeyframe);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr pk_vpx_encoder_error(IntPtr encoder);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void pk_vpx_encoder_destroy(IntPtr encoder);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr pk_vpx_decoder_create(int codec);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int pk_vpx_decode_to_i420(IntPtr decoder, byte[] data, int size,
                byte[] outI420, long outLength, int width, int height);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr pk_vpx_decoder_error(IntPtr decoder);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void pk_vpx_decoder_destroy(IntPtr decoder);
        }
#else
        internal static readonly bool IsBuiltWithVideo = false;

        private static string CheckTheLibrary() => OnlyOn64BitWindows;

        /// <summary>Never built here: nothing outside Windows calls it.</summary>
        private sealed class Encoder : IProtokitePlaytestVideoEncoder
        {
            public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error) { error = CheckTheLibrary(); return false; }
            public bool Encode(byte[] i420, long timestampMs, long durationMs, bool forceKeyframe, List<ProtokitePlaytestEncodedFrame> output, out string error) { error = CheckTheLibrary(); return false; }
            public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error) { error = CheckTheLibrary(); return false; }
            public void Dispose() { }
        }
#endif
    }
}
