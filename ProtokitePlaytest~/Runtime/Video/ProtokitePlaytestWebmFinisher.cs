using System;
using System.IO;
using System.Text;

namespace Protokite.Playtest
{
    /// <summary>Finishes a WebM recording (VP8 or VP9) an earlier version of the package was writing when its game ended, so it is kept and uploaded; this version writes no WebM.</summary>
    internal sealed class ProtokitePlaytestWebmFinisher : IProtokitePlaytestRecordingFinisher
    {
        /// <summary>What each frame cost on top of its own bytes: a cluster around it, its time and the block header.</summary>
        internal const int FrameHeaderBytes = 23;

        private const uint IdEbmlHeader = 0x1A45DFA3;
        private const uint IdDocType = 0x4282;
        private const uint IdSegment = 0x18538067;
        private const uint IdInfo = 0x1549A966;
        private const uint IdDuration = 0x4489;
        private const uint IdTracks = 0x1654AE6B;
        private const uint IdTrackEntry = 0xAE;
        private const uint IdCodecId = 0x86;
        private const uint IdCluster = 0x1F43B675;
        private const string CodecNameVp8 = "V_VP8";
        private const string CodecNameVp9 = "V_VP9";
        private const int HeaderReadLimit = 64 * 1024;
        // A VP8 key frame's first six bytes say everything the frame check needs.
        private const int FrameStartBytesChecked = 6;

        /// <summary>The codecs a WebM recording held.</summary>
        internal enum Codec
        {
            Vp8,
            Vp9
        }

