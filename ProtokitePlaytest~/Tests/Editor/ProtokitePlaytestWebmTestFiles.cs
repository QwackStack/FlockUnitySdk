using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Protokite.Playtest.Tests
{
    /// <summary>What an independent read of a WebM file found; written apart from the writer so the two cannot share a bug.</summary>
    internal sealed class WebmFileRead
    {
        public string DocType;
        public string Codec;
        public string MuxingApp;
        public int Width;
        public int Height;
        public ulong TimecodeScaleNanoseconds;
        public bool SegmentSizeWritten;
        public double DurationMs = -1;
        public long FileBytes;
        public readonly List<WebmFrameRead> Frames = new List<WebmFrameRead>();
    }

    internal sealed class WebmFrameRead
    {
        public long TimestampMs;
        public bool IsKeyframe;
        public byte[] Bytes;
    }

    internal static class ProtokitePlaytestWebmTestFiles
    {
        public const int Width = 64;
        public const int Height = 36;
        public const long FrameMs = 33;

        // One frame's bytes, starting the way the codec's frames start, then bytes different for each frame and never zero.
        public static byte[] MakeFrame(ProtokitePlaytestWebmFinisher.Codec codec, int frameIndex, int frameBytes, bool keyframe)
        {
            byte[] bytes = new byte[frameBytes];
            for (int index = 0; index < frameBytes; index++)
                bytes[index] = (byte)(((frameIndex * 31 + index) & 0xFF) | 1);
            if (codec == ProtokitePlaytestWebmFinisher.Codec.Vp9)
            {
                bytes[0] = 0x82;
                return bytes;
            }
            // VP8: key frame bit, version 0, shown, and a first part that fills the rest of the frame; a key frame's start code.
            int headerBytes = keyframe ? 10 : 3;
            int tag = (keyframe ? 0 : 1) | 0x10 | ((frameBytes - headerBytes) << 5);
            bytes[0] = (byte)tag;
            bytes[1] = (byte)(tag >> 8);
            bytes[2] = (byte)(tag >> 16);
            if (keyframe)
            {
                bytes[3] = 0x9D;
                bytes[4] = 0x01;
                bytes[5] = 0x2A;
            }
            return bytes;
        }

        // A file laid out the way earlier versions of the package wrote recordings, closed or left as a crash leaves it.
        public static byte[] MakeVideoBytes(string folder, ProtokitePlaytestWebmFinisher.Codec codec, int frames, int frameBytes, bool closed,
            string writtenBy = WebmTestFileWriter.WrittenBy)
        {
            string scratch = Path.Combine(folder, "fixture-" + Guid.NewGuid().ToString("N") + ".webm");
            WebmTestFileWriter file = new WebmTestFileWriter(scratch, codec, Width, Height, writtenBy);
            for (int index = 0; index < frames; index++)
                file.WriteFrame(MakeFrame(codec, index, frameBytes, index == 0), index * FrameMs, index == 0);
            if (closed)
                file.Close();
            else
                file.Abandon();
            byte[] bytes = File.ReadAllBytes(scratch);
            File.Delete(scratch);
            return bytes;
        }

        // A cluster header claiming a frame whose bytes never arrived: what a recording cut off mid-frame leaves.
        public static void AppendFrameHeader(List<byte> bytes, int frameIndex, int frameBytes, int blockFrameBytes = -1, byte track = 0x81)
        {
            bytes.AddRange(new byte[] { 0x1F, 0x43, 0xB6, 0x75 });
            AppendBigEndian(bytes, 0x10000000UL | (ulong)(15 + frameBytes), 4);
            bytes.Add(0xE7);
            bytes.Add(0x84);
            AppendBigEndian(bytes, (ulong)(frameIndex * FrameMs), 4);
            bytes.Add(0xA3);
            AppendBigEndian(bytes, 0x10000000UL | (ulong)(4 + (blockFrameBytes < 0 ? frameBytes : blockFrameBytes)), 4);
            bytes.AddRange(new byte[] { track, 0x00, 0x00, 0x00 });
        }

        private static void AppendBigEndian(List<byte> bytes, ulong value, int width)
        {
            for (int index = 0; index < width; index++)
                bytes.Add((byte)(value >> (8 * (width - 1 - index))));
        }

        public static bool Read(string path, out WebmFileRead read)
        {
            read = new WebmFileRead();
            byte[] bytes = File.ReadAllBytes(path);
            read.FileBytes = bytes.Length;
            WebmFileRead result = read;
            bool wellFormed = Visit(bytes, 0, bytes.Length, (id, start, end, unknownSize) =>
            {
                if (id == 0x1A45DFA3)
                {
                    Visit(bytes, start, end, (childId, childStart, childEnd, _) =>
                    {
                        if (childId == 0x4282) result.DocType = Encoding.ASCII.GetString(bytes, (int)childStart, (int)(childEnd - childStart));
                    });
                    return;
                }
                if (id != 0x18538067)
                    return;
                result.SegmentSizeWritten = !unknownSize;
                Visit(bytes, start, end, (segmentId, segmentStart, segmentEnd, _) => ReadSegmentChild(bytes, result, segmentId, segmentStart, segmentEnd));
            });
            return wellFormed && result.DocType != null;
        }

        private static void ReadSegmentChild(byte[] bytes, WebmFileRead read, ulong id, long start, long end)
        {
            if (id == 0x1549A966)
            {
                Visit(bytes, start, end, (infoId, infoStart, infoEnd, _) =>
                {
                    if (infoId == 0x2AD7B1) read.TimecodeScaleNanoseconds = BigEndian(bytes, infoStart, (int)(infoEnd - infoStart));
                    if (infoId == 0x4D80) read.MuxingApp = Encoding.ASCII.GetString(bytes, (int)infoStart, (int)(infoEnd - infoStart));
                    if (infoId == 0x4489 && infoEnd - infoStart == 8) read.DurationMs = BitConverter.Int64BitsToDouble((long)BigEndian(bytes, infoStart, 8));
                });
            }
            else if (id == 0x1654AE6B)
            {
                Visit(bytes, start, end, (_, entryStart, entryEnd, __) => Visit(bytes, entryStart, entryEnd, (trackId, trackStart, trackEnd, ___) =>
                {
                    if (trackId == 0x86) read.Codec = Encoding.ASCII.GetString(bytes, (int)trackStart, (int)(trackEnd - trackStart));
                    if (trackId == 0xE0)
                    {
                        Visit(bytes, trackStart, trackEnd, (videoId, videoStart, videoEnd, ____) =>
                        {
                            if (videoId == 0xB0) read.Width = (int)BigEndian(bytes, videoStart, (int)(videoEnd - videoStart));
                            if (videoId == 0xBA) read.Height = (int)BigEndian(bytes, videoStart, (int)(videoEnd - videoStart));
                        });
                    }
                }));
            }
            else if (id == 0x1F43B675)
            {
                long clusterTime = 0;
                Visit(bytes, start, end, (clusterId, clusterStart, clusterEnd, _) =>
                {
                    if (clusterId == 0xE7) clusterTime = (long)BigEndian(bytes, clusterStart, (int)(clusterEnd - clusterStart));
                    if (clusterId == 0xA3)
                    {
                        short relative = (short)((bytes[clusterStart + 1] << 8) | bytes[clusterStart + 2]);
                        byte[] frame = new byte[clusterEnd - clusterStart - 4];
                        Array.Copy(bytes, clusterStart + 4, frame, 0, frame.Length);
                        read.Frames.Add(new WebmFrameRead { TimestampMs = clusterTime + relative, IsKeyframe = (bytes[clusterStart + 3] & 0x80) != 0, Bytes = frame });
                    }
                });
            }
        }

        // Walks the elements between start and end, handing each one's id, data range and whether its size was left unknown.
        private static bool Visit(byte[] bytes, long start, long end, Action<ulong, long, long, bool> visit)
        {
            long position = start;
            while (position < end)
            {
                int idWidth = VintWidth(bytes[position]);
                if (idWidth == 0 || idWidth > 4 || position + idWidth >= end) return false;
                ulong id = BigEndian(bytes, position, idWidth);
                int sizeWidth = VintWidth(bytes[position + idWidth]);
                if (sizeWidth == 0 || position + idWidth + sizeWidth > end) return false;
                ulong raw = BigEndian(bytes, position + idWidth, sizeWidth);
                ulong valueBits = (1UL << (7 * sizeWidth)) - 1;
                bool unknownSize = (raw & valueBits) == valueBits;
                long dataStart = position + idWidth + sizeWidth;
                long dataEnd = unknownSize ? end : dataStart + (long)(raw & valueBits);
                if (dataEnd > end) return false;
                visit(id, dataStart, dataEnd, unknownSize);
                position = dataEnd;
            }
            return true;
        }

        private static int VintWidth(byte first)
        {
            for (int width = 1; width <= 8; width++)
            {
                if ((first & (0x80 >> (width - 1))) != 0) return width;
            }
            return 0;
        }

        private static ulong BigEndian(byte[] bytes, long position, int width)
        {
            ulong value = 0;
            for (int index = 0; index < width; index++) value = (value << 8) | bytes[position + index];
            return value;
        }
    }

    /// <summary>A file stream that fails part-way through one write, as a disk that fills up does.</summary>
    internal sealed class StreamThatFailsPartWay : FileStream
    {
        private int _writesBeforeFailing;
        private bool _failNextSetLength;

        public StreamThatFailsPartWay(string path, int writesBeforeFailing, bool cuttingFails = false) : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)
        {
            _writesBeforeFailing = writesBeforeFailing;
            _failNextSetLength = cuttingFails;
        }

        public override void SetLength(long value)
        {
            if (_failNextSetLength)
            {
                _failNextSetLength = false;
                throw new IOException("The disk is still busy.");
            }
            base.SetLength(value);
        }

        public override void Write(byte[] array, int offset, int count)
        {
            if (_writesBeforeFailing-- == 0)
            {
                base.Write(array, offset, count / 2);
                base.Flush();
                throw new IOException("There is not enough space on the disk.");
            }
            base.Write(array, offset, count);
        }
    }

    /// <summary>Writes WebM recordings laid out the way earlier versions of the package wrote them, for the finisher's tests to cut off and finish.</summary>
    internal sealed class WebmTestFileWriter
    {
        public const string WrittenBy = "ProtokitePlaytest";

        private readonly FileStream _stream;
        private readonly long _segmentSizeOffset;
        private readonly long _durationValueOffset;
        private long _lastTimestampMs;

        public long HeaderBytes { get; }
        public long BytesWritten { get; private set; }

        public WebmTestFileWriter(string path, ProtokitePlaytestWebmFinisher.Codec codec, int width, int height, string writtenBy = WrittenBy)
        {
            byte[] header = BuildHeader(width, height, codec == ProtokitePlaytestWebmFinisher.Codec.Vp9 ? "V_VP9" : "V_VP8", writtenBy,
                out _segmentSizeOffset, out _durationValueOffset);
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _stream.Write(header, 0, header.Length);
            HeaderBytes = header.Length;
            BytesWritten = header.Length;
        }

        // Each frame a cluster of its own: the cluster's size, its time, then a block on track 1 holding the frame.
        public void WriteFrame(byte[] frame, long timestampMs, bool keyframe)
        {
            List<byte> cluster = new List<byte> { 0x1F, 0x43, 0xB6, 0x75 };
            cluster.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)(ProtokitePlaytestWebmFinisher.FrameHeaderBytes - 8 + frame.Length), 4));
            cluster.Add(0xE7);
            cluster.Add(0x84);
            AppendBigEndian(cluster, (ulong)timestampMs, 4);
            cluster.Add(0xA3);
            cluster.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)(4 + frame.Length), 4));
            cluster.AddRange(new byte[] { 0x81, 0x00, 0x00, keyframe ? (byte)0x80 : (byte)0x00 });
            cluster.AddRange(frame);
            _stream.Write(cluster.ToArray(), 0, cluster.Count);
            _stream.Flush();
            BytesWritten += cluster.Count;
            _lastTimestampMs = timestampMs;
        }

        // What closing a recording stamped in: the segment's length and the last frame's time as the duration.
        public void Close()
        {
            _stream.Seek(_segmentSizeOffset, SeekOrigin.Begin);
            byte[] segmentSize = ProtokitePlaytestWebmFinisher.SizeBytes((ulong)(BytesWritten - (_segmentSizeOffset + 8)), 8);
            _stream.Write(segmentSize, 0, segmentSize.Length);
            _stream.Seek(_durationValueOffset, SeekOrigin.Begin);
            byte[] duration = ProtokitePlaytestWebmFinisher.DurationBytes(_lastTimestampMs);
            _stream.Write(duration, 0, duration.Length);
            _stream.Dispose();
        }

        // What a process that died part-way through leaves: nothing stamped.
        public void Abandon() => _stream.Dispose();

        private static byte[] BuildHeader(int width, int height, string codecName, string writtenBy, out long segmentSizeOffset, out long durationValueOffset)
        {
            List<byte> ebml = new List<byte>();
            AppendUnsigned(ebml, 0x4286, 1, 1);
            AppendUnsigned(ebml, 0x42F7, 1, 1);
            AppendUnsigned(ebml, 0x42F2, 4, 1);
            AppendUnsigned(ebml, 0x42F3, 8, 1);
            AppendText(ebml, 0x4282, "webm");
            AppendUnsigned(ebml, 0x4287, 2, 1);
            AppendUnsigned(ebml, 0x4285, 2, 1);

            List<byte> info = new List<byte>();
            AppendUnsigned(info, 0x2AD7B1, 1000000, 4);
            AppendText(info, 0x4D80, writtenBy);
            AppendText(info, 0x5741, writtenBy);
            AppendId(info, 0x4489);
            info.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes(8, 1));
            int durationInInfo = info.Count;
            info.AddRange(ProtokitePlaytestWebmFinisher.DurationBytes(0));

            List<byte> video = new List<byte>();
            AppendUnsigned(video, 0xB0, (ulong)width, 4);
            AppendUnsigned(video, 0xBA, (ulong)height, 4);

            List<byte> track = new List<byte>();
            AppendUnsigned(track, 0xD7, 1, 1);
            AppendUnsigned(track, 0x73C5, 1, 1);
            AppendUnsigned(track, 0x83, 1, 1);
            AppendText(track, 0x86, codecName);
            AppendId(track, 0xE0);
            track.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)video.Count, 1));
            track.AddRange(video);

            List<byte> header = new List<byte>();
            AppendId(header, 0x1A45DFA3);
            header.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)ebml.Count, 1));
            header.AddRange(ebml);
            AppendId(header, 0x18538067);
            segmentSizeOffset = header.Count;
            // "Size unknown", eight bytes wide so the real size fits later.
            header.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes(0x00FFFFFFFFFFFFFFUL, 8));
            AppendId(header, 0x1549A966);
            header.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)info.Count, 4));
            durationValueOffset = header.Count + durationInInfo;
            header.AddRange(info);
            AppendId(header, 0x1654AE6B);
            header.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)(track.Count + 5), 4));
            AppendId(header, 0xAE);
            header.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)track.Count, 4));
            header.AddRange(track);
            return header.ToArray();
        }

        private static void AppendId(List<byte> bytes, uint id)
        {
            if (id > 0x00FFFFFF) bytes.Add((byte)(id >> 24));
            if (id > 0x0000FFFF) bytes.Add((byte)(id >> 16));
            if (id > 0x000000FF) bytes.Add((byte)(id >> 8));
            bytes.Add((byte)id);
        }

        private static void AppendUnsigned(List<byte> bytes, uint id, ulong value, int width)
        {
            AppendId(bytes, id);
            bytes.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)width, 1));
            AppendBigEndian(bytes, value, width);
        }

        private static void AppendText(List<byte> bytes, uint id, string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text);
            AppendId(bytes, id);
            bytes.AddRange(ProtokitePlaytestWebmFinisher.SizeBytes((ulong)ascii.Length, 1));
            bytes.AddRange(ascii);
        }

        private static void AppendBigEndian(List<byte> bytes, ulong value, int width)
        {
            for (int index = 0; index < width; index++)
                bytes.Add((byte)(value >> (8 * (width - 1 - index))));
        }
    }

    /// <summary>A recording file that keeps the contract and writes a plain list, for code that records without a real container.</summary>
    internal sealed class FakeRecordingFile : IProtokitePlaytestRecordingFile
    {
        private string _path;
        private readonly List<string> _lines = new List<string>();

        public string ContentType => "application/x-protokite-test";
        public string FileExtension => ".frames";
        public int BytesAddedToEachFrame => 0;
        public long BytesFor(ProtokitePlaytestEncodedFrame frame) => frame.Data?.Length ?? 0;
        public long BytesWritten { get; private set; }
        public int FramesWritten { get; private set; }
        public long LastTimestampMs { get; private set; } = -1;

        public bool Open(string path, int width, int height, long frameDurationMs, out string error)
        {
            _path = path;
            _lines.Clear();
            FramesWritten = 0;
            LastTimestampMs = -1;
            BytesWritten = 0;
            error = null;
            return true;
        }

        public bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error)
        {
            error = _path == null ? "the file is not open." : frame.Data == null || frame.Data.Length == 0 ? "a frame has at least one byte."
                : frame.TimestampMs <= LastTimestampMs ? "a frame's time must be later than the last one." : null;
            if (error != null)
                return false;
            _lines.Add(frame.TimestampMs + " " + frame.Data.Length);
            BytesWritten += frame.Data.Length;
            FramesWritten++;
            LastTimestampMs = frame.TimestampMs;
            return true;
        }

        public bool Close(out string error)
        {
            error = null;
            if (_path != null)
                File.WriteAllLines(_path, _lines);
            _path = null;
            return true;
        }

        public ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error)
        {
            error = null;
            framesKept = File.Exists(unfinishedPath) ? File.ReadAllLines(unfinishedPath).Length : 0;
            if (framesKept == 0)
            {
                File.Delete(unfinishedPath);
                return ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame;
            }
            File.Move(unfinishedPath, finishedPath);
            return ProtokitePlaytestInterruptedRecordingResult.Finished;
        }

        public void Dispose() => Close(out _);
    }
}
