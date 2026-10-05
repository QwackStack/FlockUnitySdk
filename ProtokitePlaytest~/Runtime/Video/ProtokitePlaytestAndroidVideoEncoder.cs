using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Protokite.Playtest
{
    /// <summary>Records through the phone's own H.264 encoder (MediaCodec from C#, nothing native shipped): NV12 in, units out as the MP4 file stores them.</summary>
    internal sealed class ProtokitePlaytestAndroidVideoEncoder : IProtokitePlaytestVideoEncoder
    {
        private const long MicrosecondsPerMillisecond = 1000;
        private static readonly TimeSpan LongestWaitForAnInputBuffer = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LongestWaitForTheLastFrames = TimeSpan.FromSeconds(3);

        private readonly ProtokitePlaytestMakeAndroidCodec _makeCodec;
        private ProtokitePlaytestVideoEncoderSettings _settings;
        private IProtokitePlaytestAndroidCodec _codec;
        private bool _started;
        private bool _endOfStreamHandedIn;
        private bool _endOfStreamCameOut;
        private int _stride;
        private int _sliceHeight;
        private byte[] _sequenceSettings;
        private byte[] _pictureSettings;
        private bool _decoderSettingsHandedOver;
        // The settings the video's file holds, which a stream started after the background must match.
        private byte[] _settingsInTheVideo;
        private bool _newStreamToCheck;
        private long _lastTimestampUs = -1;

        /// <summary>An encoder for the phone's encoder the check chose.</summary>
        public ProtokitePlaytestAndroidVideoEncoder(ProtokitePlaytestEncoderFound found)
            : this(found, (out string whyNot) => ProtokitePlaytestMediaCodec.Create(found.Name, out whyNot))
        {
        }

        /// <summary>An encoder over a codec of the caller's making, so a test can stand in for the phone's.</summary>
        internal ProtokitePlaytestAndroidVideoEncoder(ProtokitePlaytestEncoderFound found, ProtokitePlaytestMakeAndroidCodec makeCodec)
        {
            Found = found;
            _makeCodec = makeCodec;
        }

        /// <summary>The encoder the check found, with what it takes.</summary>
        internal ProtokitePlaytestEncoderFound Found { get; }

        public ProtokitePlaytestPixelFormat InputPixelFormat => ProtokitePlaytestPixelFormat.Nv12;

        public string Description { get; private set; }

        public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error)
        {
            if (settings == null || settings.Width <= 0 || settings.Height <= 0 || settings.Width % 2 != 0 || settings.Height % 2 != 0
                || settings.FramesPerSecond <= 0 || settings.BitrateKbps <= 0)
            {
                error = "the video settings are out of range.";
                return false;
            }
            _settings = settings;
            error = null;
            return true;
        }

        public bool Start(out string error)
        {
            if (_started)
            {
                error = null;
                return true;
            }
            if (_settings == null)
            {
                error = "the encoder was started before it was configured.";
                return false;
            }
            try
            {
                return StartTheCodec(out error);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                error = "Android's media library, which encodes the video, could not be reached on this phone: " + ex.Message;
                return false;
            }
        }

        private bool StartTheCodec(out string error)
        {
            // A start that failed left its codec configured or half made; it is given back before another is made.
            _codec?.Dispose();
            _codec = _makeCodec(out error);
            if (_codec == null)
            {
                error = error ?? $"the phone would not create its encoder {Found.Name}.";
                return false;
            }
            int status = _codec.Configure(_settings.Width, _settings.Height, _settings.FramesPerSecond, _settings.BitrateKbps * 1000,
                Math.Max(1, _settings.KeyframeIntervalSeconds), Found.TakesConstantBitrate);
            if (status != 0)
            {
                error = $"{Found} refused {_settings.Width}x{_settings.Height} at {_settings.FramesPerSecond} frames a second and {_settings.BitrateKbps} kbps (status {status}).";
                return false;
            }
            _codec.ReadInputLayout(out int stride, out int sliceHeight);
            // A codec that does not say takes the frame packed; one that asks for less than the picture is not believed.
            _stride = Math.Max(stride, _settings.Width);
            _sliceHeight = Math.Max(sliceHeight, _settings.Height);
            status = _codec.Start();
            if (status != 0)
            {
                error = $"{Found} would not start (status {status}).";
                return false;
            }
            _started = true;
            Description = Found.ToString();
            error = null;
            return true;
        }

        public bool Encode(byte[] pixels, long timestampMs, long durationMs, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (!_started && !Start(out error))
                return false;
            if (_endOfStreamHandedIn)
            {
                error = "the encoder was given a frame after it finished.";
                return false;
            }
            long frameBytes = ProtokitePlaytestNv12.FrameLength(_settings.Width, _settings.Height);
            if (pixels == null || pixels.Length < frameBytes)
            {
                error = $"a frame held {pixels?.Length ?? 0} bytes where {frameBytes} were expected.";
                return false;
            }
            long timestampUs = timestampMs * MicrosecondsPerMillisecond;
            if (timestampUs <= _lastTimestampUs)
            {
                error = $"a frame at {timestampMs} ms came after one at {_lastTimestampUs / MicrosecondsPerMillisecond} ms.";
                return false;
            }
            if (!TakeAnInputBuffer(output, out long index, out error))
                return false;
            IntPtr buffer = _codec.GetInputBuffer(index, out long capacity);
            int used = CopyFrame(pixels, buffer, capacity, out error);
            if (used < 0)
                return false;
            int status = _codec.QueueInputBuffer(index, used, timestampUs, 0);
            if (status != 0)
            {
                error = $"{Found} refused a frame (status {status}).";
                return false;
            }
            _lastTimestampUs = timestampUs;
            return TakeWaitingOutput(output, 0, out error);
        }

        // What the encoder has finished is taken while waiting, so an encoder holding every buffer still moves.
        private bool TakeAnInputBuffer(List<ProtokitePlaytestEncodedFrame> output, out long index, out string error)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                index = _codec.DequeueInputBuffer(10000);
                if (index >= 0)
                {
                    error = null;
                    return true;
                }
                if (index != ProtokitePlaytestMediaCodec.TryAgainLater)
                {
                    error = $"{Found} failed handing out a buffer for a frame (status {index}).";
                    return false;
                }
                if (!TakeWaitingOutput(output, 0, out error))
                    return false;
                if (waited.Elapsed > LongestWaitForAnInputBuffer)
                {
                    error = $"{Found} took no frame for {LongestWaitForAnInputBuffer.TotalSeconds:0} seconds.";
                    return false;
                }
            }
        }

        // NV12 into the layout the encoder asked for: each row at its stride, the second plane after the first plane's rows.
        private int CopyFrame(byte[] pixels, IntPtr buffer, long capacity, out string error)
        {
            int width = _settings.Width;
            int height = _settings.Height;
            int chromaRows = (height + 1) / 2;
            long secondPlane = (long)_stride * _sliceHeight;
            long needed = secondPlane + (long)_stride * (chromaRows - 1) + width;
            if (buffer == IntPtr.Zero || needed > capacity)
            {
                error = $"{Found} handed out a buffer of {capacity} bytes for a frame that needs {needed}.";
                return -1;
            }
            if (_stride == width && _sliceHeight == height)
            {
                Marshal.Copy(pixels, 0, buffer, (int)ProtokitePlaytestNv12.FrameLength(width, height));
            }
            else
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * width, new IntPtr(buffer.ToInt64() + (long)row * _stride), width);
                for (int row = 0; row < chromaRows; row++)
                    Marshal.Copy(pixels, width * height + row * width, new IntPtr(buffer.ToInt64() + secondPlane + (long)row * _stride), width);
            }
            error = null;
            return (int)Math.Min(capacity, secondPlane + (long)_stride * chromaRows);
        }

        // Takes what the encoder has finished, waiting up to the timeout for the first of it.
        private bool TakeWaitingOutput(List<ProtokitePlaytestEncodedFrame> output, long waitMicroseconds, out string error)
        {
            long wait = waitMicroseconds;
            while (!_endOfStreamCameOut)
            {
                long index = _codec.DequeueOutputBuffer(wait, out ProtokitePlaytestCodecBuffer buffer);
                wait = 0;
                if (index == ProtokitePlaytestMediaCodec.TryAgainLater)
                    break;
                if (index == ProtokitePlaytestMediaCodec.OutputFormatChanged)
                {
                    ReadSettingsFromTheOutputFormat();
                    continue;
                }
                if (index == ProtokitePlaytestMediaCodec.OutputBuffersChanged)
                    continue;
                if (index < 0)
                {
                    error = $"{Found} failed handing a frame back (status {index}).";
                    return false;
                }
                bool taken;
                try
                {
                    taken = TakeOneOutput(index, buffer, output, out error);
                }
                finally
                {
                    _codec.ReleaseOutputBuffer(index);
                }
                if (!taken)
                    return false;
                if ((buffer.Flags & ProtokitePlaytestMediaCodec.EndOfStreamFlag) != 0)
                    _endOfStreamCameOut = true;
            }
            error = null;
            return true;
        }

        private bool TakeOneOutput(long index, ProtokitePlaytestCodecBuffer buffer, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            error = null;
            if (buffer.Size <= 0)
                return true;
            IntPtr data = _codec.GetOutputBuffer(index, out long capacity);
            if (data == IntPtr.Zero || buffer.Offset < 0 || (long)buffer.Offset + buffer.Size > capacity)
            {
                error = $"{Found} handed back a frame outside its buffer.";
                return false;
            }
            byte[] bytes = new byte[buffer.Size];
            Marshal.Copy(new IntPtr(data.ToInt64() + buffer.Offset), bytes, 0, buffer.Size);
            // Units in another form would split into nothing, and every frame would be lost without a word.
            if (!ProtokitePlaytestH264.StartsWithAStartCode(bytes, bytes.Length))
            {
                error = "it handed back frames in a form other than an H.264 byte stream, so they cannot be stored.";
                return false;
            }
            List<ArraySegment<byte>> units = ProtokitePlaytestH264.SplitUnits(bytes, bytes.Length);
            bool holdsAPicture = false;
            foreach (ArraySegment<byte> unit in units)
            {
                int type = ProtokitePlaytestH264.UnitType(unit);
                if (type == ProtokitePlaytestH264.SequenceSettingsUnit)
                    _sequenceSettings = Copy(unit);
                else if (type == ProtokitePlaytestH264.PictureSettingsUnit)
                    _pictureSettings = Copy(unit);
                else if (ProtokitePlaytestH264.IsPicture(unit))
                    holdsAPicture = true;
            }
            // The stream's settings come on their own before the first frame (a buffer with no picture); they go out with it.
            if (!holdsAPicture)
                return true;

            byte[] decoderSettings = null;
            bool keyframe = (buffer.Flags & ProtokitePlaytestMediaCodec.KeyframeFlag) != 0 || ProtokitePlaytestH264.HoldsKeyframe(units);
            if (!_decoderSettingsHandedOver)
            {
                decoderSettings = ProtokitePlaytestH264.DecoderSettingsRecord(_sequenceSettings, _pictureSettings);
                if (decoderSettings == null)
                {
                    error = "it gave no sequence and picture settings with its first frame, so no player could decode the video.";
                    return false;
                }
                _decoderSettingsHandedOver = true;
                _settingsInTheVideo = decoderSettings;
            }
            else if (_newStreamToCheck)
            {
                // The file describes its stream once, in its header: frames of a stream described otherwise would not decode.
                byte[] again = ProtokitePlaytestH264.DecoderSettingsRecord(_sequenceSettings, _pictureSettings);
                if (!keyframe || again == null || !again.SequenceEqual(_settingsInTheVideo))
                {
                    error = "it came back from the background with " + (keyframe ? "stream settings other than the video's" : "no keyframe to start from") +
                            ", so the video ends where the game left.";
                    return false;
                }
                _newStreamToCheck = false;
            }
            long timestampMs = (buffer.PresentationTimeUs + MicrosecondsPerMillisecond / 2) / MicrosecondsPerMillisecond;
            output.Add(new ProtokitePlaytestEncodedFrame(ProtokitePlaytestH264.FrameAsStored(units), timestampMs, keyframe, decoderSettings));
            return true;
        }

        // Some encoders give the stream's settings on their output format instead of in a buffer of their own.
        private void ReadSettingsFromTheOutputFormat()
        {
            foreach (byte[] settings in _codec.SettingsOnTheOutputFormat())
            {
                foreach (ArraySegment<byte> unit in ProtokitePlaytestH264.SplitUnits(settings, settings.Length))
                {
                    int type = ProtokitePlaytestH264.UnitType(unit);
                    if (type == ProtokitePlaytestH264.SequenceSettingsUnit && _sequenceSettings == null)
                        _sequenceSettings = Copy(unit);
                    else if (type == ProtokitePlaytestH264.PictureSettingsUnit && _pictureSettings == null)
                        _pictureSettings = Copy(unit);
                }
            }
        }

        public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (!_started || _endOfStreamCameOut)
            {
                error = null;
                return true;
            }
            if (!_endOfStreamHandedIn)
            {
                if (!TakeAnInputBuffer(output, out long index, out error))
                    return false;
                int status = _codec.QueueInputBuffer(index, 0, Math.Max(0L, _lastTimestampUs), ProtokitePlaytestMediaCodec.EndOfStreamFlag);
                if (status != 0)
                {
                    error = $"{Found} refused the end of the video (status {status}).";
                    return false;
                }
                _endOfStreamHandedIn = true;
            }
            Stopwatch waited = Stopwatch.StartNew();
            while (!_endOfStreamCameOut)
            {
                if (!TakeWaitingOutput(output, 10000, out error))
                    return false;
                if (waited.Elapsed > LongestWaitForTheLastFrames)
                {
                    error = $"{Found} did not hand back its last frames within {LongestWaitForTheLastFrames.TotalSeconds:0} seconds.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        // The phone's encoder is given back while the game is away, so another app may have it, and Android has none to take back from
        // the game. The stream is ended first, so every frame it held comes out; the next frame starts a new one on a keyframe.
        public bool HandOverEverythingAndLetGo(List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (!Finish(output, out error))
                return false;
            LetGoOfTheCodec();
            _endOfStreamHandedIn = false;
            _endOfStreamCameOut = false;
            _sequenceSettings = null;
            _pictureSettings = null;
            _newStreamToCheck = _decoderSettingsHandedOver;
            return true;
        }

        public void Dispose() => LetGoOfTheCodec();

        private void LetGoOfTheCodec()
        {
            if (_codec == null)
                return;
            if (_started)
                _codec.Stop();
            _codec.Dispose();
            _codec = null;
            _started = false;
        }

        private static byte[] Copy(ArraySegment<byte> unit)
        {
            byte[] copy = new byte[unit.Count];
            Array.Copy(unit.Array, unit.Offset, copy, 0, unit.Count);
            return copy;
        }
    }
}
