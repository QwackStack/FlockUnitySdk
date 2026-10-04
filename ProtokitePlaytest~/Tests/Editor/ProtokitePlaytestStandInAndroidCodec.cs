using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// Stands in for a phone's H.264 encoder the way Android's MediaCodec behaves: two input buffers handed out and taken back,
    /// the stream's settings in a buffer of their own before the first frame (or only on a format change), frames that can be
    /// held until the end of the stream, and every call it was given kept for the test to read back.
    /// </summary>
    internal sealed class ProtokitePlaytestStandInAndroidCodec : IProtokitePlaytestAndroidCodec
    {
        // A High profile sequence settings unit and a picture settings unit, as a phone's encoder writes them.
        internal static readonly byte[] SequenceSettings = { 0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9, 0x40 };
        internal static readonly byte[] PictureSettings = { 0x68, 0xEB, 0xE3, 0xCB, 0x22, 0xC0 };
        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        private const int InputBuffers = 2;

        public int ConfigureStatus;
        public int StartStatus;
        public int Stride;
        public int SliceHeight;
        public bool SettingsInABufferOfTheirOwn = true;
        public bool SettingsOnlyOnTheOutputFormat;
        public bool SettingsInFrontOfTheFirstPicture;
        public int FramesHeldUntilTheEnd;
        public bool NeverFreesAnInputBuffer;
        public bool HandsBackNoStartCode;
        public bool HandsBackPastItsBuffer;
        /// <summary>The sequence settings this codec's stream carries; another codec of the same phone may write others.</summary>
        public byte[] SequenceSettingsSent = SequenceSettings;
        /// <summary>True makes the first picture a frame that needs an earlier one, which a new stream never should.</summary>
        public bool FirstPictureNeedsAnEarlierOne;

        public int Width;
        public int Height;
        public int FramesPerSecond;
        public int BitsPerSecond;
        public int KeyframeIntervalSeconds;
        public bool ConstantBitrateAsked;
        public bool EndOfStreamQueued;
        public int StopCalls;
        public int DisposeCalls;
        public readonly List<byte[]> FramesQueued = new List<byte[]>();
        public readonly List<long> TimesQueuedUs = new List<long>();

        private readonly IntPtr[] _input = new IntPtr[InputBuffers];
        private readonly bool[] _inputHandedOut = new bool[InputBuffers];
        private readonly Queue<Output> _waiting = new Queue<Output>();
        private readonly Queue<Output> _held = new Queue<Output>();
        private readonly Dictionary<long, Output> _handedBack = new Dictionary<long, Output>();
        private long _inputCapacity;
        private long _nextOutputIndex;
        private bool _settingsSent;

        private sealed class Output
        {
            public byte[] Bytes;
            public long TimeUs;
            public uint Flags;
            public bool FormatChange;
            public IntPtr Memory;
        }

        public int Configure(int width, int height, int framesPerSecond, int bitsPerSecond, int keyframeIntervalSeconds, bool constantBitrate)
        {
            Width = width;
            Height = height;
            FramesPerSecond = framesPerSecond;
            BitsPerSecond = bitsPerSecond;
            KeyframeIntervalSeconds = keyframeIntervalSeconds;
            ConstantBitrateAsked = constantBitrate;
            return ConfigureStatus;
        }

        public void ReadInputLayout(out int stride, out int sliceHeight)
        {
            stride = Stride;
            sliceHeight = SliceHeight;
        }

        public int Start()
        {
            if (StartStatus != 0)
                return StartStatus;
            int rowBytes = Math.Max(Stride, Width);
            int rows = Math.Max(SliceHeight, Height);
            _inputCapacity = (long)rowBytes * rows * 3 / 2 + rowBytes;
            for (int i = 0; i < InputBuffers; i++)
                _input[i] = Marshal.AllocHGlobal((IntPtr)_inputCapacity);
            return 0;
        }

        public long DequeueInputBuffer(long timeoutMicroseconds)
        {
            if (!NeverFreesAnInputBuffer)
            {
                for (int i = 0; i < InputBuffers; i++)
                {
                    if (!_inputHandedOut[i])
                    {
                        _inputHandedOut[i] = true;
                        return i;
                    }
                }
            }
            Thread.Sleep(1);
            return ProtokitePlaytestMediaCodec.TryAgainLater;
        }

        public IntPtr GetInputBuffer(long index, out long capacity)
        {
            capacity = _inputCapacity;
            return _input[index];
        }

        public int QueueInputBuffer(long index, int size, long presentationTimeUs, uint flags)
        {
            _inputHandedOut[index] = false;
            if ((flags & ProtokitePlaytestMediaCodec.EndOfStreamFlag) != 0)
            {
                EndOfStreamQueued = true;
                while (_held.Count > 0)
                    _waiting.Enqueue(_held.Dequeue());
                _waiting.Enqueue(new Output { Bytes = new byte[0], TimeUs = presentationTimeUs, Flags = ProtokitePlaytestMediaCodec.EndOfStreamFlag });
                return 0;
            }
            byte[] queued = new byte[size];
            Marshal.Copy(_input[index], queued, 0, size);
            FramesQueued.Add(queued);
            TimesQueuedUs.Add(presentationTimeUs);
            if (!_settingsSent)
            {
                _settingsSent = true;
                if (SettingsInABufferOfTheirOwn)
                    _waiting.Enqueue(new Output { Bytes = Join(StartCode, SequenceSettingsSent, StartCode, PictureSettings), Flags = ProtokitePlaytestMediaCodec.SettingsOnlyFlag });
                if (SettingsOnlyOnTheOutputFormat)
                    _waiting.Enqueue(new Output { FormatChange = true });
            }
            bool first = FramesQueued.Count == 1 && !FirstPictureNeedsAnEarlierOne;
            byte[] picture = HandsBackNoStartCode
                ? new byte[] { 0x65, 0x88, (byte)FramesQueued.Count }
                : Join(StartCode, new byte[] { (byte)(first ? 0x65 : 0x41), 0x88, (byte)FramesQueued.Count });
            if (first && SettingsInFrontOfTheFirstPicture)
                picture = Join(StartCode, SequenceSettingsSent, StartCode, PictureSettings, picture);
            Output frame = new Output { Bytes = picture, TimeUs = presentationTimeUs, Flags = first ? ProtokitePlaytestMediaCodec.KeyframeFlag : 0 };
            _held.Enqueue(frame);
            while (_held.Count > FramesHeldUntilTheEnd)
                _waiting.Enqueue(_held.Dequeue());
            return 0;
        }

        public long DequeueOutputBuffer(long timeoutMicroseconds, out ProtokitePlaytestCodecBuffer buffer)
        {
            buffer = default;
            if (_waiting.Count == 0)
                return ProtokitePlaytestMediaCodec.TryAgainLater;
            Output output = _waiting.Dequeue();
            if (output.FormatChange)
                return ProtokitePlaytestMediaCodec.OutputFormatChanged;
            output.Memory = Marshal.AllocHGlobal(Math.Max(1, output.Bytes.Length));
            Marshal.Copy(output.Bytes, 0, output.Memory, output.Bytes.Length);
            long index = _nextOutputIndex++;
            _handedBack[index] = output;
            buffer = new ProtokitePlaytestCodecBuffer
            {
                Offset = 0,
                Size = HandsBackPastItsBuffer ? output.Bytes.Length + 10 : output.Bytes.Length,
                PresentationTimeUs = output.TimeUs,
                Flags = output.Flags
            };
            return index;
        }

        public IntPtr GetOutputBuffer(long index, out long capacity)
        {
            Output output = _handedBack[index];
            capacity = output.Bytes.Length;
            return output.Memory;
        }

        public void ReleaseOutputBuffer(long index)
        {
            if (_handedBack.TryGetValue(index, out Output output))
            {
                Marshal.FreeHGlobal(output.Memory);
                _handedBack.Remove(index);
            }
        }

        public List<byte[]> SettingsOnTheOutputFormat()
            => SettingsOnlyOnTheOutputFormat ? new List<byte[]> { Join(StartCode, SequenceSettingsSent), Join(StartCode, PictureSettings) } : new List<byte[]>();

        public void Stop() => StopCalls++;

        public void Dispose()
        {
            DisposeCalls++;
            for (int i = 0; i < InputBuffers; i++)
            {
                if (_input[i] != IntPtr.Zero)
                    Marshal.FreeHGlobal(_input[i]);
                _input[i] = IntPtr.Zero;
            }
            foreach (Output output in _handedBack.Values)
                Marshal.FreeHGlobal(output.Memory);
            _handedBack.Clear();
        }

        private static byte[] Join(params byte[][] parts)
        {
            List<byte> all = new List<byte>();
            foreach (byte[] part in parts)
                all.AddRange(part);
            return all.ToArray();
        }
    }
}
