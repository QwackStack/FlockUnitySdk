#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Runtime.InteropServices;

namespace Protokite.Playtest
{
    /// <summary>
    /// The part of Windows' Media Foundation the playtest calls, from the Windows SDK 10.0.26100 headers. Interface methods keep
    /// the headers' order, because COM finds a method by its place; a method the playtest never calls only holds its place.
    /// </summary>
    internal static class ProtokitePlaytestMediaFoundation
    {
        internal const uint MediaFoundationVersion = 0x00020070;
        internal const uint StartupFull = 0;
        internal const uint MultiThreadedApartment = 0;

        internal const uint EnumSynchronous = 0x1;
        internal const uint EnumAsynchronous = 0x2;
        internal const uint EnumHardware = 0x4;
        internal const uint EnumSortAndFilter = 0x40;

        internal const uint InterlaceProgressive = 2;
        internal const uint H264ProfileHigh = 100;
        internal const uint H264ProfileMain = 77;
        internal const uint MatrixBt709 = 1;
        internal const uint Range16To235 = 2;
        internal const uint PrimariesBt709 = 2;
        internal const uint TransferBt709 = 5;
        internal const uint RateControlConstantBitrate = 0;

        internal const uint EventNeedInput = 601;
        internal const uint EventHaveOutput = 602;
        internal const uint EventDrainComplete = 603;
        internal const uint EventNoWait = 1;

        internal const uint MessageCommandDrain = 0x1;
        internal const uint MessageBeginStreaming = 0x10000000;
        internal const uint MessageEndStreaming = 0x10000001;
        internal const uint MessageEndOfStream = 0x10000002;
        internal const uint MessageStartOfStream = 0x10000003;

        internal const uint OutputStreamProvidesSamples = 0x100;
        internal const uint OutputStreamCanProvideSamples = 0x200;

        internal const int Ok = 0;
        internal const int AlreadyStarted = 1;
        internal const int NeedMoreInput = unchecked((int)0xC00D6D72);
        internal const int StreamChange = unchecked((int)0xC00D6D61);
        internal const int NoEventsAvailable = unchecked((int)0xC00D3E80);

        internal const ushort VariantUInt32 = 19;
        internal const ushort VariantBool = 11;

