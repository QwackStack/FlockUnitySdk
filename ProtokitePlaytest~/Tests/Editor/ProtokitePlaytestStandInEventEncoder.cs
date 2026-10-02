#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Protokite.Playtest.ProtokitePlaytestMediaFoundation;

namespace Protokite.Playtest.Tests
{
    /// <summary>Stands in for a graphics card's encoder that hands frames back through events, keeping Windows' rule: one output call per "has output" event.</summary>
    internal sealed class StandInEventEncoder : IMFTransform, IMFMediaEventGenerator
    {
        /// <summary>Windows' answer to an output call no event allowed (E_UNEXPECTED).</summary>
        internal const int Unexpected = unchecked((int)0x8000FFFF);

        private const int NotAccepting = unchecked((int)0xC00D36B5);
        private const int NotImplemented = unchecked((int)0x80004001);
        private const int NoMoreTypes = unchecked((int)0xC00D36B9);
        private const int BufferTooSmall = unchecked((int)0xC00D36B1);
        private const uint FormatChangeFlag = 0x100;
        private const long HundredNanosecondsPerMillisecond = 10000;
        private static readonly byte[] StreamSettings = { 0, 0, 0, 1, 0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9, 0, 0, 0, 1, 0x68, 0xEB, 0xE3 };
        private static readonly byte[] Keyframe = { 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21 };
        private static readonly byte[] LaterFrame = { 0, 0, 0, 1, 0x41, 0x9A, 0x02 };

        private readonly Queue<uint> _events = new Queue<uint>();
        private readonly Queue<long> _timesHandedIn = new Queue<long>();
        private int _inputsAllowed;
        private int _outputsAllowed;
        private bool _waitingForANewOutputType;
        private int _framesHandedBack;

        /// <summary>Changes its output format when first asked for a frame, as Intel's Quick Sync encoder evidently does (a laptop's E_UNEXPECTED, 2026-10-01).</summary>
        public bool ChangesFormatBeforeTheFirstFrame;

        /// <summary>Gives the stream's sequence and picture settings only on its output type, never in a frame.</summary>
        public bool GivesSettingsOnlyOnItsOutputType;

        /// <summary>The output buffer its new output type needs after a format change, refusing a smaller one; 0 for none.</summary>
        public uint OutputBufferBytesAfterTheChange;

        public int FormatChanges { get; private set; }

        /// <summary>Calls its events did not allow, each answered as Windows answers them.</summary>
        public int CallsRefused { get; private set; }

        public int GetEvent(uint flags, out IMFMediaEvent mediaEvent)
        {
            if (_events.Count == 0)
            {
                mediaEvent = null;
                return NoEventsAvailable;
            }
            uint type = _events.Dequeue();
            if (type == EventNeedInput)
                _inputsAllowed++;
            else if (type == EventHaveOutput)
                _outputsAllowed++;
            mediaEvent = new StandInEvent(type);
            return Ok;
        }

        public int GetOutputStreamInfo(uint streamId, out OutputStreamInfo info)
        {
            // The caller provides each output sample, as big as the current output type needs.
            info = new OutputStreamInfo { Size = FormatChanges > 0 ? OutputBufferBytesAfterTheChange : 0 };
            return Ok;
        }

        public int GetAttributes(out IMFAttributes attributes)
        {
            attributes = new StandInAttributes();
            return Ok;
        }

        public int GetOutputAvailableType(uint streamId, uint index, out IMFMediaType type)
        {
            type = null;
            return index > 0 ? NoMoreTypes : MFCreateMediaType(out type);
        }

        public int SetInputType(uint streamId, IMFMediaType type, uint flags) => Ok;

        public int SetOutputType(uint streamId, IMFMediaType type, uint flags)
        {
            // The frame the change held back is announced again once the new type is set.
            if (_waitingForANewOutputType)
            {
                _waitingForANewOutputType = false;
                _events.Enqueue(EventHaveOutput);
            }
            return Ok;
        }

        public int ProcessMessage(uint message, IntPtr parameter)
        {
            if (message == MessageStartOfStream)
                _events.Enqueue(EventNeedInput);
            else if (message == MessageCommandDrain)
                _events.Enqueue(EventDrainComplete);
            return Ok;
        }

        public int ProcessInput(uint streamId, IMFSample sample, uint flags)
        {
            if (_inputsAllowed == 0)
            {
                CallsRefused++;
                return NotAccepting;
            }
            _inputsAllowed--;
            sample.GetSampleTime(out long time);
            _timesHandedIn.Enqueue(time);
            _events.Enqueue(EventHaveOutput);
            _events.Enqueue(EventNeedInput);
            return Ok;
        }

