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
        /// <summary>The flags a frame has when its fragment states none (the movie's defaults).</summary>
        public uint DefaultFrameFlags;
        public long FileBytes;
        public readonly List<Mp4FrameRead> Frames = new List<Mp4FrameRead>();
        /// <summary>Why the file is refused: a fragment that would send a player to bytes other than its frame's, or a layout this package never writes; null when neither.</summary>
        public string Problem;
    }

    internal sealed class Mp4FrameRead
    {
        public uint Sequence;
        public long TimestampMs;
        public long DurationMs;
        public bool IsKeyframe;
        public byte[] Bytes;
        /// <summary>The frame's size as its fragment states it, which is what a player reads.</summary>
        public long SampleBytes = -1;
        /// <summary>The byte of the file a player starts reading the frame at: its fragment's start plus the data offset it states.</summary>
        public long StartsAtByte = -1;
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

        // Shaped like a recording a phone's hardware encoder wrote (read off a Galaxy S23 Ultra's file): its settings, size and frames.
        public const int PhoneWidth = 1280;
        public const int PhoneHeight = 592;
        public const long PhoneFrameDurationMs = 67;
        public static readonly byte[] PhoneSequenceSettings = { 0x67, 0x64, 0x00, 0x1F, 0xAC, 0xB4, 0x02, 0x80, 0x25, 0xD3, 0x50, 0x10, 0x10, 0x10, 0x6D, 0x0A, 0x13, 0x50 };
        public static readonly byte[] PhonePictureSettings = { 0x68, 0xEE, 0x06, 0xF2, 0xC0 };
        public static byte[] PhoneDecoderSettings => ProtokitePlaytestH264.DecoderSettingsRecord(PhoneSequenceSettings, PhonePictureSettings);

        /// <summary>The phone's first eight frames as it recorded them, then a keyframe past what two bytes count (as a higher bitrate makes) and a frame after it.</summary>
        public static readonly (long TimestampMs, int Bytes, bool Keyframe)[] PhoneFrames =
        {
            (0, 27841, true), (76, 2947, false), (133, 6083, false), (201, 9108, false), (267, 11389, false), (334, 14236, false),
            (401, 12678, false), (467, 9175, false), (534, 70000, true), (600, 12318, false)
        };

        public static ProtokitePlaytestEncodedFrame MakePhoneFrame(int frameIndex)
        {
            (long timestampMs, int bytes, bool keyframe) = PhoneFrames[frameIndex];
            return new ProtokitePlaytestEncodedFrame(MakeFrame(frameIndex, bytes, keyframe), timestampMs, keyframe, frameIndex == 0 ? PhoneDecoderSettings : null);
        }

        // The phone's recording as the writer lays it out: closed, or as a process killed after its last frame leaves it.
        public static byte[] MakePhoneRecordingBytes(string folder, bool closed)
        {
            string scratch = Path.Combine(folder, "phone-" + Guid.NewGuid().ToString("N") + ".mp4");
            ProtokitePlaytestMp4File file = new ProtokitePlaytestMp4File();
            if (!file.Open(scratch, PhoneWidth, PhoneHeight, PhoneFrameDurationMs, out string error))
                throw new InvalidOperationException(error);
            for (int index = 0; index < PhoneFrames.Length; index++)
            {
                if (!file.WriteFrame(MakePhoneFrame(index), out error))
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
        /// claimed box size other than the frame's stands in for a header whose sizes disagree; other flags for a layout this package
        /// never wrote; another data offset for a fragment that sends a player to bytes other than its frame's.
        /// </summary>
        public static void AppendFragmentHeader(List<byte> bytes, uint sequence, long timestampMs, int frameBytes, bool keyframe,
            int claimedBoxBytes = -1, uint runFlags = 0x000701, uint? sampleFlags = null, uint dataOffset = 108)
        {
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
            AppendUInt32(bytes, dataOffset);
            AppendUInt32(bytes, (uint)FrameDurationMs);
            AppendUInt32(bytes, (uint)frameBytes);
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

        /// <summary>Where the first box of this type is named at or after a byte (its four letters, after its size), or -1.</summary>
        public static int FindBoxType(byte[] bytes, long from, string type) => Encoding.ASCII.GetString(bytes).IndexOf(type, (int)from, StringComparison.Ordinal);

        /// <summary>Adds to the four-byte number at a position, as a writer that got it wrong would have written it.</summary>
        public static void AddToNumber(byte[] bytes, int position, int amount)
        {
            uint value = (uint)BigEndian(bytes, position, 4);
            value = (uint)(value + amount);
            bytes[position] = (byte)(value >> 24);
            bytes[position + 1] = (byte)(value >> 16);
            bytes[position + 2] = (byte)(value >> 8);
            bytes[position + 3] = (byte)value;
        }

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
                        if (pending != null)
                            NoteProblem(result, $"fragment {result.Frames.Count + 1} has no data box after it");
                        pending = ReadFragment(bytes, start, end, result);
                        break;
                    case "mdat":
                        if (pending == null)
                        {
                            NoteProblem(result, $"a data box after fragment {result.Frames.Count} belongs to no fragment");
                            break;
                        }
                        pending.Bytes = new byte[end - start];
                        Array.Copy(bytes, start, pending.Bytes, 0, pending.Bytes.Length);
                        // A player finds the frame from the fragment's own fields, never from the data box around it.
                        if (pending.StartsAtByte != start)
                            NoteProblem(result, $"fragment {result.Frames.Count + 1} says its frame starts at byte {pending.StartsAtByte}, where its data box puts it at byte {start}");
                        if (pending.SampleBytes != pending.Bytes.Length)
                            NoteProblem(result, $"fragment {result.Frames.Count + 1} says its frame is {pending.SampleBytes} bytes, and its data box holds {pending.Bytes.Length}");
                        result.Frames.Add(pending);
                        pending = null;
                        break;
                }
            });
            if (pending != null)
                NoteProblem(result, $"fragment {result.Frames.Count + 1} has no data box after it");
            return wellFormed && result.MajorBrand != null && result.Problem == null;
        }

        private static void NoteProblem(Mp4FileRead read, string problem)
        {
            if (read.Problem == null)
                read.Problem = problem;
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
                        {
                            read.DefaultFrameDurationMs = (uint)BigEndian(bytes, extendsStart + 12, 4);
                            read.DefaultFrameFlags = (uint)BigEndian(bytes, extendsStart + 20, 4);
                        }
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

        // The run box's fields for its one frame, each four bytes when its flag is set: data offset, first frame's flags, duration, size, flags, time offset.
        private static readonly uint[] RunFieldFlags = { 0x000001, 0x000004, 0x000100, 0x000200, 0x000400, 0x000800 };

        // Reads a fragment the way a player does: each field is where the fragment's own flags say it is.
        private static Mp4FrameRead ReadFragment(byte[] bytes, long start, long end, Mp4FileRead read)
        {
            Mp4FrameRead frame = new Mp4FrameRead();
            int fragment = read.Frames.Count + 1;
            // With one track a fragment, its frame's position is counted from the fragment's start unless it names a base of its own.
            long fragmentStart = start - 8;
            Visit(bytes, start, end, (type, childStart, childEnd) =>
            {
                if (type == "mfhd")
                    frame.Sequence = (uint)BigEndian(bytes, childStart + 4, 4);
                if (type != "traf")
                    return;
                Visit(bytes, childStart, childEnd, (trackType, trackStart, trackEnd) =>
                {
                    uint flags = (uint)BigEndian(bytes, trackStart, 4) & 0xFFFFFF;
                    // A base or default flags of the fragment's own are never written here, so a fragment naming either is refused.
                    if (trackType == "tfhd" && (flags & 0x000021) != 0)
                        NoteProblem(read, $"fragment {fragment} names a base or default flags of its own, which this package never writes");
                    if (trackType == "tfdt")
                        frame.TimestampMs = bytes[trackStart] == 1 ? BigEndian(bytes, trackStart + 4, 8) : BigEndian(bytes, trackStart + 4, 4);
                    if (trackType != "trun")
                        return;
                    long stated = 8;
                    foreach (uint field in RunFieldFlags)
                        stated += (flags & field) != 0 ? 4 : 0;
                    if (trackEnd - trackStart < stated)
                    {
                        NoteProblem(read, $"fragment {fragment}'s run box is shorter than its flags say");
                        return;
                    }
                    long samples = BigEndian(bytes, trackStart + 4, 4);
                    if (samples != 1)
                        NoteProblem(read, $"fragment {fragment} holds {samples} frames, where this package writes one");
                    if ((flags & 0x000200) == 0 || (flags & 0x000001) == 0)
                        NoteProblem(read, $"fragment {fragment} does not say where its frame is or how big (run flags 0x{flags:X6})");
                    long position = trackStart + 8;
                    if ((flags & 0x000001) != 0)
                    {
                        frame.StartsAtByte = fragmentStart + BigEndian(bytes, position, 4);
                        position += 4;
                    }
                    uint sampleFlags = read.DefaultFrameFlags;
                    if ((flags & 0x000004) != 0)
                    {
                        sampleFlags = (uint)BigEndian(bytes, position, 4);
                        position += 4;
                    }
                    if ((flags & 0x000100) != 0)
                    {
                        frame.DurationMs = BigEndian(bytes, position, 4);
                        position += 4;
                    }
                    if ((flags & 0x000200) != 0)
                    {
                        frame.SampleBytes = BigEndian(bytes, position, 4);
                        position += 4;
                    }
                    if ((flags & 0x000400) != 0)
                        sampleFlags = (uint)BigEndian(bytes, position, 4);
                    frame.IsKeyframe = (sampleFlags & 0x00010000) == 0;
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