        internal static readonly Guid MajorType = new Guid(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
        internal static readonly Guid Video = new Guid(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        internal static readonly Guid Subtype = new Guid(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
        // The four letters H264 and NV12 in Media Foundation's video subtype pattern.
        internal static readonly Guid H264 = new Guid(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        internal static readonly Guid Nv12 = new Guid(0x3231564E, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        internal static readonly Guid AverageBitrate = new Guid(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
        internal static readonly Guid InterlaceMode = new Guid(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);
        internal static readonly Guid FrameSize = new Guid(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
        internal static readonly Guid FrameRate = new Guid(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
        internal static readonly Guid PixelAspectRatio = new Guid(0xc6376a1e, 0x8d0a, 0x4027, 0xbe, 0x45, 0x6d, 0x9a, 0x0a, 0xd3, 0x9b, 0xb6);
        internal static readonly Guid Profile = new Guid(0xad76a80b, 0x2d5c, 0x4e0b, 0xb3, 0x75, 0x64, 0xe5, 0x20, 0x13, 0x70, 0x36);
        internal static readonly Guid SequenceHeader = new Guid(0x3c036de7, 0x3ad0, 0x4c9e, 0x92, 0x16, 0xee, 0x6d, 0x6a, 0xc2, 0x1c, 0xb3);
        internal static readonly Guid YuvMatrix = new Guid(0x3e23d450, 0x2c75, 0x4d25, 0xa0, 0x0e, 0xb9, 0x16, 0x70, 0xd1, 0x23, 0x27);
        internal static readonly Guid NominalRange = new Guid(0xc21b8ee5, 0xb956, 0x4071, 0x8d, 0xaf, 0x32, 0x5e, 0xdf, 0x5c, 0xab, 0x11);
        internal static readonly Guid Primaries = new Guid(0xdbfbe4d7, 0x0740, 0x4ee0, 0x81, 0x92, 0x85, 0x0a, 0xb0, 0xe2, 0x19, 0x35);
        internal static readonly Guid TransferFunction = new Guid(0x5fb0fce9, 0xbe5c, 0x4935, 0xa8, 0x11, 0xec, 0x83, 0x8f, 0x8e, 0xed, 0x93);
        internal static readonly Guid LowLatency = new Guid(0x9c27891a, 0xed7a, 0x40e1, 0x88, 0xe8, 0xb2, 0x27, 0x27, 0xa0, 0x24, 0xee);
        internal static readonly Guid VideoEncoderCategory = new Guid(0xf79eac7d, 0xe545, 0x4387, 0xbd, 0xee, 0xd6, 0x47, 0xd7, 0xbd, 0xe4, 0x2a);
        internal static readonly Guid FriendlyName = new Guid(0x314ffbae, 0x5b41, 0x4c95, 0x9c, 0x19, 0x4e, 0x7d, 0x58, 0x6f, 0xac, 0xe3);
        internal static readonly Guid HardwareUrl = new Guid(0x2fb866ac, 0xb078, 0x4942, 0xab, 0x6c, 0x00, 0x3d, 0x05, 0xcd, 0xa6, 0x74);
        internal static readonly Guid HardwareVendorId = new Guid(0x3aecb0cc, 0x035b, 0x4bcc, 0x81, 0x85, 0x2b, 0x8d, 0x55, 0x1e, 0xf3, 0xaf);
        internal static readonly Guid TransformAsync = new Guid(0xf81a699a, 0x649a, 0x497d, 0x8c, 0x73, 0x29, 0xf8, 0xfe, 0xd6, 0xad, 0x7a);
        internal static readonly Guid TransformAsyncUnlock = new Guid(0xe5666d6b, 0x3422, 0x4eb6, 0xa4, 0x21, 0xda, 0x7d, 0xb1, 0xf8, 0xe2, 0x07);
        internal static readonly Guid CodecRateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        internal static readonly Guid CodecMeanBitrate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");
        internal static readonly Guid CodecKeyframeInterval = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        internal static readonly Guid CodecBFrameCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");
        internal static readonly Guid CodecLowLatency = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        internal static readonly Guid TransformInterface = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");

        [StructLayout(LayoutKind.Sequential)]
        internal struct RegisterTypeInfo
        {
            public Guid MajorType;
            public Guid Subtype;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OutputStreamInfo
        {
            public uint Flags;
            public uint Size;
            public uint Alignment;
        }

        /// <summary>One output stream's sample; the encoder fills in a sample of its own when the stream provides them.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct OutputDataBuffer
        {
            public uint StreamId;
            public IntPtr Sample;
            public uint Status;
            public IntPtr Events;
        }

        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IMFMediaType mediaType);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFTEnumEx(Guid category, uint flags, ref RegisterTypeInfo inputType,
            ref RegisterTypeInfo outputType, out IntPtr activates, out uint count);
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoInitializeEx(IntPtr reserved, uint apartment);
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern void CoUninitialize();

        /// <summary>A COM object from a pointer a call handed over, which this takes over: the pointer's own reference is let go.</summary>
        internal static T TakeObject<T>(IntPtr pointer) where T : class
        {
            if (pointer == IntPtr.Zero)
                return null;
            try
            {
                return (T)Marshal.GetObjectForIUnknown(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        /// <summary>Lets go of a COM object now rather than when the garbage collector gets to it; null is ignored.</summary>
        internal static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }

        /// <summary>Sets one of an encoder's codec settings to a whole number, or a yes or no; Windows' own answer.</summary>
        internal static int SetCodecValue(ICodecAPI codec, Guid setting, uint value, bool asYesOrNo)
        {
            // A VARIANT: its type in the first two bytes, its value eight bytes in; 24 bytes covers it on 64-bit Windows.
            IntPtr variant = Marshal.AllocHGlobal(24);
            try
            {
                for (int offset = 0; offset < 24; offset += 4)
                    Marshal.WriteInt32(variant, offset, 0);
                Marshal.WriteInt16(variant, 0, (short)(asYesOrNo ? VariantBool : VariantUInt32));
                if (asYesOrNo)
                    Marshal.WriteInt16(variant, 8, (short)(value != 0 ? -1 : 0));
                else
                    Marshal.WriteInt32(variant, 8, (int)value);
                return codec.SetValue(ref setting, variant);
            }
            finally
            {
                Marshal.FreeHGlobal(variant);
            }
        }

        /// <summary>Windows' answer as eight hex digits, the way Windows' own documentation lists them.</summary>
        internal static string Hex(int result) => "0x" + result.ToString("X8");

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFAttributes
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr text, out uint length);
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr blob, out uint size);
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaType
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr blob, out uint size);
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
            [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFSample
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32();
            [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID();
            [PreserveSig] int SetString();
            [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex();
            [PreserveSig] int CopyAllItems();
            [PreserveSig] int GetSampleFlags();
            [PreserveSig] int SetSampleFlags();
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount();
            [PreserveSig] int GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint length);
            [PreserveSig] int SetCurrentLength(uint length);
        }

        [ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFTransform
        {
            [PreserveSig] int GetStreamLimits();
            [PreserveSig] int GetStreamCount();
            [PreserveSig] int GetStreamIDs();
            [PreserveSig] int GetInputStreamInfo();
            [PreserveSig] int GetOutputStreamInfo(uint streamId, out OutputStreamInfo info);
            [PreserveSig] int GetAttributes(out IMFAttributes attributes);
            [PreserveSig] int GetInputStreamAttributes();
            [PreserveSig] int GetOutputStreamAttributes();
            [PreserveSig] int DeleteInputStream();
            [PreserveSig] int AddInputStreams();
            [PreserveSig] int GetInputAvailableType(uint streamId, uint index, out IMFMediaType type);
            [PreserveSig] int GetOutputAvailableType(uint streamId, uint index, out IMFMediaType type);
            [PreserveSig] int SetInputType(uint streamId, IMFMediaType type, uint flags);
            [PreserveSig] int SetOutputType(uint streamId, IMFMediaType type, uint flags);
            [PreserveSig] int GetInputCurrentType(uint streamId, out IMFMediaType type);
            [PreserveSig] int GetOutputCurrentType(uint streamId, out IMFMediaType type);
            [PreserveSig] int GetInputStatus();
            [PreserveSig] int GetOutputStatus();
            [PreserveSig] int SetOutputBounds();
            [PreserveSig] int ProcessEvent();
            [PreserveSig] int ProcessMessage(uint message, IntPtr parameter);
            [PreserveSig] int ProcessInput(uint streamId, IMFSample sample, uint flags);
            [PreserveSig] int ProcessOutput(uint flags, uint count, ref OutputDataBuffer buffer, out uint status);
        }

        [ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFActivate
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr text, out uint length);
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32();
            [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID();
            [PreserveSig] int SetString();
            [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex();
            [PreserveSig] int CopyAllItems();
            [PreserveSig] int ActivateObject([In] ref Guid interfaceId, out IntPtr created);
            [PreserveSig] int ShutdownObject();
            [PreserveSig] int DetachObject();
        }

        [ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaEventGenerator
        {
            [PreserveSig] int GetEvent(uint flags, out IMFMediaEvent mediaEvent);
        }

        [ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMFMediaEvent
        {
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32();
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32();
            [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID();
            [PreserveSig] int SetString();
            [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex();
            [PreserveSig] int CopyAllItems();
            // The headers' GetType; renamed, since every C# object has a GetType of its own. Only the place matters to COM.
            [PreserveSig] int GetEventType(out uint type);
            [PreserveSig] int GetExtendedType();
            [PreserveSig] int GetStatus(out int status);
        }

        [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ICodecAPI
        {
            [PreserveSig] int IsSupported([In] ref Guid setting);
            [PreserveSig] int IsModifiable();
            [PreserveSig] int GetParameterRange();
            [PreserveSig] int GetParameterValues();
            [PreserveSig] int GetDefaultValue();
            [PreserveSig] int GetValue();
            [PreserveSig] int SetValue([In] ref Guid setting, IntPtr value);
        }
    }
}
#endif
