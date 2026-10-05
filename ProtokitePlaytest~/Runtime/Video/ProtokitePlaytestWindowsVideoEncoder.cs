#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using static Protokite.Playtest.ProtokitePlaytestMediaFoundation;

namespace Protokite.Playtest
{
    /// <summary>H.264 through Windows' Media Foundation on the graphics card's video engine (the software encoder only when allowed), started and used on the encoding thread alone.</summary>
    internal sealed class ProtokitePlaytestWindowsVideoEncoder : IProtokitePlaytestVideoEncoder
    {
        /// <summary>How long a frame may wait for the encoder before the recording gives up on it.</summary>
        internal const int LongestWaitMs = 10000;

        private const long HundredNanosecondsPerMillisecond = 10000;
        // A stream change or a frame held for later never needs more than a few rounds; more means the encoder is stuck.
        private const int MostOutputRounds = 64;

        private ProtokitePlaytestVideoEncoderSettings _settings;
        private int _startedOnThread;
        private bool _comStarted;
        private bool _mediaFoundationStarted;
        private IMFActivate _activate;
        private IMFTransform _transform;
        private IMFMediaEventGenerator _events;
        private bool _asynchronous;
        private bool _providesSamples;
        private uint _outputBufferBytes;
        private int _inputsWanted;
        private bool _drained;
        private byte[] _sequenceSettings;
        private byte[] _pictureSettings;
        private bool _decoderSettingsHandedOver;
        private readonly Queue<long> _timesHandedIn = new Queue<long>();
        private string _failure;
        private bool _finished;
        private bool _disposed;

        /// <summary>Lets a test or probe keep only some of the encoders Windows offers, to drive one of them.</summary>
        internal Func<ProtokitePlaytestEncoderFound, bool> EncoderAllowedForTesting;

        /// <summary>Stands in for Windows' encoder, so a test can act out one that behaves as no encoder on this PC does; set up like a real one.</summary>
        internal Func<IMFTransform> TransformForTesting;

        public ProtokitePlaytestPixelFormat InputPixelFormat => ProtokitePlaytestPixelFormat.Nv12;

        public string Description { get; private set; }

        /// <summary>The codec settings the encoder turned down, for the log; empty when it took them all.</summary>
        internal readonly List<string> SettingsRefused = new List<string>();

        /// <summary>Whether the encoder in use hands frames back through events, as graphics card encoders do.</summary>
        internal bool IsAsynchronous => _asynchronous;

        public bool Configure(ProtokitePlaytestVideoEncoderSettings settings, out string error)
        {
            error = _settings != null ? "the encoder is already configured."
                : settings.Width < 16 || settings.Height < 16 || settings.Width % 2 != 0 || settings.Height % 2 != 0
                    ? $"a {settings.Width}x{settings.Height} video cannot be encoded: each side must be even and at least 16."
                : settings.FramesPerSecond < 1 ? "a video needs at least one frame a second."
                : settings.BitrateKbps < 1 ? "a video needs a bitrate."
                : null;
            if (error != null)
                return false;
            _settings = settings;
            return true;
        }

        public bool Start(out string error)
        {
            if (!Usable(out error))
                return false;
            return _transform != null || StartTheEncoder(out error) || Fail(error);
        }

        public bool Encode(byte[] pixels, long timestampMs, long durationMs, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (!Usable(out error))
                return false;
            long frameBytes = ProtokitePlaytestNv12.FrameLength(_settings.Width, _settings.Height);
            if (pixels == null || pixels.LongLength != frameBytes)
            {
                error = $"a {_settings.Width}x{_settings.Height} frame is {frameBytes} bytes, not {pixels?.LongLength ?? 0}.";
                return false;
            }
            if (_transform == null && !StartTheEncoder(out error))
                return Fail(error);
            if (!MakeSample(pixels, timestampMs, durationMs, out IMFSample sample, out error))
                return Fail(error);
            try
            {
                if (_asynchronous && !WaitForEvents(Waiting.UntilAFrameIsWanted, output, out error))
                    return Fail(error);
                int result = _transform.ProcessInput(0, sample, 0);
                if (result < 0)
                    return Fail($"it refused a frame ({Hex(result)}).");
                _timesHandedIn.Enqueue(timestampMs);
                if (_asynchronous)
                {
                    _inputsWanted--;
                    if (!WaitForEvents(Waiting.No, output, out error))
                        return Fail(error);
                }
                else if (!TakeSynchronousOutput(output, out error))
                {
                    return Fail(error);
                }
                return true;
            }
            finally
            {
                Release(sample);
            }
        }

