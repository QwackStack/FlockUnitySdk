using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Protokite.Playtest
{
    /// <summary>Writes H.264 frames as a fragmented MP4 a browser plays, one fragment per frame written in one piece, so a file cut off anywhere plays up to the cut. One thread at a time, except <see cref="BytesFor"/>.</summary>
    internal sealed class ProtokitePlaytestMp4File : IProtokitePlaytestRecordingFile
    {
        /// <summary>Times in the file are in milliseconds.</summary>
        internal const uint Timescale = 1000;
        /// <summary>What each frame costs on top of its own bytes: its fragment's header and the header of the box its bytes sit in.</summary>
        internal const int FrameHeaderBytes = 108;
        /// <summary>The largest frame one fragment holds: the box around its bytes has a four-byte size.</summary>
        internal const long MaxFrameBytes = uint.MaxValue - 8L;

        private const uint TrackId = 1;
        private const int HeadReadLimit = 64 * 1024;
        // Every fragment is laid out the same way; a cut-off file is finished by reading these places back.
        private const int SequenceOffset = 20;
        private const int DecodeTimeOffset = 60;
        private const int SampleDurationOffset = 88;
        private const int SampleSizeOffset = 92;
        private const int SampleFlagsOffset = 96;
        private const int FrameBoxSizeOffset = 100;
        private const uint KeyframeFlags = 0x02000000;
        private const uint OtherFrameFlags = 0x01010000;
        // Bytes of a frame read when a cut-off file is finished: enough for its first unit's length and first byte.
        private const int FrameStartBytesChecked = 5;
        private static readonly byte[] FragmentTemplate = BuildFragmentTemplate();

        private readonly Func<string, Stream> _openForWriting;
        private Stream _stream;
        private int _width;
        private int _height;
        private long _frameDurationMs;
        private long _durationValueOffset = -1;
        private uint _nextSequence;
        private string _writeFailure;
        // Each frame goes out in one write through this, grown to the largest frame so far rather than made anew each time.
        private byte[] _piece;

        public string ContentType => "video/mp4";
        public string FileExtension => ".mp4";
        public int BytesAddedToEachFrame => FrameHeaderBytes;
        public long BytesWritten { get; private set; }
        public int FramesWritten { get; private set; }
        public long LastTimestampMs { get; private set; } = -1;

        public ProtokitePlaytestMp4File() : this(null)
        {
        }

        // Another stream stands in for a disk that fails; tests use it.
        internal ProtokitePlaytestMp4File(Func<string, Stream> openForWriting)
        {
            // Unbuffered: each frame reaches the operating system as it is written, so a process that dies loses at most the
            // frame in progress, and a write the disk refuses fails at that write rather than in a later flush.
            _openForWriting = openForWriting ?? (path => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1));
        }

        public long BytesFor(ProtokitePlaytestEncodedFrame frame)
            => FrameHeaderBytes + (frame.Data?.LongLength ?? 0) + (frame.DecoderSettings == null ? 0 : BuildMovieHeader(16, 16, 1, frame.DecoderSettings, out _).LongLength);

        public bool Open(string path, int width, int height, long frameDurationMs, out string error)
        {
            Close(out _);
            BytesWritten = 0;
            FramesWritten = 0;
            LastTimestampMs = -1;
            _durationValueOffset = -1;
            _nextSequence = 1;
            _writeFailure = null;

            if (width < 2 || height < 2 || width > 65535 || height > 65535)
            {
                error = $"a {width}x{height} video cannot be recorded.";
                return false;
            }
            if (frameDurationMs < 1 || frameDurationMs > uint.MaxValue)
            {
                error = $"a frame cannot be shown for {frameDurationMs} ms.";
                return false;
            }

            byte[] fileType = BuildFileType();
            try
            {
                _stream = _openForWriting(path);
                _stream.Write(fileType, 0, fileType.Length);
                _stream.Flush();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                DisposeStream();
                error = $"the file {path} could not be written: {exception.Message}";
                return false;
            }

            _width = width;
            _height = height;
            _frameDurationMs = frameDurationMs;
            BytesWritten = fileType.Length;
            error = null;
            return true;
        }

        public bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error)
        {
            byte[] bytes = frame.Data;
            bool first = FramesWritten == 0;
            error = _stream == null ? "the file is not open."
                : _writeFailure != null ? "an earlier write failed, so the file takes no more frames: " + _writeFailure
                : bytes == null || bytes.Length == 0 ? "a frame has at least one byte."
                : bytes.LongLength > MaxFrameBytes ? $"a frame of {bytes.LongLength} bytes is larger than one MP4 fragment holds."
                : frame.TimestampMs < 0 ? $"a frame's time cannot be before 0 ms, as {frame.TimestampMs} ms is."
                : frame.TimestampMs <= LastTimestampMs ? $"a frame at {frame.TimestampMs} ms is not later than the last one, at {LastTimestampMs} ms."
                : first && frame.DecoderSettings == null ? "the first frame does not carry the stream's settings, so no player could decode the file."
                // Finishing a cut-off file stops at a frame that fails this check and loses every frame after it.
                : !ProtokitePlaytestH264.LooksLikeAStoredFrame(bytes, 0, Math.Min(bytes.Length, FrameStartBytesChecked), bytes.Length) ? "the frame does not start the way a stored H.264 frame does."
                : null;
            if (error != null)
                return false;

            long durationInHeader = 0;
            byte[] header = first ? BuildMovieHeader(_width, _height, _frameDurationMs, frame.DecoderSettings, out durationInHeader) : null;
            int headerBytes = header?.Length ?? 0;
            int pieceBytes = headerBytes + FrameHeaderBytes + bytes.Length;
            if (_piece == null || _piece.Length < pieceBytes)
                _piece = new byte[pieceBytes];
            byte[] piece = _piece;
            if (header != null)
                Array.Copy(header, piece, headerBytes);
            Array.Copy(FragmentTemplate, 0, piece, headerBytes, FrameHeaderBytes);
            WriteBigEndian(piece, headerBytes + SequenceOffset, _nextSequence, 4);
            WriteBigEndian(piece, headerBytes + DecodeTimeOffset, (ulong)frame.TimestampMs, 8);
            WriteBigEndian(piece, headerBytes + SampleDurationOffset, (ulong)_frameDurationMs, 4);
            WriteBigEndian(piece, headerBytes + SampleSizeOffset, (ulong)bytes.Length, 4);
            WriteBigEndian(piece, headerBytes + SampleFlagsOffset, frame.IsKeyframe ? KeyframeFlags : OtherFrameFlags, 4);
            WriteBigEndian(piece, headerBytes + FrameBoxSizeOffset, 8UL + (ulong)bytes.Length, 4);
            Array.Copy(bytes, 0, piece, headerBytes + FrameHeaderBytes, bytes.Length);

            try
            {
                _stream.Write(piece, 0, pieceBytes);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // Nothing more is written, so the torn piece stays last: closing cuts it off, and so does finishing the file after a crash.
                _writeFailure = exception.Message;
                error = "the frame could not be written: " + exception.Message;
                return false;
            }

            if (header != null)
                _durationValueOffset = BytesWritten + durationInHeader;
            BytesWritten += pieceBytes;
            FramesWritten++;
            LastTimestampMs = frame.TimestampMs;
            _nextSequence++;
            return true;
        }

        public bool Close(out string error)
        {
            error = null;
            if (_stream == null)
                return true;

            try
            {
                long endOfFrames = BytesWritten;
                // A failed write left part of a frame after the last whole one.
                if (_stream.Length != endOfFrames)
                    _stream.SetLength(endOfFrames);
                if (_durationValueOffset > 0)
                {
                    _stream.Seek(_durationValueOffset, SeekOrigin.Begin);
                    byte[] duration = new byte[8];
                    WriteBigEndian(duration, 0, (ulong)(LastTimestampMs + _frameDurationMs), 8);
                    _stream.Write(duration, 0, duration.Length);
                    _stream.Seek(endOfFrames, SeekOrigin.Begin);
                }
                if (_stream is FileStream file)
                    file.Flush(true);
                else
                    _stream.Flush();
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
                return ProtokitePlaytestInterruptedRecordingResult.HeldNoFrame;

            int frames = 0;
            try
            {
                // Shared with nobody, so the file cannot change while it is walked, and a file another process still writes is left alone.
                using (FileStream stream = new FileStream(unfinishedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (ReadMovieHeader(stream, out long firstFrameOffset, out long durationValueOffset))
                    {
                        if (durationValueOffset < 0)
                        {
                            error = $"{unfinishedPath} is not laid out the way this package writes recordings, so it is left as it was.";
                            return ProtokitePlaytestInterruptedRecordingResult.CouldNotFinish;
                        }
                        long wholeFramesEnd = WalkWholeFrames(stream, firstFrameOffset, out frames, out long videoEndMs);
                        if (frames > 0)
                        {
                            stream.SetLength(wholeFramesEnd);
                            stream.Seek(durationValueOffset, SeekOrigin.Begin);
                            byte[] duration = new byte[8];
                            WriteBigEndian(duration, 0, (ulong)videoEndMs, 8);
                            stream.Write(duration, 0, duration.Length);
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

        // Where the first frame starts and where the length is stamped, read off the file's own bytes; false when its header is not all there.
        private static bool ReadMovieHeader(FileStream stream, out long firstFrameOffset, out long durationValueOffset)
        {
            firstFrameOffset = 0;
            durationValueOffset = -1;
            byte[] bytes = new byte[(int)Math.Min(stream.Length, HeadReadLimit)];
            stream.Seek(0, SeekOrigin.Begin);
            if (ReadFully(stream, bytes, bytes.Length) != bytes.Length)
                return false;
            if (!ReadBoxHead(bytes, 0, bytes.Length, out string fileType, out long fileTypeSize) || fileType != "ftyp")
                return false;
            if (!ReadBoxHead(bytes, (int)fileTypeSize, bytes.Length, out string movieType, out long movieSize) || movieType != "moov")
                return false;
            long movieEnd = fileTypeSize + movieSize;
            if (movieEnd > bytes.Length)
                return false;
            firstFrameOffset = movieEnd;
            int extends = FindChild(bytes, (int)fileTypeSize + 8, (int)movieEnd, "mvex", out int extendsEnd);
            if (extends < 0)
                return true;
            int lengthBox = FindChild(bytes, extends + 8, extendsEnd, "mehd", out int lengthBoxEnd);
            // Version 1: an eight-byte length after the version and flags.
            if (lengthBox >= 0 && lengthBoxEnd - lengthBox == 20 && bytes[lengthBox + 8] == 1)
                durationValueOffset = lengthBox + 12;
            return true;
        }

        // Each fragment was written in one piece with its size known, so one is whole when it fits in the file, matches the
        // layout this file writes and its frame starts the way a stored frame does.
        private static long WalkWholeFrames(FileStream stream, long firstFrameOffset, out int frames, out long videoEndMs)
        {
            frames = 0;
            videoEndMs = 0;
            long fileBytes = stream.Length;
            long wholeFramesEnd = firstFrameOffset;
            byte[] head = new byte[FrameHeaderBytes + FrameStartBytesChecked];
            while (wholeFramesEnd + FrameHeaderBytes + 1 <= fileBytes)
            {
                int headBytes = (int)Math.Min(head.Length, fileBytes - wholeFramesEnd);
                stream.Seek(wholeFramesEnd, SeekOrigin.Begin);
                if (ReadFully(stream, head, headBytes) != headBytes || !MatchesTheFragmentLayout(head))
                    break;
                long frameBytes = (long)ReadBigEndian(head, SampleSizeOffset, 4);
                long fragmentEnd = wholeFramesEnd + FrameHeaderBytes + frameBytes;
                int startLength = (int)Math.Min(FrameStartBytesChecked, Math.Min(frameBytes, headBytes - FrameHeaderBytes));
                if (frameBytes < 1 || fragmentEnd > fileBytes || (long)ReadBigEndian(head, FrameBoxSizeOffset, 4) != 8 + frameBytes
                    || !ProtokitePlaytestH264.LooksLikeAStoredFrame(head, FrameHeaderBytes, startLength, frameBytes))
                    break;
                videoEndMs = (long)ReadBigEndian(head, DecodeTimeOffset, 8) + (long)ReadBigEndian(head, SampleDurationOffset, 4);
                wholeFramesEnd = fragmentEnd;
                frames++;
            }
            return wholeFramesEnd;
        }

        // Every byte the writer never changes from frame to frame must be as written; the sample's flags must be one of the two it writes.
        private static bool MatchesTheFragmentLayout(byte[] head)
        {
            for (int index = 0; index < FrameHeaderBytes; index++)
            {
                bool changes = (index >= SequenceOffset && index < SequenceOffset + 4)
                               || (index >= DecodeTimeOffset && index < DecodeTimeOffset + 8)
                               || (index >= SampleDurationOffset && index < FrameBoxSizeOffset + 4);
                if (!changes && head[index] != FragmentTemplate[index])
                    return false;
            }
            uint flags = (uint)ReadBigEndian(head, SampleFlagsOffset, 4);
            return flags == KeyframeFlags || flags == OtherFrameFlags;
        }

        private static byte[] BuildFragmentTemplate()
        {
            Boxes boxes = new Boxes();
            boxes.Begin("moof");
            boxes.BeginFull("mfhd", 0, 0);
            boxes.UInt32(0);
            boxes.End();
            boxes.Begin("traf");
            // The frame's bytes are found from the start of this fragment.
            boxes.BeginFull("tfhd", 0, 0x020000);
            boxes.UInt32(TrackId);
            boxes.End();
            boxes.BeginFull("tfdt", 1, 0);
            boxes.UInt64(0);
            boxes.End();
            // One frame, with where its bytes start, how long it shows, its size and whether a player can start at it.
            boxes.BeginFull("trun", 0, 0x000701);
            boxes.UInt32(1);
            boxes.UInt32(FrameHeaderBytes);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.End();
            boxes.End();
            boxes.End();
            boxes.UInt32(8);
            boxes.Type("mdat");
            byte[] template = boxes.ToArray();
            if (template.Length != FrameHeaderBytes)
                throw new InvalidOperationException($"an MP4 fragment's header came to {template.Length} bytes, not {FrameHeaderBytes}.");
            return template;
        }

        private static byte[] BuildFileType()
        {
            Boxes boxes = new Boxes();
            boxes.Begin("ftyp");
            boxes.Type("isom");
            boxes.UInt32(0x200);
            boxes.Type("isom");
            boxes.Type("iso6");
            boxes.Type("avc1");
            boxes.Type("mp41");
            boxes.End();
            return boxes.ToArray();
        }

        // The movie's header: one video track, no frames of its own (they come in fragments), and the length stamped in on close.
        private static byte[] BuildMovieHeader(int width, int height, long frameDurationMs, byte[] decoderSettings, out long durationValueOffset)
        {
            Boxes boxes = new Boxes();
            boxes.Begin("moov");
            boxes.BeginFull("mvhd", 0, 0);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.UInt32(Timescale);
            boxes.UInt32(0);
            boxes.UInt32(0x00010000);
            boxes.UInt16(0x0100);
            boxes.Zeros(10);
            boxes.Matrix();
            boxes.Zeros(24);
            boxes.UInt32(TrackId + 1);
            boxes.End();

            boxes.Begin("trak");
            boxes.BeginFull("tkhd", 0, 3);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.UInt32(TrackId);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.Zeros(8);
            boxes.UInt16(0);
            boxes.UInt16(0);
            boxes.UInt16(0);
            boxes.UInt16(0);
            boxes.Matrix();
            boxes.UInt32((uint)width << 16);
            boxes.UInt32((uint)height << 16);
            boxes.End();

            boxes.Begin("mdia");
            boxes.BeginFull("mdhd", 0, 0);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.UInt32(Timescale);
            boxes.UInt32(0);
            // "und", the language of a track that has none.
            boxes.UInt16(0x55C4);
            boxes.UInt16(0);
            boxes.End();
            boxes.BeginFull("hdlr", 0, 0);
            boxes.UInt32(0);
            boxes.Type("vide");
            boxes.Zeros(12);
            boxes.Text("VideoHandler");
            boxes.End();

            boxes.Begin("minf");
            boxes.BeginFull("vmhd", 0, 1);
            boxes.Zeros(8);
            boxes.End();
            boxes.Begin("dinf");
            boxes.BeginFull("dref", 0, 0);
            boxes.UInt32(1);
            // The frames are in this file.
            boxes.BeginFull("url ", 0, 1);
            boxes.End();
            boxes.End();
            boxes.End();

            boxes.Begin("stbl");
            boxes.BeginFull("stsd", 0, 0);
            boxes.UInt32(1);
            boxes.Begin("avc1");
            boxes.Zeros(6);
            boxes.UInt16(1);
            boxes.Zeros(16);
            boxes.UInt16((ushort)width);
            boxes.UInt16((ushort)height);
            boxes.UInt32(0x00480000);
            boxes.UInt32(0x00480000);
            boxes.UInt32(0);
            boxes.UInt16(1);
            boxes.Zeros(32);
            boxes.UInt16(0x0018);
            boxes.UInt16(0xFFFF);
            boxes.Begin("avcC");
            boxes.Bytes(decoderSettings);
            boxes.End();
            // BT.709 colour in the limited range, the way the capture converts it.
            boxes.Begin("colr");
            boxes.Type("nclx");
            boxes.UInt16(1);
            boxes.UInt16(1);
            boxes.UInt16(1);
            boxes.Byte(0);
            boxes.End();
            boxes.End();
            boxes.End();
            boxes.BeginFull("stts", 0, 0);
            boxes.UInt32(0);
            boxes.End();
            boxes.BeginFull("stsc", 0, 0);
            boxes.UInt32(0);
            boxes.End();
            boxes.BeginFull("stsz", 0, 0);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.End();
            boxes.BeginFull("stco", 0, 0);
            boxes.UInt32(0);
            boxes.End();
            boxes.End();
            boxes.End();
            boxes.End();
            boxes.End();

            boxes.Begin("mvex");
            boxes.BeginFull("mehd", 1, 0);
            durationValueOffset = boxes.Count;
            boxes.UInt64(0);
            boxes.End();
            boxes.BeginFull("trex", 0, 0);
            boxes.UInt32(TrackId);
            boxes.UInt32(1);
            boxes.UInt32((uint)frameDurationMs);
            boxes.UInt32(0);
            boxes.UInt32(0);
            boxes.End();
            boxes.End();
            boxes.End();
            return boxes.ToArray();
        }

        // The start of the first child box of this type between start and end, or -1; its end in childEnd.
        private static int FindChild(byte[] bytes, int start, int end, string type, out int childEnd)
        {
            childEnd = 0;
            int position = start;
            while (position < end && ReadBoxHead(bytes, position, end, out string childType, out long size))
            {
                if (childType == type)
                {
                    childEnd = (int)(position + size);
                    return position;
                }
                position += (int)size;
            }
            return -1;
        }

        // A box's type and whole size; false when it does not fit between position and end.
        private static bool ReadBoxHead(byte[] bytes, int position, int end, out string type, out long size)
        {
            type = null;
            size = 0;
            if (position + 8 > end)
                return false;
            size = (long)ReadBigEndian(bytes, position, 4);
            type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            return size >= 8 && position + size <= end;
        }

        private static void WriteBigEndian(byte[] bytes, int position, ulong value, int width)
        {
            for (int index = 0; index < width; index++)
                bytes[position + index] = (byte)(value >> (8 * (width - 1 - index)));
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

        /// <summary>Builds boxes: each is opened, filled and closed, and closing writes its size at its start.</summary>
        private sealed class Boxes
        {
            private readonly List<byte> _bytes = new List<byte>();
            private readonly Stack<int> _open = new Stack<int>();

            public int Count => _bytes.Count;

            public void Begin(string type)
            {
                _open.Push(_bytes.Count);
                UInt32(0);
                Type(type);
            }

            /// <summary>A box that starts with a version and flags.</summary>
            public void BeginFull(string type, byte version, uint flags)
            {
                Begin(type);
                UInt32(((uint)version << 24) | flags);
            }

            public void End()
            {
                int start = _open.Pop();
                uint size = (uint)(_bytes.Count - start);
                _bytes[start] = (byte)(size >> 24);
                _bytes[start + 1] = (byte)(size >> 16);
                _bytes[start + 2] = (byte)(size >> 8);
                _bytes[start + 3] = (byte)size;
            }

            public void Byte(byte value) => _bytes.Add(value);

            public void UInt16(ushort value)
            {
                _bytes.Add((byte)(value >> 8));
                _bytes.Add((byte)value);
            }

            public void UInt32(uint value)
            {
                for (int shift = 24; shift >= 0; shift -= 8)
                    _bytes.Add((byte)(value >> shift));
            }

            public void UInt64(ulong value)
            {
                for (int shift = 56; shift >= 0; shift -= 8)
                    _bytes.Add((byte)(value >> shift));
            }

            public void Type(string type) => _bytes.AddRange(Encoding.ASCII.GetBytes(type));

            /// <summary>Text ending in a zero byte.</summary>
            public void Text(string text)
            {
                _bytes.AddRange(Encoding.ASCII.GetBytes(text));
                _bytes.Add(0);
            }

            public void Bytes(byte[] bytes) => _bytes.AddRange(bytes);

            public void Zeros(int count)
            {
                for (int index = 0; index < count; index++)
                    _bytes.Add(0);
            }

            /// <summary>The picture is shown as it is: no rotation, no scaling.</summary>
            public void Matrix()
            {
                UInt32(0x00010000);
                UInt32(0);
                UInt32(0);
                UInt32(0);
                UInt32(0x00010000);
                UInt32(0);
                UInt32(0);
                UInt32(0);
                UInt32(0x40000000);
            }

            public byte[] ToArray() => _bytes.ToArray();
        }
    }
}
