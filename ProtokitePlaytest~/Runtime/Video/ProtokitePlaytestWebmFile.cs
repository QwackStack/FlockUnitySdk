using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Protokite.Playtest
{
    /// <summary>
    /// Writes VP8 or VP9 frames into a WebM file, which a browser plays with nothing installed. Every element's size is known
    /// before it is written and each frame is a cluster of its own, so a file cut off anywhere still plays up to the cut; only
    /// the length and the duration are stamped in on close. Not thread-safe: one thread at a time.
    /// </summary>
    internal sealed class ProtokitePlaytestWebmFile : IProtokitePlaytestRecordingFile
    {
        /// <summary>The name written into every file as the program that made it.</summary>
        internal const string WrittenBy = "ProtokitePlaytest";
        /// <summary>What each frame costs on top of its own bytes: a cluster around it, its time and the block header.</summary>
        internal const int FrameHeaderBytes = 23;
        /// <summary>The largest frame one cluster's four-byte size can hold; all ones in that field means "size unknown".</summary>
        internal const int MaxFrameBytes = 0x0FFFFFFE - (FrameHeaderBytes - 8);
        /// <summary>A cluster's time is four bytes of milliseconds.</summary>
        internal const long MaxTimestampMs = uint.MaxValue;

        private const uint IdEbmlHeader = 0x1A45DFA3;
        private const uint IdEbmlVersion = 0x4286;
        private const uint IdEbmlReadVersion = 0x42F7;
        private const uint IdEbmlMaxIdLength = 0x42F2;
        private const uint IdEbmlMaxSizeLength = 0x42F3;
        private const uint IdDocType = 0x4282;
        private const uint IdDocTypeVersion = 0x4287;
        private const uint IdDocTypeReadVersion = 0x4285;
        private const uint IdSegment = 0x18538067;
        private const uint IdInfo = 0x1549A966;
        private const uint IdTimecodeScale = 0x2AD7B1;
        private const uint IdMuxingApp = 0x4D80;
        private const uint IdWritingApp = 0x5741;
        private const uint IdDuration = 0x4489;
        private const uint IdTracks = 0x1654AE6B;
        private const uint IdTrackEntry = 0xAE;
        private const uint IdTrackNumber = 0xD7;
        private const uint IdTrackUid = 0x73C5;
        private const uint IdTrackType = 0x83;
        private const uint IdCodecId = 0x86;
        private const uint IdVideo = 0xE0;
        private const uint IdPixelWidth = 0xB0;
        private const uint IdPixelHeight = 0xBA;
        private const uint IdCluster = 0x1F43B675;
        private const uint IdTimecode = 0xE7;
        private const uint IdSimpleBlock = 0xA3;

        private const string CodecNameVp8 = "V_VP8";
        private const string CodecNameVp9 = "V_VP9";
        private const ulong TimecodeScaleNanoseconds = 1000000;
        private const int SegmentSizeWidth = 8;
        private const ulong UnknownSegmentSize = 0x00FFFFFFFFFFFFFFUL;
        private const int HeaderReadLimit = 64 * 1024;
        // A VP8 key frame's first six bytes say everything the frame check needs.
        private const int FrameStartBytesChecked = 6;

        private readonly string _writtenBy;
        private readonly Func<string, Stream> _openForWriting;
        private Stream _stream;
        private ProtokitePlaytestVideoCodec _codec;
        private long _segmentSizeOffset;
        private long _durationValueOffset;
        private string _writeFailure;

        public string ContentType => "video/webm";
        public string FileExtension => ".webm";
        public int BytesAddedToEachFrame => FrameHeaderBytes;
        public long BytesWritten { get; private set; }
        public int FramesWritten { get; private set; }
        public long LastTimestampMs { get; private set; } = -1;

        /// <summary>Bytes before the first frame, which depend on the name the file is written by.</summary>
        internal long HeaderBytes { get; private set; }

        public ProtokitePlaytestWebmFile() : this(WrittenBy, null)
        {
        }

        // Another name moves every offset behind it, and another stream stands in for a disk that fails; tests use both.
        internal ProtokitePlaytestWebmFile(string writtenBy, Func<string, Stream> openForWriting)
        {
            _writtenBy = writtenBy;
            // Unbuffered: each frame reaches the operating system as it is written, so a process that dies loses at most the
            // frame in progress, and a write the disk refuses fails at that write rather than in a later flush.
            _openForWriting = openForWriting ?? (path => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1));
        }

        public bool Open(string path, ProtokitePlaytestVideoCodec codec, int width, int height, out string error)
        {
            Close(out _);
            BytesWritten = 0;
            FramesWritten = 0;
            LastTimestampMs = -1;
            _writeFailure = null;

            string codecName = CodecName(codec);
            if (codecName == null)
            {
                error = $"a WebM recording holds VP8 or VP9 video, not codec {(int)codec}.";
                return false;
            }
            if (width < 2 || height < 2 || width > 65535 || height > 65535)
            {
                error = $"a {width}x{height} video cannot be recorded.";
                return false;
            }

            byte[] header = BuildHeader(width, height, codecName, _writtenBy, out _segmentSizeOffset, out _durationValueOffset);
            try
            {
                _stream = _openForWriting(path);
                _stream.Write(header, 0, header.Length);
                _stream.Flush();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                DisposeStream();
                error = $"the file {path} could not be written: {exception.Message}";
                return false;
            }

            _codec = codec;
            HeaderBytes = header.Length;
            BytesWritten = header.Length;
            error = null;
            return true;
        }

        public bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error)
        {
            byte[] bytes = frame.Data;
            error = _stream == null ? "the file is not open."
                : _writeFailure != null ? "an earlier write failed, so the file takes no more frames: " + _writeFailure
                : bytes == null || bytes.Length == 0 ? "a frame has at least one byte."
                : bytes.Length > MaxFrameBytes ? $"a frame of {bytes.Length} bytes is larger than one WebM cluster holds."
                : frame.TimestampMs < 0 || frame.TimestampMs > MaxTimestampMs ? $"a frame's time must be 0 to {MaxTimestampMs} ms, not {frame.TimestampMs}."
                : frame.TimestampMs <= LastTimestampMs ? $"a frame at {frame.TimestampMs} ms is not later than the last one, at {LastTimestampMs} ms."
                // Finishing a cut-off file stops at a frame that fails this check and loses every frame after it.
                : !LooksLikeAFrame(_codec, bytes, 0, Math.Min(bytes.Length, FrameStartBytesChecked), bytes.Length) ? $"the frame does not start the way a {_codec} frame does."
                : null;
            if (error != null)
            {
                return false;
            }

            byte[] cluster = new byte[FrameHeaderBytes];
            int position = 0;
            WriteId(cluster, ref position, IdCluster);
            WriteSize(cluster, ref position, (ulong)(FrameHeaderBytes - 8 + bytes.Length), 4);
            WriteId(cluster, ref position, IdTimecode);
            WriteSize(cluster, ref position, 4, 1);
            WriteBigEndian(cluster, ref position, (ulong)frame.TimestampMs, 4);
            WriteId(cluster, ref position, IdSimpleBlock);
            WriteSize(cluster, ref position, (ulong)(4 + bytes.Length), 4);
            // Track 1, no offset from the cluster's time, and whether a player may start decoding here.
            cluster[position++] = 0x81;
            cluster[position++] = 0x00;
            cluster[position++] = 0x00;
            cluster[position] = frame.IsKeyframe ? (byte)0x80 : (byte)0x00;

            try
            {
                _stream.Write(cluster, 0, cluster.Length);
                _stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // Nothing more is written, so the torn frame stays last: closing cuts it off, and so does finishing the file after a crash.
                _writeFailure = exception.Message;
                error = "the frame could not be written: " + exception.Message;
                return false;
            }

            BytesWritten += FrameHeaderBytes + bytes.Length;
            FramesWritten++;
            LastTimestampMs = frame.TimestampMs;
            return true;
        }

        public bool Close(out string error)
        {
            error = null;
            if (_stream == null)
            {
                return true;
            }

            try
            {
                long endOfFrames = BytesWritten;
                // A failed write left part of a frame after the last whole one.
                if (_stream.Length != endOfFrames)
                {
                    _stream.SetLength(endOfFrames);
                }
                _stream.Seek(_segmentSizeOffset, SeekOrigin.Begin);
                byte[] segmentSize = SizeBytes((ulong)(endOfFrames - (_segmentSizeOffset + SegmentSizeWidth)), SegmentSizeWidth);
                _stream.Write(segmentSize, 0, segmentSize.Length);
                _stream.Seek(_durationValueOffset, SeekOrigin.Begin);
                byte[] duration = DurationBytes(Math.Max(0L, LastTimestampMs));
                _stream.Write(duration, 0, duration.Length);
                _stream.Seek(endOfFrames, SeekOrigin.Begin);
                if (_stream is FileStream file)
                {
                    file.Flush(true);
                }
                else
                {
                    _stream.Flush();
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is NotSupportedException)
            {
                error = "the recording's length could not be saved, so it is left for finishing later: " + exception.Message;
            }
            finally
            {
                DisposeStream();
            }
            return error == null;
        }

        private void DisposeStream()
        {
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
            }
            _stream = null;
        }

        public void Dispose()
        {
            Close(out _);
        }

        // Closes without stamping anything, which is exactly what a process that died part-way through leaves.
        internal void AbandonForTesting()
        {
            DisposeStream();
        }

        public ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error)
        {
            framesKept = 0;
            error = null;
            if (!File.Exists(unfinishedPath))
            {
                return ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame;
            }

            int frames = 0;
            try
            {
                // Shared with nobody, so the file cannot change while it is walked, and a file another process still writes is left alone.
                using (FileStream stream = new FileStream(unfinishedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (ReadHeaderLayout(stream, out HeaderLayout layout))
                    {
                        if (layout.Codec == null)
                        {
                            error = $"{unfinishedPath} holds a codec this build cannot check ({layout.CodecName}), so it is left as it was.";
                            return ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish;
                        }
                        long wholeFramesEnd = WalkWholeFrames(stream, layout, out frames, out long lastTimestampMs);
                        if (frames > 0)
                        {
                            stream.SetLength(wholeFramesEnd);
                            stream.Seek(layout.SegmentSizeOffset, SeekOrigin.Begin);
                            byte[] segmentSize = SizeBytes((ulong)(wholeFramesEnd - (layout.SegmentSizeOffset + layout.SegmentSizeWidth)), layout.SegmentSizeWidth);
                            stream.Write(segmentSize, 0, segmentSize.Length);
                            if (layout.DurationValueOffset > 0)
                            {
                                stream.Seek(layout.DurationValueOffset, SeekOrigin.Begin);
                                byte[] duration = DurationBytes(lastTimestampMs);
                                stream.Write(duration, 0, duration.Length);
                            }
                            stream.Flush(true);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException)
            {
                error = $"{unfinishedPath} could not be finished (is another program using it, or the disk full?): {exception.Message}";
                return ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish;
            }

            try
            {
                if (frames == 0)
                {
                    File.Delete(unfinishedPath);
                    return ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame;
                }
                if (!string.Equals(Path.GetFullPath(unfinishedPath), Path.GetFullPath(finishedPath), StringComparison.OrdinalIgnoreCase))
                {
                    // Replaced in one step, so a failure leaves both files as they were.
                    if (File.Exists(finishedPath))
                    {
                        File.Replace(unfinishedPath, finishedPath, null);
                    }
                    else
                    {
                        File.Move(unfinishedPath, finishedPath);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException)
            {
                error = frames == 0
                    ? $"{unfinishedPath} holds no whole frame and could not be deleted: {exception.Message}"
                    : $"{unfinishedPath} could not be renamed to {finishedPath}: {exception.Message}";
                return ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish;
            }

            framesKept = frames;
            return ProtokitePlaytestInterruptedRecordingResult.Finished;
        }

        /// <summary>
        /// Whether a frame starts the way every frame of the codec does, read from its first bytes and its length. Zeros the disk
        /// never filled in fail it, so finishing a cut-off file stops there; the writer refuses a frame that fails it for the same reason.
        /// </summary>
        internal static bool LooksLikeAFrame(ProtokitePlaytestVideoCodec codec, byte[] bytes, int offset, int startLength, long frameLength)
        {
            if (codec == ProtokitePlaytestVideoCodec.Vp9)
            {
                // Every VP9 frame starts with the two bits 10.
                return frameLength >= 1 && startLength >= 1 && (bytes[offset] & 0xC0) == 0x80;
            }
            if (codec != ProtokitePlaytestVideoCodec.Vp8 || frameLength < 3 || startLength < 3)
            {
                return false;
            }
            // A VP8 frame opens with three little-endian bytes: key frame (bit clear), version 0 to 3, and its first part's size,
            // which fits inside the frame; a key frame then carries the start code 9D 01 2A.
            int tag = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
            bool keyframe = (tag & 1) == 0;
            int version = (tag >> 1) & 7;
            long firstPartBytes = tag >> 5;
            int headerBytes = keyframe ? 10 : 3;
            if (version > 3 || headerBytes + firstPartBytes > frameLength)
            {
                return false;
            }
            return !keyframe || (startLength >= 6 && bytes[offset + 3] == 0x9D && bytes[offset + 4] == 0x01 && bytes[offset + 5] == 0x2A);
        }

        private static string CodecName(ProtokitePlaytestVideoCodec codec)
        {
            return codec == ProtokitePlaytestVideoCodec.Vp8 ? CodecNameVp8 : codec == ProtokitePlaytestVideoCodec.Vp9 ? CodecNameVp9 : null;
        }

        private struct HeaderLayout
        {
            public long SegmentSizeOffset;
            public int SegmentSizeWidth;
            public long DurationValueOffset;
            public long FirstClusterOffset;
            public string CodecName;
            public ProtokitePlaytestVideoCodec? Codec;
        }

        // Where the segment size, the duration and the first frame sit, and the codec, all read off the file's own bytes.
        private static bool ReadHeaderLayout(FileStream stream, out HeaderLayout layout)
        {
            layout = default;
            byte[] bytes = new byte[(int)Math.Min(stream.Length, HeaderReadLimit)];
            stream.Seek(0, SeekOrigin.Begin);
            if (ReadFully(stream, bytes, bytes.Length) != bytes.Length)
            {
                return false;
            }

            if (!ReadElementHead(bytes, 0, out uint id, out ulong size, out int dataStart, out bool unknownSize) || id != IdEbmlHeader || unknownSize)
            {
                return false;
            }
            long ebmlEnd = dataStart + (long)size;
            if (ebmlEnd > bytes.Length || FindString(bytes, dataStart, (int)ebmlEnd, IdDocType) != "webm")
            {
                return false;
            }

            int position = (int)ebmlEnd;
            if (!ReadId(bytes, position, out uint segmentId, out int segmentIdWidth) || segmentId != IdSegment)
            {
                return false;
            }
            int segmentSizeOffset = position + segmentIdWidth;
            if (!ReadSize(bytes, segmentSizeOffset, out _, out int segmentSizeWidth, out _))
            {
                return false;
            }
            layout.SegmentSizeOffset = segmentSizeOffset;
            layout.SegmentSizeWidth = segmentSizeWidth;

            position = segmentSizeOffset + segmentSizeWidth;
            while (true)
            {
                // The header ends exactly where the file does: a recording with no frame yet.
                if (position >= bytes.Length)
                {
                    layout.FirstClusterOffset = position;
                    return position == stream.Length && layout.CodecName != null;
                }
                if (!ReadElementHead(bytes, position, out uint childId, out ulong childSize, out int childData, out bool childUnknown))
                {
                    return false;
                }
                if (childId == IdCluster)
                {
                    layout.FirstClusterOffset = position;
                    return layout.CodecName != null;
                }
                long childEnd = childData + (long)childSize;
                if (childUnknown || childEnd > bytes.Length)
                {
                    return false;
                }
                if (childId == IdInfo)
                {
                    layout.DurationValueOffset = FindDurationValue(bytes, childData, (int)childEnd);
                }
                else if (childId == IdTracks && ReadElementHead(bytes, childData, out uint entryId, out ulong entrySize, out int entryData, out _)
                    && entryId == IdTrackEntry && entryData + (long)entrySize <= childEnd)
                {
                    layout.CodecName = FindString(bytes, entryData, entryData + (int)entrySize, IdCodecId);
                    layout.Codec = layout.CodecName == CodecNameVp8 ? ProtokitePlaytestVideoCodec.Vp8
                        : layout.CodecName == CodecNameVp9 ? ProtokitePlaytestVideoCodec.Vp9
                        : (ProtokitePlaytestVideoCodec?)null;
                }
                position = (int)childEnd;
            }
        }

        // Each cluster was written in one piece with its size known, so one is whole when it fits in the file and its frame
        // starts the way the codec's frames do.
        private static long WalkWholeFrames(FileStream stream, HeaderLayout layout, out int frames, out long lastTimestampMs)
        {
            frames = 0;
            lastTimestampMs = 0;
            long fileBytes = stream.Length;
            long wholeFramesEnd = layout.FirstClusterOffset;
            byte[] head = new byte[FrameHeaderBytes + FrameStartBytesChecked];
            while (wholeFramesEnd + FrameHeaderBytes + 1 <= fileBytes)
            {
                int headBytes = (int)Math.Min(head.Length, fileBytes - wholeFramesEnd);
                stream.Seek(wholeFramesEnd, SeekOrigin.Begin);
                if (ReadFully(stream, head, headBytes) != headBytes)
                {
                    break;
                }
                if (head[0] != 0x1F || head[1] != 0x43 || head[2] != 0xB6 || head[3] != 0x75 || (head[4] & 0xF0) != 0x10)
                {
                    break;
                }
                long clusterBytes = (long)(ReadBigEndian(head, 4, 4) & 0x0FFFFFFF);
                long clusterEnd = wholeFramesEnd + 8 + clusterBytes;
                long frameLength = clusterBytes - (FrameHeaderBytes - 8);
                long blockBytes = (long)(ReadBigEndian(head, 15, 4) & 0x0FFFFFFF);
                // The time, then the block: a header whose frame never arrived, or whose sizes disagree, ends the video.
                if (frameLength < 1 || clusterEnd > fileBytes || head[8] != 0xE7 || head[9] != 0x84 || head[14] != 0xA3
                    || (head[15] & 0xF0) != 0x10 || blockBytes != 4 + frameLength || head[19] != 0x81)
                {
                    break;
                }
                int startLength = (int)Math.Min(FrameStartBytesChecked, Math.Min(frameLength, headBytes - FrameHeaderBytes));
                if (!LooksLikeAFrame(layout.Codec.Value, head, FrameHeaderBytes, startLength, frameLength))
                {
                    break;
                }
                lastTimestampMs = (long)ReadBigEndian(head, 10, 4);
                wholeFramesEnd = clusterEnd;
                frames++;
            }
            return wholeFramesEnd;
        }

        private static byte[] BuildHeader(int width, int height, string codecName, string writtenBy, out long segmentSizeOffset, out long durationValueOffset)
        {
            List<byte> ebml = new List<byte>();
            AppendUnsignedElement(ebml, IdEbmlVersion, 1, 1);
            AppendUnsignedElement(ebml, IdEbmlReadVersion, 1, 1);
            AppendUnsignedElement(ebml, IdEbmlMaxIdLength, 4, 1);
            AppendUnsignedElement(ebml, IdEbmlMaxSizeLength, 8, 1);
            AppendStringElement(ebml, IdDocType, "webm");
            AppendUnsignedElement(ebml, IdDocTypeVersion, 2, 1);
            AppendUnsignedElement(ebml, IdDocTypeReadVersion, 2, 1);

            List<byte> info = new List<byte>();
            AppendUnsignedElement(info, IdTimecodeScale, TimecodeScaleNanoseconds, 4);
            AppendStringElement(info, IdMuxingApp, writtenBy);
            AppendStringElement(info, IdWritingApp, writtenBy);
            AppendId(info, IdDuration);
            info.AddRange(SizeBytes(8, 1));
            int durationInInfo = info.Count;
            info.AddRange(DurationBytes(0));

            List<byte> video = new List<byte>();
            AppendUnsignedElement(video, IdPixelWidth, (ulong)width, 4);
            AppendUnsignedElement(video, IdPixelHeight, (ulong)height, 4);

            List<byte> track = new List<byte>();
            AppendUnsignedElement(track, IdTrackNumber, 1, 1);
            AppendUnsignedElement(track, IdTrackUid, 1, 1);
            AppendUnsignedElement(track, IdTrackType, 1, 1);
            AppendStringElement(track, IdCodecId, codecName);
            AppendId(track, IdVideo);
            track.AddRange(SizeBytes((ulong)video.Count, 1));
            track.AddRange(video);

            List<byte> header = new List<byte>();
            AppendId(header, IdEbmlHeader);
            header.AddRange(SizeBytes((ulong)ebml.Count, 1));
            header.AddRange(ebml);

            AppendId(header, IdSegment);
            segmentSizeOffset = header.Count;
            // "Size unknown" is legal, and is what a recording that never closed keeps; eight bytes so the real size fits later.
            header.AddRange(SizeBytes(UnknownSegmentSize, SegmentSizeWidth));

            AppendId(header, IdInfo);
            header.AddRange(SizeBytes((ulong)info.Count, 4));
            durationValueOffset = header.Count + durationInInfo;
            header.AddRange(info);

            AppendId(header, IdTracks);
            header.AddRange(SizeBytes((ulong)(track.Count + 5), 4));
            AppendId(header, IdTrackEntry);
            header.AddRange(SizeBytes((ulong)track.Count, 4));
            header.AddRange(track);
            return header.ToArray();
        }

        private static long FindDurationValue(byte[] bytes, int start, int end)
        {
            int position = start;
            while (position < end && ReadElementHead(bytes, position, out uint id, out ulong size, out int dataStart, out bool unknown) && !unknown)
            {
                if (id == IdDuration && size == 8)
                {
                    return dataStart;
                }
                position = dataStart + (int)size;
            }
            return 0;
        }

        private static string FindString(byte[] bytes, int start, int end, uint wantedId)
        {
            int position = start;
            while (position < end && ReadElementHead(bytes, position, out uint id, out ulong size, out int dataStart, out bool unknown) && !unknown)
            {
                if (id == wantedId)
                {
                    return dataStart + (long)size <= end ? Encoding.ASCII.GetString(bytes, dataStart, (int)size) : null;
                }
                position = dataStart + (int)size;
            }
            return null;
        }

        private static bool ReadElementHead(byte[] bytes, int position, out uint id, out ulong size, out int dataStart, out bool unknownSize)
        {
            size = 0;
            dataStart = 0;
            unknownSize = false;
            if (!ReadId(bytes, position, out id, out int idWidth) || !ReadSize(bytes, position + idWidth, out size, out int sizeWidth, out unknownSize))
            {
                return false;
            }
            dataStart = position + idWidth + sizeWidth;
            return true;
        }

        // An id is kept as the bytes WebM writes for it, one to four of them, the count given by the first byte's leading zeros.
        private static bool ReadId(byte[] bytes, int position, out uint id, out int width)
        {
            id = 0;
            width = 0;
            if (position >= bytes.Length || bytes[position] == 0)
            {
                return false;
            }
            width = LeadingZeros(bytes[position]) + 1;
            if (width > 4 || position + width > bytes.Length)
            {
                return false;
            }
            id = (uint)ReadBigEndian(bytes, position, width);
            return true;
        }

        private static bool ReadSize(byte[] bytes, int position, out ulong size, out int width, out bool unknown)
        {
            size = 0;
            width = 0;
            unknown = false;
            if (position >= bytes.Length || bytes[position] == 0)
            {
                return false;
            }
            width = LeadingZeros(bytes[position]) + 1;
            if (position + width > bytes.Length)
            {
                return false;
            }
            ulong valueBits = (1UL << (7 * width)) - 1;
            size = ReadBigEndian(bytes, position, width) & valueBits;
            unknown = size == valueBits;
            return true;
        }

        private static int LeadingZeros(byte value)
        {
            int count = 0;
            for (int bit = 7; bit >= 0 && (value & (1 << bit)) == 0; bit--)
            {
                count++;
            }
            return count;
        }

        private static void AppendId(List<byte> bytes, uint id)
        {
            if (id > 0x00FFFFFF) bytes.Add((byte)(id >> 24));
            if (id > 0x0000FFFF) bytes.Add((byte)(id >> 16));
            if (id > 0x000000FF) bytes.Add((byte)(id >> 8));
            bytes.Add((byte)id);
        }

        private static void WriteId(byte[] bytes, ref int position, uint id)
        {
            if (id > 0x00FFFFFF) bytes[position++] = (byte)(id >> 24);
            if (id > 0x0000FFFF) bytes[position++] = (byte)(id >> 16);
            if (id > 0x000000FF) bytes[position++] = (byte)(id >> 8);
            bytes[position++] = (byte)id;
        }

        private static void WriteSize(byte[] bytes, ref int position, ulong size, int width)
        {
            byte[] sizeBytes = SizeBytes(size, width);
            Array.Copy(sizeBytes, 0, bytes, position, width);
            position += width;
        }

        private static void WriteBigEndian(byte[] bytes, ref int position, ulong value, int width)
        {
            for (int index = 0; index < width; index++)
            {
                bytes[position++] = (byte)(value >> (8 * (width - 1 - index)));
            }
        }

        // A size is big-endian with a leading 1 bit giving its width; the width is passed in so a size stamped later always fits.
        private static byte[] SizeBytes(ulong size, int width)
        {
            byte[] bytes = new byte[width];
            for (int index = 0; index < width; index++)
            {
                bytes[index] = (byte)(size >> (8 * (width - 1 - index)));
            }
            bytes[0] |= (byte)(1 << (8 - width));
            return bytes;
        }

        private static void AppendUnsignedElement(List<byte> bytes, uint id, ulong value, int valueWidth)
        {
            AppendId(bytes, id);
            bytes.AddRange(SizeBytes((ulong)valueWidth, 1));
            for (int index = 0; index < valueWidth; index++)
            {
                bytes.Add((byte)(value >> (8 * (valueWidth - 1 - index))));
            }
        }

        private static void AppendStringElement(List<byte> bytes, uint id, string value)
        {
            byte[] text = Encoding.ASCII.GetBytes(value);
            AppendId(bytes, id);
            bytes.AddRange(SizeBytes((ulong)text.Length, 1));
            bytes.AddRange(text);
        }

        // The duration is a float in milliseconds, written big-endian.
        private static byte[] DurationBytes(long milliseconds)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(milliseconds);
            byte[] bytes = new byte[8];
            for (int index = 0; index < 8; index++)
            {
                bytes[index] = (byte)(bits >> (8 * (7 - index)));
            }
            return bytes;
        }

        private static ulong ReadBigEndian(byte[] bytes, int position, int width)
        {
            ulong value = 0;
            for (int index = 0; index < width; index++)
            {
                value = (value << 8) | bytes[position + index];
            }
            return value;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }
    }

    /// <summary>The kind of file recordings are written to on this platform, so nothing outside this file names it.</summary>
    internal static class ProtokitePlaytestRecordingFiles
    {
        /// <summary>A new file, not yet open.</summary>
        public static IProtokitePlaytestRecordingFile Create() => new ProtokitePlaytestWebmFile();
    }
}