        public bool Finish(List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            if (!Usable(out error))
                return false;
            _finished = true;
            // Never started: no frame was encoded, so nothing is held.
            if (_transform == null)
                return true;
            _transform.ProcessMessage(MessageEndOfStream, IntPtr.Zero);
            int result = _transform.ProcessMessage(MessageCommandDrain, IntPtr.Zero);
            if (result < 0)
                return Fail($"it would not hand over the frames it held ({Hex(result)}).");
            bool drained = _asynchronous ? WaitForEvents(Waiting.UntilDrained, output, out error) : TakeSynchronousOutput(output, out error);
            return drained || Fail(error);
        }

        // A desktop game keeps its encoder: Windows neither ends a game in the background nor takes its encoder back.
        public bool HandOverEverythingAndLetGo(List<ProtokitePlaytestEncodedFrame> output, out string error) => Usable(out error);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_transform != null)
                    _transform.ProcessMessage(MessageEndStreaming, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
            LetGoOfTheEncoder();
            if (_mediaFoundationStarted)
                MFShutdown();
            _mediaFoundationStarted = false;
            // COM is let go on the thread that started it, which is the one that encoded.
            if (_comStarted && Thread.CurrentThread.ManagedThreadId == _startedOnThread)
                CoUninitialize();
            _comStarted = false;
        }

        private bool Usable(out string error)
        {
            error = _disposed ? "the encoder is closed."
                : _settings == null ? "the encoder is not configured."
                : _failure != null ? "the encoder failed earlier: " + _failure
                : _finished ? "the encoder has finished."
                : null;
            return error == null;
        }

        private bool Fail(string error)
        {
            _failure = error;
            return false;
        }

        // Starts Media Foundation on this thread and the first encoder Windows offers that takes the settings, the game's graphics card maker's first.
        private bool StartTheEncoder(out string error)
        {
            _startedOnThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                _comStarted = StartCom();
                int result = MFStartup(MediaFoundationVersion, StartupFull);
                if (result < 0)
                {
                    error = $"Windows' Media Foundation would not start ({Hex(result)}).";
                    return false;
                }
                _mediaFoundationStarted = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                error = ProtokitePlaytestVideoEncoders.MediaFoundationMissing;
                return false;
            }
            if (TransformForTesting != null)
                return StartTheStandIn(out error);

