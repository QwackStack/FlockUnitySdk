#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static Protokite.Playtest.ProtokitePlaytestMediaFoundation;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// Reads a recording back through Windows' own MP4 reader and H.264 decoder (Media Foundation's source reader), so a test
    /// judges a file by what Windows makes of it: its frames, their times, its length and the decoded pictures, as NV12.
    /// </summary>
    public static class ProtokitePlaytestWindowsMp4Reader
    {
        public sealed class Result
        {
            public string Error;
            public int Width;
            public int Height;
            public long DurationMs = -1;
            public readonly List<long> TimesMs = new List<long>();
            public readonly List<byte[]> Pictures = new List<byte[]>();
        }

        private const uint FirstVideoStream = 0xfffffffc;
        private const uint MediaSource = 0xffffffff;
        private const uint EndOfStream = 0x2;
        private const uint StreamError = 0x1;
        private static readonly Guid EnableVideoProcessing = new Guid(0xfb394f3d, 0xccf1, 0x42ee, 0xbb, 0xb3, 0xf9, 0xb8, 0x45, 0xd5, 0x68, 0x1d);
        private static readonly Guid PresentationDuration = new Guid(0x6c990d33, 0xbb8e, 0x477a, 0x85, 0x98, 0x0d, 0x5d, 0x96, 0xfc, 0xd8, 0x8a);

        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url,
            IMFAttributes attributes, out IMFSourceReader reader);

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection();
            [PreserveSig] int SetStreamSelection();
            [PreserveSig] int GetNativeMediaType();
            [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition();
            [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample sample);
            [PreserveSig] int Flush();
            [PreserveSig] int GetServiceForStream();
            [PreserveSig] int GetPresentationAttribute(uint stream, [In] ref Guid key, IntPtr value);
        }

        /// <summary>Reads the file on a thread of its own, keeping each decoded picture whose index <paramref name="keep"/> accepts.</summary>
        public static Result Read(string path, Func<int, bool> keep = null)
        {
            Result result = null;
            Thread reading = new Thread(() => result = ReadOnThisThread(path, keep ?? (index => false))) { IsBackground = true, Name = "Test MP4 reader" };
            reading.Start();
            if (!reading.Join(TimeSpan.FromSeconds(120)))
                return new Result { Error = "Windows' reader did not finish within two minutes" };
            return result;
        }

        /// <summary>A pixel of a decoded NV12 picture as RGB, decoded with BT.709 in the limited range.</summary>
        public static int[] Rgb(byte[] nv12, int width, int height, int x, int y)
        {
            double luma = nv12[y * width + x] - 16;
            int chroma = width * height + (y / 2) * width + (x / 2) * 2;
            double blue = nv12[chroma] - 128;
            double red = nv12[chroma + 1] - 128;
            return new[]
            {
                Clamp(1.16438 * luma + 1.79274 * red),
                Clamp(1.16438 * luma - 0.21325 * blue - 0.53291 * red),
                Clamp(1.16438 * luma + 2.11240 * blue)
            };
        }

        private static int Clamp(double value) => (int)Math.Max(0, Math.Min(255, Math.Round(value)));

        private static Result ReadOnThisThread(string path, Func<int, bool> keep)
        {
            Result result = new Result();
            bool comStarted = ProtokitePlaytestWindowsVideoEncoder.StartCom();
            bool started = MFStartup(MediaFoundationVersion, StartupFull) >= 0;
            IMFAttributes attributes = null;
            IMFSourceReader reader = null;
            try
            {
                Check(MFCreateAttributes(out attributes, 1), "MFCreateAttributes");
                Guid key = EnableVideoProcessing;
                Check(attributes.SetUINT32(ref key, 1), "video processing");
                Check(MFCreateSourceReaderFromURL(path, attributes, out reader), "MFCreateSourceReaderFromURL");
                Check(MFCreateMediaType(out IMFMediaType wanted), "MFCreateMediaType");
                try
                {
                    key = MajorType;
                    Guid video = Video;
                    wanted.SetGUID(ref key, ref video);
                    key = Subtype;
                    Guid nv12 = Nv12;
                    wanted.SetGUID(ref key, ref nv12);
                    Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, wanted), "NV12 out of the decoder");
                }
                finally
                {
                    Release(wanted);
                }
                Check(reader.GetCurrentMediaType(FirstVideoStream, out IMFMediaType current), "GetCurrentMediaType");
                try
                {
                    key = FrameSize;
                    Check(current.GetUINT64(ref key, out ulong size), "frame size");
                    result.Width = (int)(size >> 32);
                    result.Height = (int)(size & 0xFFFFFFFF);
                }
                finally
                {
                    Release(current);
                }

                // A PROPVARIANT: its type, then a 64-bit length in 100 ns units eight bytes in.
                IntPtr value = Marshal.AllocHGlobal(24);
                try
                {
                    for (int offset = 0; offset < 24; offset += 4)
                        Marshal.WriteInt32(value, offset, 0);
                    key = PresentationDuration;
                    if (reader.GetPresentationAttribute(MediaSource, ref key, value) >= 0)
                        result.DurationMs = Marshal.ReadInt64(value, 8) / 10000;
                }
                finally
                {
                    Marshal.FreeHGlobal(value);
                }

                while (true)
                {
                    Check(reader.ReadSample(FirstVideoStream, 0, out uint _, out uint flags, out long timestamp, out IMFSample sample), "ReadSample");
                    if ((flags & StreamError) != 0)
                        throw new InvalidOperationException("the reader reported a stream error after frame " + result.TimesMs.Count);
                    if (sample != null)
                    {
                        try
                        {
                            result.Pictures.Add(keep(result.TimesMs.Count) ? Contents(sample) : null);
                            result.TimesMs.Add((timestamp + 5000) / 10000);
                        }
                        finally
                        {
                            Release(sample);
                        }
                    }
                    if ((flags & EndOfStream) != 0)
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Release(reader);
                Release(attributes);
                if (started)
                    MFShutdown();
                if (comStarted)
                    CoUninitialize();
            }
            return result;
        }

        private static byte[] Contents(IMFSample sample)
        {
            Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer");
            try
            {
                Check(buffer.Lock(out IntPtr memory, out uint _, out uint length), "Lock");
                byte[] bytes = new byte[length];
                Marshal.Copy(memory, bytes, 0, (int)length);
                buffer.Unlock();
                return bytes;
            }
            finally
            {
                Release(buffer);
            }
        }

        private static void Check(int result, string what)
        {
            if (result < 0)
                throw new InvalidOperationException($"{what} returned {Hex(result)}");
        }
    }
}
#endif