        public ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error)
        {
            framesKept = 0;
            error = null;
            if (!File.Exists(unfinishedPath))
                return ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame;

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
                        File.Replace(unfinishedPath, finishedPath, null);
                    else
                        File.Move(unfinishedPath, finishedPath);
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
        /// never filled in fail it, so finishing a cut-off file stops there.
        /// </summary>
        internal static bool LooksLikeAFrame(Codec codec, byte[] bytes, int offset, int startLength, long frameLength)
        {
            if (codec == Codec.Vp9)
            {
                // Every VP9 frame starts with the two bits 10.
                return frameLength >= 1 && startLength >= 1 && (bytes[offset] & 0xC0) == 0x80;
            }
            if (frameLength < 3 || startLength < 3)
                return false;
            // A VP8 frame opens with three little-endian bytes: key frame (bit clear), version 0 to 3, and its first part's size,
            // which fits inside the frame; a key frame then carries the start code 9D 01 2A.
            int tag = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
            bool keyframe = (tag & 1) == 0;
            int version = (tag >> 1) & 7;
            long firstPartBytes = tag >> 5;
            int headerBytes = keyframe ? 10 : 3;
            if (version > 3 || headerBytes + firstPartBytes > frameLength)
                return false;
            return !keyframe || (startLength >= 6 && bytes[offset + 3] == 0x9D && bytes[offset + 4] == 0x01 && bytes[offset + 5] == 0x2A);
        }

        private struct HeaderLayout
        {
            public long SegmentSizeOffset;
            public int SegmentSizeWidth;
            public long DurationValueOffset;
            public long FirstClusterOffset;
            public string CodecName;
            public Codec? Codec;
        }

        // Where the segment size, the duration and the first frame sit, and the codec, all read off the file's own bytes.
        private static bool ReadHeaderLayout(FileStream stream, out HeaderLayout layout)
        {
            layout = default;
            byte[] bytes = new byte[(int)Math.Min(stream.Length, HeaderReadLimit)];
            stream.Seek(0, SeekOrigin.Begin);
            if (ReadFully(stream, bytes, bytes.Length) != bytes.Length)
                return false;

            if (!ReadElementHead(bytes, 0, out uint id, out ulong size, out int dataStart, out bool unknownSize) || id != IdEbmlHeader || unknownSize)
                return false;
            long ebmlEnd = dataStart + (long)size;
            if (ebmlEnd > bytes.Length || FindString(bytes, dataStart, (int)ebmlEnd, IdDocType) != "webm")
                return false;

            int position = (int)ebmlEnd;
            if (!ReadId(bytes, position, out uint segmentId, out int segmentIdWidth) || segmentId != IdSegment)
                return false;
            int segmentSizeOffset = position + segmentIdWidth;
            if (!ReadSize(bytes, segmentSizeOffset, out _, out int segmentSizeWidth, out _))
                return false;
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
                    return false;
                if (childId == IdCluster)
                {
                    layout.FirstClusterOffset = position;
                    return layout.CodecName != null;
                }
                long childEnd = childData + (long)childSize;
                if (childUnknown || childEnd > bytes.Length)
                    return false;
                if (childId == IdInfo)
                {
                    layout.DurationValueOffset = FindDurationValue(bytes, childData, (int)childEnd);
                }
                else if (childId == IdTracks && ReadElementHead(bytes, childData, out uint entryId, out ulong entrySize, out int entryData, out _)
                    && entryId == IdTrackEntry && entryData + (long)entrySize <= childEnd)
                {
                    layout.CodecName = FindString(bytes, entryData, entryData + (int)entrySize, IdCodecId);
                    layout.Codec = layout.CodecName == CodecNameVp8 ? Codec.Vp8
                        : layout.CodecName == CodecNameVp9 ? Codec.Vp9
                        : (Codec?)null;
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
                    break;
                if (head[0] != 0x1F || head[1] != 0x43 || head[2] != 0xB6 || head[3] != 0x75 || (head[4] & 0xF0) != 0x10)
                    break;
                long clusterBytes = (long)(ReadBigEndian(head, 4, 4) & 0x0FFFFFFF);
                long clusterEnd = wholeFramesEnd + 8 + clusterBytes;
                long frameLength = clusterBytes - (FrameHeaderBytes - 8);
                long blockBytes = (long)(ReadBigEndian(head, 15, 4) & 0x0FFFFFFF);
                // The time, then the block: a header whose frame never arrived, or whose sizes disagree, ends the video.
                if (frameLength < 1 || clusterEnd > fileBytes || head[8] != 0xE7 || head[9] != 0x84 || head[14] != 0xA3
                    || (head[15] & 0xF0) != 0x10 || blockBytes != 4 + frameLength || head[19] != 0x81)
                    break;
                int startLength = (int)Math.Min(FrameStartBytesChecked, Math.Min(frameLength, headBytes - FrameHeaderBytes));
                if (!LooksLikeAFrame(layout.Codec.Value, head, FrameHeaderBytes, startLength, frameLength))
                    break;
                lastTimestampMs = (long)ReadBigEndian(head, 10, 4);
                wholeFramesEnd = clusterEnd;
                frames++;
            }
            return wholeFramesEnd;
        }

        private static long FindDurationValue(byte[] bytes, int start, int end)
        {
            int position = start;
            while (position < end && ReadElementHead(bytes, position, out uint id, out ulong size, out int dataStart, out bool unknown) && !unknown)
            {
                if (id == IdDuration && size == 8)
                    return dataStart;
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
                    return dataStart + (long)size <= end ? Encoding.ASCII.GetString(bytes, dataStart, (int)size) : null;
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
                return false;
            dataStart = position + idWidth + sizeWidth;
            return true;
        }

        // An id is kept as the bytes WebM writes for it, one to four of them, the count given by the first byte's leading zeros.
        private static bool ReadId(byte[] bytes, int position, out uint id, out int width)
        {
            id = 0;
            width = 0;
            if (position >= bytes.Length || bytes[position] == 0)
                return false;
            width = LeadingZeros(bytes[position]) + 1;
            if (width > 4 || position + width > bytes.Length)
                return false;
            id = (uint)ReadBigEndian(bytes, position, width);
            return true;
        }

        private static bool ReadSize(byte[] bytes, int position, out ulong size, out int width, out bool unknown)
        {
            size = 0;
            width = 0;
            unknown = false;
            if (position >= bytes.Length || bytes[position] == 0)
                return false;
            width = LeadingZeros(bytes[position]) + 1;
            if (position + width > bytes.Length)
                return false;
            ulong valueBits = (1UL << (7 * width)) - 1;
            size = ReadBigEndian(bytes, position, width) & valueBits;
            unknown = size == valueBits;
            return true;
        }

        private static int LeadingZeros(byte value)
        {
            int count = 0;
            for (int bit = 7; bit >= 0 && (value & (1 << bit)) == 0; bit--)
                count++;
            return count;
        }

        // A size is big-endian with a leading 1 bit giving its width; the width is passed in so a size stamped later always fits.
        internal static byte[] SizeBytes(ulong size, int width)
        {
            byte[] bytes = new byte[width];
            for (int index = 0; index < width; index++)
                bytes[index] = (byte)(size >> (8 * (width - 1 - index)));
            bytes[0] |= (byte)(1 << (8 - width));
            return bytes;
        }

        // The duration is a float in milliseconds, written big-endian.
        internal static byte[] DurationBytes(long milliseconds)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(milliseconds);
            byte[] bytes = new byte[8];
            for (int index = 0; index < 8; index++)
                bytes[index] = (byte)(bits >> (8 * (7 - index)));
            return bytes;
        }

        private static ulong ReadBigEndian(byte[] bytes, int position, int width)
        {
            ulong value = 0;
            for (int index = 0; index < width; index++)
                value = (value << 8) | bytes[position + index];
            return value;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }
    }
}