            List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>> offered = Offered(_settings.AllowSoftwareEncoder, _settings.GraphicsCardVendorId);
            List<string> refused = new List<string>();
            int tried = 0;
            foreach (KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound> encoder in offered)
            {
                if (_transform != null || (EncoderAllowedForTesting != null && !EncoderAllowedForTesting(encoder.Value)))
                {
                    Release(encoder.Key);
                    continue;
                }
                tried++;
                bool started;
                string why;
                try
                {
                    started = TryStart(encoder.Key, out why);
                }
                catch (Exception ex)
                {
                    // An encoder that throws is one more that would not take the video; the next one is tried.
                    started = false;
                    why = ex.Message;
                }
                if (started)
                {
                    _activate = encoder.Key;
                    Description = encoder.Value.ToString();
                    continue;
                }
                refused.Add($"{encoder.Value.Name}: {why}");
                DropTheTransform();
                ShutDown(encoder.Key);
            }
            if (_transform != null)
            {
                error = null;
                return true;
            }
            error = tried == 0
                ? (_settings.AllowSoftwareEncoder ? ProtokitePlaytestVideoEncoders.NoEncoderAtAll : ProtokitePlaytestVideoEncoders.NoGraphicsCardEncoder)
                : "no H.264 encoder on this PC would take the video: " + string.Join("; ", refused);
            return false;
        }

        private bool TryStart(IMFActivate activate, out string error)
        {
            Guid transformInterface = TransformInterface;
            int result = activate.ActivateObject(ref transformInterface, out IntPtr created);
            if (result < 0)
            {
                error = $"it could not be created ({Hex(result)}).";
                return false;
            }
            _transform = TakeObject<IMFTransform>(created);
            return SetUpTheTransform(out error);
        }

        // Started as each real encoder is: named only once it runs, and an exception is one more refusal.
        private bool StartTheStandIn(out string error)
        {
            _transform = TransformForTesting();
            bool started;
            try
            {
                started = SetUpTheTransform(out error);
            }
            catch (Exception ex)
            {
                started = false;
                error = ex.Message;
            }
            if (started)
            {
                Description = "a stand-in encoder";
                return true;
            }
            DropTheTransform();
            return false;
        }

        // Unlocks an encoder that hands frames back through events, then gives it the settings and types and starts it streaming.
        private bool SetUpTheTransform(out string error)
        {
            int result;
            SettingsRefused.Clear();

            IMFAttributes attributes = null;
            try
            {
                if (_transform.GetAttributes(out attributes) < 0)
                    attributes = null;
                Guid key = TransformAsync;
                _asynchronous = attributes != null && attributes.GetUINT32(ref key, out uint asynchronous) >= 0 && asynchronous != 0;
                if (_asynchronous)
                {
                    key = TransformAsyncUnlock;
                    result = attributes.SetUINT32(ref key, 1);
                    if (result < 0)
                    {
                        error = $"it could not be unlocked for use ({Hex(result)}).";
                        return false;
                    }
                    _events = _transform as IMFMediaEventGenerator;
                    if (_events == null)
                    {
                        error = "it hands frames back through events, and gives none.";
                        return false;
                    }
                }
                key = LowLatency;
                if (attributes != null && attributes.SetUINT32(ref key, 1) < 0)
                    SettingsRefused.Add("low latency (attribute)");
            }
            finally
            {
                Release(attributes);
            }

            SetCodecValues();
            if (!SetTypes(out error))
                return false;

            if (!ReadOutputStreamInfo(out error))
                return false;

            result = _transform.ProcessMessage(MessageBeginStreaming, IntPtr.Zero);
            if (result < 0)
            {
                error = $"it would not start (is another program using it?) ({Hex(result)}).";
                return false;
            }
            result = _transform.ProcessMessage(MessageStartOfStream, IntPtr.Zero);
            if (result < 0 && _asynchronous)
            {
                error = $"it would not start a stream ({Hex(result)}).";
                return false;
            }
            error = null;
            return true;
        }

        // One keyframe every interval, no frames held back to be shown out of order, and a steady bitrate; each refusal is noted, not fatal.
        private void SetCodecValues()
        {
            ICodecAPI codec = _transform as ICodecAPI;
            if (codec == null)
            {
                SettingsRefused.Add("all codec settings (none can be set)");
                return;
            }
            NoteRefusal(SetCodecValue(codec, CodecRateControlMode, RateControlConstantBitrate, false), "constant bitrate");
            NoteRefusal(SetCodecValue(codec, CodecMeanBitrate, (uint)_settings.BitrateKbps * 1000, false), "bitrate");
            NoteRefusal(SetCodecValue(codec, CodecKeyframeInterval, (uint)Math.Max(1, _settings.KeyframeIntervalSeconds * _settings.FramesPerSecond), false), "keyframe interval");
            NoteRefusal(SetCodecValue(codec, CodecBFrameCount, 0, false), "no frames out of order");
            NoteRefusal(SetCodecValue(codec, CodecLowLatency, 1, true), "low latency");
        }

        private void NoteRefusal(int result, string setting)
        {
            if (result < 0)
                SettingsRefused.Add($"{setting} ({Hex(result)})");
        }

        // The output first, as encoders ask: H.264 High profile, or Main where High is refused; then NV12 in.
        private bool SetTypes(out string error)
        {
            int result = SetOutputType(H264ProfileHigh);
            if (result < 0)
                result = SetOutputType(H264ProfileMain);
            if (result < 0)
            {
                error = $"it would not make H.264 at {_settings.Width}x{_settings.Height}, {_settings.FramesPerSecond} frames a second ({Hex(result)}).";
                return false;
            }
            IMFMediaType input = VideoType(Nv12);
            try
            {
                result = _transform.SetInputType(0, input, 0);
            }
            finally
            {
                Release(input);
            }
            if (result < 0)
            {
                error = $"it would not take NV12 frames of {_settings.Width}x{_settings.Height} ({Hex(result)}).";
                return false;
            }
            error = null;
            return true;
        }

        private int SetOutputType(uint profile)
        {
            IMFMediaType output = VideoType(H264);
            try
            {
                Guid key = AverageBitrate;
                output.SetUINT32(ref key, (uint)_settings.BitrateKbps * 1000);
                key = Profile;
                output.SetUINT32(ref key, profile);
                return _transform.SetOutputType(0, output, 0);
            }
            finally
            {
                Release(output);
            }
        }

        // The colour description goes into the stream, so a player decodes with the matrix the capture converted with.
        private IMFMediaType VideoType(Guid subtype)
        {
            int result = MFCreateMediaType(out IMFMediaType type);
            if (result < 0)
                throw new InvalidOperationException($"Media Foundation could not describe a video ({Hex(result)}).");
            Guid key = MajorType;
            Guid video = Video;
            type.SetGUID(ref key, ref video);
            key = Subtype;
            type.SetGUID(ref key, ref subtype);
            key = FrameSize;
            type.SetUINT64(ref key, ((ulong)_settings.Width << 32) | (uint)_settings.Height);
            key = FrameRate;
            type.SetUINT64(ref key, ((ulong)_settings.FramesPerSecond << 32) | 1);
            key = PixelAspectRatio;
            type.SetUINT64(ref key, (1UL << 32) | 1);
            key = InterlaceMode;
            type.SetUINT32(ref key, InterlaceProgressive);
            key = YuvMatrix;
            type.SetUINT32(ref key, MatrixBt709);
            key = NominalRange;
            type.SetUINT32(ref key, Range16To235);
            key = Primaries;
            type.SetUINT32(ref key, PrimariesBt709);
            key = TransferFunction;
            type.SetUINT32(ref key, TransferBt709);
            return type;
        }

        private bool MakeSample(byte[] pixels, long timestampMs, long durationMs, out IMFSample sample, out string error)
        {
            sample = null;
            int result = MFCreateMemoryBuffer((uint)pixels.Length, out IMFMediaBuffer buffer);
            if (result < 0)
            {
                error = $"Media Foundation could not hold a frame ({Hex(result)}).";
                return false;
            }
            try
            {
                result = buffer.Lock(out IntPtr memory, out uint _, out uint _);
                if (result < 0)
                {
                    error = $"a frame's memory could not be written ({Hex(result)}).";
                    return false;
                }
                Marshal.Copy(pixels, 0, memory, pixels.Length);
                buffer.Unlock();
                buffer.SetCurrentLength((uint)pixels.Length);
                result = MFCreateSample(out IntPtr created);
                if (result < 0)
                {
                    error = $"Media Foundation could not make a frame ({Hex(result)}).";
                    return false;
                }
                sample = TakeObject<IMFSample>(created);
                sample.AddBuffer(buffer);
                sample.SetSampleTime(timestampMs * HundredNanosecondsPerMillisecond);
                sample.SetSampleDuration(durationMs * HundredNanosecondsPerMillisecond);
                error = null;
                return true;
            }
            finally
            {
                Release(buffer);
            }
        }

        private enum Waiting
        {
            No,
            UntilAFrameIsWanted,
            UntilDrained
        }

        // An encoder on the graphics card asks for each frame and says when one is ready, through events; this takes them until the wait is over.
        private bool WaitForEvents(Waiting waiting, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            Stopwatch quiet = null;
            while (true)
            {
                if ((waiting == Waiting.UntilAFrameIsWanted && _inputsWanted > 0) || (waiting == Waiting.UntilDrained && _drained))
                {
                    error = null;
                    return true;
                }
                int result = _events.GetEvent(EventNoWait, out IMFMediaEvent mediaEvent);
                if (result == NoEventsAvailable)
                {
                    if (waiting == Waiting.No)
                    {
                        error = null;
                        return true;
                    }
                    quiet = quiet ?? Stopwatch.StartNew();
                    if (quiet.ElapsedMilliseconds > LongestWaitMs)
                    {
                        error = $"it stopped answering for {LongestWaitMs / 1000} seconds.";
                        return false;
                    }
                    Thread.Sleep(1);
                    continue;
                }
                if (result < 0)
                {
                    error = $"its events could not be read ({Hex(result)}).";
                    return false;
                }
                quiet = null;
                uint type;
                int status;
                try
                {
                    mediaEvent.GetEventType(out type);
                    mediaEvent.GetStatus(out status);
                }
                finally
                {
                    Release(mediaEvent);
                }
                if (status < 0)
                {
                    error = $"it reported a failure ({Hex(status)}).";
                    return false;
                }
                if (type == EventNeedInput)
                    _inputsWanted++;
                else if (type == EventDrainComplete)
                    _drained = true;
                else if (type == EventHaveOutput && !TakeOneOutput(output, out _, out error))
                    return false;
            }
        }

        private bool TakeSynchronousOutput(List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            for (int round = 0; round < MostOutputRounds; round++)
            {
                if (!TakeOneOutput(output, out bool wantsMoreInput, out error))
                    return false;
                if (wantsMoreInput)
                    return true;
            }
            error = "it kept handing frames back without asking for more.";
            return false;
        }

        private bool TakeOneOutput(List<ProtokitePlaytestEncodedFrame> output, out bool wantsMoreInput, out string error)
        {
            wantsMoreInput = false;
            for (int round = 0; round < 3; round++)
            {
                IntPtr ourSamplePointer = IntPtr.Zero;
                IMFSample ourSample = null;
                if (!_providesSamples && !MakeEmptySample(out ourSamplePointer, out ourSample, out error))
                    return false;
                OutputDataBuffer buffer = new OutputDataBuffer { StreamId = 0, Sample = ourSamplePointer };
                int result = _transform.ProcessOutput(0, 1, ref buffer, out uint _);
                if (buffer.Events != IntPtr.Zero)
                    Marshal.Release(buffer.Events);
                IMFSample sample = _providesSamples ? TakeObject<IMFSample>(buffer.Sample) : ourSample;
                try
                {
                    if (result == StreamChange)
                    {
                        if (!TakeNewOutputType(out error))
                            return false;
                        // The change used up the event: an encoder that hands frames back through events sends another for the
                        // frame, and asking again before it refuses with E_UNEXPECTED (as Intel's evidently did on a laptop).
                        if (_asynchronous)
                        {
                            error = null;
                            return true;
                        }
                        continue;
                    }
                    if (result == NeedMoreInput)
                    {
                        wantsMoreInput = true;
                        error = null;
                        return true;
                    }
                    if (result < 0)
                    {
                        error = $"it could not hand a frame back ({Hex(result)}).";
                        return false;
                    }
                    return AddFrame(sample, output, out error);
                }
                finally
                {
                    Release(sample);
                    if (ourSamplePointer != IntPtr.Zero)
                        Marshal.Release(ourSamplePointer);
                }
            }
            error = "its output kept changing format.";
            return false;
        }

        // A sample with room for a whole frame, for an encoder that leaves providing them to its caller.
        private bool MakeEmptySample(out IntPtr pointer, out IMFSample sample, out string error)
        {
            pointer = IntPtr.Zero;
            sample = null;
            int result = MFCreateMemoryBuffer(_outputBufferBytes, out IMFMediaBuffer buffer);
            if (result < 0)
            {
                error = $"Media Foundation could not hold an encoded frame ({Hex(result)}).";
                return false;
            }
            try
            {
                result = MFCreateSample(out pointer);
                if (result < 0)
                {
                    error = $"Media Foundation could not make a frame ({Hex(result)}).";
                    return false;
                }
                // The pointer keeps its own reference for the encoder's call; the object is for reading the result back.
                sample = (IMFSample)Marshal.GetObjectForIUnknown(pointer);
                sample.AddBuffer(buffer);
                error = null;
                return true;
            }
            finally
            {
                Release(buffer);
            }
        }

        private bool TakeNewOutputType(out string error)
        {
            int result = _transform.GetOutputAvailableType(0, 0, out IMFMediaType type);
            if (result >= 0)
            {
                try
                {
                    result = _transform.SetOutputType(0, type, 0);
                }
                finally
                {
                    Release(type);
                }
            }
            if (result < 0)
            {
                error = $"its output changed format, and the new one could not be taken ({Hex(result)}).";
                return false;
            }
            // A new output type may want a bigger buffer, or samples of the encoder's own.
            return ReadOutputStreamInfo(out error);
        }

        // Whether the encoder hands back samples of its own, and how big a buffer one of ours must be for its frames.
        private bool ReadOutputStreamInfo(out string error)
        {
            int result = _transform.GetOutputStreamInfo(0, out OutputStreamInfo info);
            if (result < 0)
            {
                error = $"it would not say how it hands frames back ({Hex(result)}).";
                return false;
            }
            _providesSamples = (info.Flags & (OutputStreamProvidesSamples | OutputStreamCanProvideSamples)) != 0;
            _outputBufferBytes = Math.Max(info.Size, (uint)Math.Min(uint.MaxValue, ProtokitePlaytestNv12.FrameLength(_settings.Width, _settings.Height)));
            error = null;
            return true;
        }

        private bool AddFrame(IMFSample sample, List<ProtokitePlaytestEncodedFrame> output, out string error)
        {
            int result = sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            if (result < 0)
            {
                error = $"an encoded frame could not be read ({Hex(result)}).";
                return false;
            }
            byte[] bytes;
            try
            {
                result = buffer.Lock(out IntPtr memory, out uint _, out uint length);
                if (result < 0)
                {
                    error = $"an encoded frame could not be read ({Hex(result)}).";
                    return false;
                }
                bytes = new byte[length];
                Marshal.Copy(memory, bytes, 0, (int)length);
                buffer.Unlock();
            }
            finally
            {
                Release(buffer);
            }

            if (bytes.Length == 0)
            {
                error = null;
                return true;
            }
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
            error = null;
            // Settings on their own come before the first frame; they go out with it.
            if (!holdsAPicture)
                return true;

            byte[] decoderSettings = null;
            if (!_decoderSettingsHandedOver)
            {
                if (_sequenceSettings == null || _pictureSettings == null)
                    ReadSettingsFromTheOutputType();
                decoderSettings = ProtokitePlaytestH264.DecoderSettingsRecord(_sequenceSettings, _pictureSettings);
                if (decoderSettings == null)
                {
                    error = "it gave no sequence and picture settings with its first frame, so no player could decode the video.";
                    return false;
                }
                _decoderSettingsHandedOver = true;
            }
            // Frames come back in the order they went in; the time handed in is kept where the encoder gives none back.
            long handedIn = _timesHandedIn.Count > 0 ? _timesHandedIn.Dequeue() : -1;
            long timestampMs = sample.GetSampleTime(out long time) >= 0 ? (time + HundredNanosecondsPerMillisecond / 2) / HundredNanosecondsPerMillisecond : handedIn;
            output.Add(new ProtokitePlaytestEncodedFrame(ProtokitePlaytestH264.FrameAsStored(units), timestampMs, ProtokitePlaytestH264.HoldsKeyframe(units), decoderSettings));
            return true;
        }

        // Some encoders give the stream's settings on their output type instead of in the first frame.
        private void ReadSettingsFromTheOutputType()
        {
            if (_transform.GetOutputCurrentType(0, out IMFMediaType type) < 0)
                return;
            try
            {
                Guid key = SequenceHeader;
                if (type.GetAllocatedBlob(ref key, out IntPtr blob, out uint size) < 0)
                    return;
                byte[] header = new byte[size];
                Marshal.Copy(blob, header, 0, (int)size);
                Marshal.FreeCoTaskMem(blob);
                foreach (ArraySegment<byte> unit in ProtokitePlaytestH264.SplitUnits(header, header.Length))
                {
                    int unitType = ProtokitePlaytestH264.UnitType(unit);
                    if (unitType == ProtokitePlaytestH264.SequenceSettingsUnit && _sequenceSettings == null)
                        _sequenceSettings = Copy(unit);
                    else if (unitType == ProtokitePlaytestH264.PictureSettingsUnit && _pictureSettings == null)
                        _pictureSettings = Copy(unit);
                }
            }
            finally
            {
                Release(type);
            }
        }

        private static byte[] Copy(ArraySegment<byte> unit)
        {
            byte[] copy = new byte[unit.Count];
            Array.Copy(unit.Array, unit.Offset, copy, 0, unit.Count);
            return copy;
        }

        private void LetGoOfTheEncoder()
        {
            DropTheTransform();
            if (_activate != null)
                ShutDown(_activate);
            _activate = null;
        }

        private void DropTheTransform()
        {
            // A test's stand-in is not a COM object, and has nothing to let go.
            if (_transform != null && Marshal.IsComObject(_transform))
                Marshal.FinalReleaseComObject(_transform);
            _transform = null;
            _events = null;
        }

        // Shut down through the object that made the encoder, so a graphics card's encoding session ends with it.
        private static void ShutDown(IMFActivate activate)
        {
            try
            {
                activate.ShutdownObject();
            }
            catch (Exception)
            {
            }
            Release(activate);
        }

        /// <summary>Starts COM on this thread for Media Foundation; true when this call started it, so it is the one to stop it.</summary>
        internal static bool StartCom()
        {
            int result = CoInitializeEx(IntPtr.Zero, MultiThreadedApartment);
            return result == Ok || result == AlreadyStarted;
        }

        /// <summary>The H.264 encoders Windows offers for NV12, graphics card ones first (the game's card maker's leading), then the software one when allowed; the caller lets go of each.</summary>
        internal static List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>> Offered(bool includeSoftware, int preferredVendorId)
        {
            List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>> offered = Enumerate(EnumHardware | EnumSortAndFilter);
            List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>> preferred = offered.FindAll(encoder => preferredVendorId != 0 && encoder.Value.VendorId == preferredVendorId);
            preferred.AddRange(offered.FindAll(encoder => preferredVendorId == 0 || encoder.Value.VendorId != preferredVendorId));
            if (!includeSoftware)
                return preferred;
            foreach (KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound> encoder in Enumerate(EnumSynchronous | EnumSortAndFilter))
            {
                if (encoder.Value.InHardware)
                    Release(encoder.Key);
                else
                    preferred.Add(encoder);
            }
            return preferred;
        }

        private static List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>> Enumerate(uint flags)
        {
            List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>> found = new List<KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>>();
            RegisterTypeInfo input = new RegisterTypeInfo { MajorType = Video, Subtype = Nv12 };
            RegisterTypeInfo output = new RegisterTypeInfo { MajorType = Video, Subtype = H264 };
            if (MFTEnumEx(VideoEncoderCategory, flags, ref input, ref output, out IntPtr activates, out uint count) < 0 || activates == IntPtr.Zero)
                return found;
            try
            {
                for (int index = 0; index < count; index++)
                {
                    IMFActivate activate = TakeObject<IMFActivate>(Marshal.ReadIntPtr(activates, index * IntPtr.Size));
                    if (activate != null)
                        found.Add(new KeyValuePair<IMFActivate, ProtokitePlaytestEncoderFound>(activate, Describe(activate)));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(activates);
            }
            return found;
        }

        private static ProtokitePlaytestEncoderFound Describe(IMFActivate activate)
        {
            Guid key = HardwareUrl;
            ProtokitePlaytestEncoderFound found = new ProtokitePlaytestEncoderFound
            {
                Name = ReadText(activate, FriendlyName) ?? "an unnamed H.264 encoder",
                InHardware = activate.GetStringLength(ref key, out uint _) >= 0
            };
            // Windows says "VEN_10DE" for NVIDIA, the same number Unity reports as the graphics card's vendor ID.
            string vendor = ReadText(activate, HardwareVendorId);
            if (vendor != null && vendor.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(vendor.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vendorId))
                found.VendorId = vendorId;
            return found;
        }

        private static string ReadText(IMFActivate activate, Guid key)
        {
            if (activate.GetAllocatedString(ref key, out IntPtr text, out uint _) < 0 || text == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
    }
}
#endif
