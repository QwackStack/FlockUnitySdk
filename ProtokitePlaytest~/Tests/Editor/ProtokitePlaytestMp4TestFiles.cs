using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Protokite.Playtest.Tests
{
    /// <summary>What an independent read of an MP4 file found; written apart from the writer so the two cannot share a bug.</summary>
    internal sealed class Mp4FileRead
    {
        public readonly List<string> TopLevelBoxes = new List<string>();
        public string MajorBrand;
        public readonly List<string> CompatibleBrands = new List<string>();
        public uint MovieTimescale;
        public uint TrackTimescale;
        public string SampleEntry;
        public int Width;
        public int Height;
        public byte[] DecoderSettings;
        public string ColourType;
        public int ColourPrimaries;
        public int ColourTransfer;
        public int ColourMatrix;
        public bool FullRange;
        public long DurationMs = -1;
        public uint DefaultFrameDurationMs;
        public long FileBytes;
        public readonly List<Mp4FrameRead> Frames = new List<Mp4FrameRead>();
    }

    internal sealed class Mp4FrameRead
    {
        public uint Sequence;
        public long TimestampMs;
        public long DurationMs;
        public bool IsKeyframe;
        public byte[] Bytes;
    }

    internal static class ProtokitePlaytestMp4TestFiles
    {
        public const int Width = 64;
        public const int Height = 36;
        public const long FrameMs = 33;
        public const long FrameDurationMs = 33;

        /// <summary>Sequence settings shaped like an encoder's: High profile (100), level 3.1, then bytes that are never zero.</summary>
        public static readonly byte[] SequenceSettings = { 0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9, 0x40, 0x50, 0x05, 0xBB, 0x01, 0x10 };
        public static readonly byte[] PictureSettings = { 0x68, 0xEB, 0xE3, 0xCB, 0x22, 0xC0 };

        /// <summary>The record a first frame carries, built from the two settings units above.</summary>
        public static byte[] DecoderSettings => ProtokitePlaytestH264.DecoderSettingsRecord(SequenceSettings, PictureSettings);

        // A stored frame: one unit after its four-byte length, a keyframe's picture or another frame's, then bytes that are never zero.
        public static byte[] MakeFrame(int frameIndex, int frameBytes, bool keyframe)
        {
            byte[] bytes = new byte[frameBytes];
            int unit = frameBytes - ProtokitePlaytestH264.LengthBytes;
            bytes[0] = (byte)(unit >> 24);
            bytes[1] = (byte)(unit >> 16);
            bytes[2] = (byte)(unit >> 8);
            bytes[3] = (byte)unit;
            bytes[4] = keyframe ? (byte)0x65 : (byte)0x41;
            for (int index = 5; index < frameBytes; index++)
                bytes[index] = (byte)(((frameIndex * 31 + index) & 0xFF) | 1);
            return bytes;
        }

        public static ProtokitePlaytestEncodedFrame MakeEncodedFrame(int frameIndex, int frameBytes) =>
            new ProtokitePlaytestEncodedFrame(MakeFrame(frameIndex, frameBytes, frameIndex == 0), frameIndex * FrameMs, frameIndex == 0, frameIndex == 0 ? DecoderSettings : null);

        // A file laid out the way a recording writes it, made by the writer itself so the fixture cannot drift from the format.
        public static byte[] MakeVideoBytes(string folder, int frames, int frameBytes, bool closed)
        {
            string scratch = Path.Combine(folder, "fixture-" + Guid.NewGuid().ToString("N") + ".mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            if (!file.Open(scratch, Width, Height, FrameDurationMs, out string error))
                throw new InvalidOperationException(error);
            for (int index = 0; index < frames; index++)
            {
                if (!file.WriteFrame(MakeEncodedFrame(index, frameBytes), out error))
                    throw new InvalidOperationException(error);
            }
            if (closed)
                file.Close(out _);
            else
                file.AbandonForTesting();
            byte[] bytes = File.ReadAllBytes(scratch);
            File.Delete(scratch);
            return bytes;
        }

        /// <summary>
        /// A frame's fragment header written out box by box, apart from the writer: what a recording cut off after it leaves. A
        /// claimed size other than the frame's stands in for a header whose sizes disagree; other flags for a layout this package never wrote.
        /// </summary>
        public static void AppendFragmentHeader(List<byte> bytes, uint sequence, long timestampMs, int frameBytes, bool keyframe,
            int claimedSampleBytes = -1, int claimedBoxBytes = -1, uint runFlags = 0x000701, uint? sampleFlags = null)
        {
            int sample = claimedSampleBytes < 0 ? frameBytes : claimedSampleBytes;
            AppendUInt32(bytes, 100);
            AppendType(bytes, "moof");
            AppendUInt32(bytes, 16);
            AppendType(bytes, "mfhd");
            AppendUInt32(bytes, 0);
            AppendUInt32(bytes, sequence);
            AppendUInt32(bytes, 76);
            AppendType(bytes, "traf");
            AppendUInt32(bytes, 16);
            AppendType(bytes, "tfhd");
            AppendUInt32(bytes, 0x00020000);
            AppendUInt32(bytes, 1);
            AppendUInt32(bytes, 20);
            AppendType(bytes, "tfdt");
            AppendUInt32(bytes, 0x01000000);
            AppendUInt32(bytes, (uint)(timestampMs >> 32));
            AppendUInt32(bytes, (uint)timestampMs);
            AppendUInt32(bytes, 32);
            AppendType(bytes, "trun");
            AppendUInt32(bytes, runFlags);
            AppendUInt32(bytes, 1);
            AppendUInt32(bytes, 108);
            AppendUInt32(bytes, (uint)FrameDurationMs);
            AppendUInt32(bytes, (uint)sample);
            AppendUInt32(bytes, sampleFlags ?? (keyframe ? 0x02000000u : 0x01010000u));
            AppendUInt32(bytes, (uint)(claimedBoxBytes < 0 ? 8 + frameBytes : claimedBoxBytes));
            AppendType(bytes, "mdat");
        }

        private static void AppendUInt32(List<byte> bytes, uint value)
        {
            bytes.Add((byte)(value >> 24));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        private static void AppendType(List<byte> bytes, string type) => bytes.AddRange(Encoding.ASCII.GetBytes(type));

        /// <summary>The offset each fragment starts at in a file, read from the top-level boxes alone.</summary>
        public static List<long> FragmentStarts(byte[] bytes)
        {
            List<long> starts = new List<long>();
            long position = 0;
            while (position + 8 <= bytes.Length)
            {
                long size = BigEndian(bytes, position, 4);
                if (size < 8)
                    break;
                if (Type(bytes, position + 4) == "moof")
                    starts.Add(position);
                position += size;
            }
            return starts;
        }

        public static bool Read(string path, out Mp4FileRead read)
        {
            read = new Mp4FileRead();
            byte[] bytes = File.ReadAllBytes(path);
            read.FileBytes = bytes.Length;
            Mp4FileRead result = read;
            Mp4FrameRead pending = null;
            bool wellFormed = Visit(bytes, 0, bytes.Length, (type, start, end) =>
            {
                result.TopLevelBoxes.Add(type);
                switch (type)
                {
                    case "ftyp":
                        result.MajorBrand = Type(bytes, start);
                        for (long brand = start + 8; brand + 4 <= end; brand += 4)
                            result.CompatibleBrands.Add(Type(bytes, brand));
                        break;
                    case "moov":
                        ReadMovie(bytes, result, start, end);
                        break;
                    case "moof":
                        pending = ReadFragment(bytes, start, end);
                        break;
                    case "mdat":
                        if (pending != null)
                        {
                            pending.Bytes = new byte[end - start];
                            Array.Copy(bytes, start, pending.Bytes, 0, pending.Bytes.Length);
                            result.Frames.Add(pending);
                            pending = null;
                        }
                        break;
                }
            });
            return wellFormed && result.MajorBrand != null;
        }

        private static void ReadMovie(byte[] bytes, Mp4FileRead read, long start, long end)
        {
            Visit(bytes, start, end, (type, childStart, childEnd) =>
            {
                if (type == "mvhd")
                    read.MovieTimescale = (uint)BigEndian(bytes, childStart + 12, 4);
                if (type == "trak")
                    ReadTrack(bytes, read, childStart, childEnd);
                if (type == "mvex")
                {
                    Visit(bytes, childStart, childEnd, (extendsType, extendsStart, extendsEnd) =>
                    {
                        if (extendsType == "mehd")
                            read.DurationMs = bytes[extendsStart] == 1 ? BigEndian(bytes, extendsStart + 4, 8) : BigEndian(bytes, extendsStart + 4, 4);
                        if (extendsType == "trex")
                            read.DefaultFrameDurationMs = (uint)BigEndian(bytes, extendsStart + 12, 4);
                    });
                }
            });
        }

        private static void ReadTrack(byte[] bytes, Mp4FileRead read, long start, long end)
        {
            Visit(bytes, start, end, (type, childStart, childEnd) =>
            {
                if (type == "mdia" || type == "minf" || type == "stbl")
                    ReadTrack(bytes, read, childStart, childEnd);
                if (type == "mdhd")
                    read.TrackTimescale = (uint)BigEndian(bytes, childStart + 12, 4);
                if (type != "stsd")
                    return;
                // Version and flags, the count, then the one sample entry.
                long entry = childStart + 8;
                read.SampleEntry = Type(bytes, entry + 4);
                read.Width = (int)BigEndian(bytes, entry + 8 + 24, 2);
                read.Height = (int)BigEndian(bytes, entry + 8 + 26, 2);
                long entryEnd = entry + BigEndian(bytes, entry, 4);
                Visit(bytes, entry + 8 + 78, entryEnd, (entryType, entryStart, entryStop) =>
                {
                    if (entryType == "avcC")
                    {
                        read.DecoderSettings = new byte[entryStop - entryStart];
                        Array.Copy(bytes, entryStart, read.DecoderSettings, 0, read.DecoderSettings.Length);
                    }
                    if (entryType == "colr")
                    {
                        read.ColourType = Type(bytes, entryStart);
                        read.ColourPrimaries = (int)BigEndian(bytes, entryStart + 4, 2);
                        read.ColourTransfer = (int)BigEndian(bytes, entryStart + 6, 2);
                        read.ColourMatrix = (int)BigEndian(bytes, entryStart + 8, 2);
                        read.FullRange = (bytes[entryStart + 10] & 0x80) != 0;
                    }
                });
            });
        }

        private static Mp4FrameRead ReadFragment(byte[] bytes, long start, long end)
        {
            Mp4FrameRead frame = new Mp4FrameRead();
            Visit(bytes, start, end, (type, childStart, childEnd) =>
            {
                if (type == "mfhd")
                    frame.Sequence = (uint)BigEndian(bytes, childStart + 4, 4);
                if (type != "traf")
                    return;
                Visit(bytes, childStart, childEnd, (trackType, trackStart, trackEnd) =>
                {
                    if (trackType == "tfdt")
                        frame.TimestampMs = bytes[trackStart] == 1 ? BigEndian(bytes, trackStart + 4, 8) : BigEndian(bytes, trackStart + 4, 4);
                    if (trackType == "trun")
                    {
                        // Flags 0x701: data offset, then each sample's duration, size and flags; one sample here.
                        frame.DurationMs = BigEndian(bytes, trackStart + 12, 4);
                        uint flags = (uint)BigEndian(bytes, trackStart + 20, 4);
                        frame.IsKeyframe = (flags & 0x00010000) == 0;
                    }
                });
            });
            return frame;
        }

        // Walks the boxes between start and end, handing each one's type and contents' range.
        private static bool Visit(byte[] bytes, long start, long end, Action<string, long, long> visit)
        {
            long position = start;
            while (position < end)
            {
                if (position + 8 > end)
                    return false;
                long size = BigEndian(bytes, position, 4);
                if (size < 8 || position + size > end)
                    return false;
                visit(Type(bytes, position + 4), position + 8, position + size);
                position += size;
            }
            return true;
        }

        private static string Type(byte[] bytes, long position) => Encoding.ASCII.GetString(bytes, (int)position, 4);

        private static long BigEndian(byte[] bytes, long position, int width)
        {
            long value = 0;
            for (int index = 0; index < width; index++)
                value = (value << 8) | bytes[position + index];
            return value;
        }
    }
}