        public int ProcessOutput(uint flags, uint count, ref OutputDataBuffer buffer, out uint status)
        {
            status = 0;
            if (_outputsAllowed == 0)
            {
                CallsRefused++;
                return Unexpected;
            }
            _outputsAllowed--;
            if (ChangesFormatBeforeTheFirstFrame && FormatChanges == 0)
            {
                FormatChanges++;
                _waitingForANewOutputType = true;
                buffer.Status |= FormatChangeFlag;
                return StreamChange;
            }

            byte[] bytes = _framesHandedBack > 0 ? LaterFrame
                : GivesSettingsOnlyOnItsOutputType ? Keyframe
                : Join(StreamSettings, Keyframe);
            // The caller's own sample object, shared with this pointer: it is the caller's to let go of.
            IMFSample sample = (IMFSample)Marshal.GetObjectForIUnknown(buffer.Sample);
            if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer memory) < 0)
                return Unexpected;
            try
            {
                memory.Lock(out IntPtr at, out uint room, out uint _);
                if (FormatChanges > 0 && room < OutputBufferBytesAfterTheChange)
                {
                    memory.Unlock();
                    CallsRefused++;
                    return BufferTooSmall;
                }
                Marshal.Copy(bytes, 0, at, bytes.Length);
                memory.Unlock();
                memory.SetCurrentLength((uint)bytes.Length);
            }
            finally
            {
                Marshal.ReleaseComObject(memory);
            }
            sample.SetSampleTime(_timesHandedIn.Dequeue());
            _framesHandedBack++;
            return Ok;
        }

        public int GetStreamLimits() => NotImplemented;
        public int GetStreamCount() => NotImplemented;
        public int GetStreamIDs() => NotImplemented;
        public int GetInputStreamInfo() => NotImplemented;
        public int GetInputStreamAttributes() => NotImplemented;
        public int GetOutputStreamAttributes() => NotImplemented;
        public int DeleteInputStream() => NotImplemented;
        public int AddInputStreams() => NotImplemented;

        public int GetInputAvailableType(uint streamId, uint index, out IMFMediaType type)
        {
            type = null;
            return NotImplemented;
        }

        public int GetInputCurrentType(uint streamId, out IMFMediaType type)
        {
            type = null;
            return NotImplemented;
        }

        public int GetOutputCurrentType(uint streamId, out IMFMediaType type)
        {
            type = GivesSettingsOnlyOnItsOutputType ? new StandInOutputType(StreamSettings) : null;
            return type != null ? Ok : NotImplemented;
        }

        private static byte[] Join(byte[] first, byte[] second)
        {
            byte[] joined = new byte[first.Length + second.Length];
            Array.Copy(first, joined, first.Length);
            Array.Copy(second, 0, joined, first.Length, second.Length);
            return joined;
        }

        /// <summary>An output type holding the stream's settings as its sequence header, handed over as Windows hands a blob: the caller frees it.</summary>
        private sealed class StandInOutputType : IMFMediaType
        {
            private readonly byte[] _sequenceHeader;

            public StandInOutputType(byte[] sequenceHeader)
            {
                _sequenceHeader = sequenceHeader;
            }

            public int GetAllocatedBlob(ref Guid key, out IntPtr blob, out uint size)
            {
                blob = IntPtr.Zero;
                size = 0;
                if (key != SequenceHeader)
                    return NotImplemented;
                blob = Marshal.AllocCoTaskMem(_sequenceHeader.Length);
                Marshal.Copy(_sequenceHeader, 0, blob, _sequenceHeader.Length);
                size = (uint)_sequenceHeader.Length;
                return Ok;
            }

            public int GetUINT32(ref Guid key, out uint value)
            {
                value = 0;
                return NotImplemented;
            }

            public int GetUINT64(ref Guid key, out ulong value)
            {
                value = 0;
                return NotImplemented;
            }

            public int GetGUID(ref Guid key, out Guid value)
            {
                value = Guid.Empty;
                return NotImplemented;
            }

            public int SetUINT32(ref Guid key, uint value) => NotImplemented;
            public int SetUINT64(ref Guid key, ulong value) => NotImplemented;
            public int SetGUID(ref Guid key, ref Guid value) => NotImplemented;
            public int GetItem() => NotImplemented;
            public int GetItemType() => NotImplemented;
            public int CompareItem() => NotImplemented;
            public int Compare() => NotImplemented;
            public int GetDouble() => NotImplemented;
            public int GetStringLength() => NotImplemented;
            public int GetString() => NotImplemented;
            public int GetAllocatedString() => NotImplemented;
            public int GetBlobSize() => NotImplemented;
            public int GetBlob() => NotImplemented;
            public int GetUnknown() => NotImplemented;
            public int SetItem() => NotImplemented;
            public int DeleteItem() => NotImplemented;
            public int DeleteAllItems() => NotImplemented;
            public int SetDouble() => NotImplemented;
        }

        public int GetInputStatus() => NotImplemented;
        public int GetOutputStatus() => NotImplemented;
        public int SetOutputBounds() => NotImplemented;
        public int ProcessEvent() => NotImplemented;

        /// <summary>The stand-in's attributes: it says it hands frames back through events, and takes every setting.</summary>
        private sealed class StandInAttributes : IMFAttributes
        {
            private const int NotFound = unchecked((int)0xC00D36E6);

            public int GetUINT32(ref Guid key, out uint value)
            {
                bool asynchronous = key == TransformAsync;
                value = asynchronous ? 1u : 0u;
                return asynchronous ? Ok : NotFound;
            }

            public int SetUINT32(ref Guid key, uint value) => Ok;
            public int SetUINT64(ref Guid key, ulong value) => Ok;
            public int SetGUID(ref Guid key, ref Guid value) => Ok;

            public int GetStringLength(ref Guid key, out uint length)
            {
                length = 0;
                return NotFound;
            }

            public int GetAllocatedString(ref Guid key, out IntPtr text, out uint length)
            {
                text = IntPtr.Zero;
                length = 0;
                return NotFound;
            }

            public int GetAllocatedBlob(ref Guid key, out IntPtr blob, out uint size)
            {
                blob = IntPtr.Zero;
                size = 0;
                return NotFound;
            }

            public int GetItem() => NotImplemented;
            public int GetItemType() => NotImplemented;
            public int CompareItem() => NotImplemented;
            public int Compare() => NotImplemented;
            public int GetUINT64() => NotImplemented;
            public int GetDouble() => NotImplemented;
            public int GetGUID() => NotImplemented;
            public int GetString() => NotImplemented;
            public int GetBlobSize() => NotImplemented;
            public int GetBlob() => NotImplemented;
            public int GetUnknown() => NotImplemented;
            public int SetItem() => NotImplemented;
            public int DeleteItem() => NotImplemented;
            public int DeleteAllItems() => NotImplemented;
            public int SetDouble() => NotImplemented;
        }

        /// <summary>One of the stand-in's events: only its type and a status of success are ever read.</summary>
        private sealed class StandInEvent : IMFMediaEvent
        {
            private readonly uint _type;

            public StandInEvent(uint type)
            {
                _type = type;
            }

            public int GetEventType(out uint type)
            {
                type = _type;
                return Ok;
            }

            public int GetStatus(out int status)
            {
                status = Ok;
                return Ok;
            }

            public int GetItem() => NotImplemented;
            public int GetItemType() => NotImplemented;
            public int CompareItem() => NotImplemented;
            public int Compare() => NotImplemented;
            public int GetUINT32() => NotImplemented;
            public int GetUINT64() => NotImplemented;
            public int GetDouble() => NotImplemented;
            public int GetGUID() => NotImplemented;
            public int GetStringLength() => NotImplemented;
            public int GetString() => NotImplemented;
            public int GetAllocatedString() => NotImplemented;
            public int GetBlobSize() => NotImplemented;
            public int GetBlob() => NotImplemented;
            public int GetAllocatedBlob() => NotImplemented;
            public int GetUnknown() => NotImplemented;
            public int SetItem() => NotImplemented;
            public int DeleteItem() => NotImplemented;
            public int DeleteAllItems() => NotImplemented;
            public int SetUINT32() => NotImplemented;
            public int SetUINT64() => NotImplemented;
            public int SetDouble() => NotImplemented;
            public int SetGUID() => NotImplemented;
            public int SetString() => NotImplemented;
            public int SetBlob() => NotImplemented;
            public int SetUnknown() => NotImplemented;
            public int LockStore() => NotImplemented;
            public int UnlockStore() => NotImplemented;
            public int GetCount() => NotImplemented;
            public int GetItemByIndex() => NotImplemented;
            public int CopyAllItems() => NotImplemented;
            public int GetExtendedType() => NotImplemented;
        }
    }
}
#endif
