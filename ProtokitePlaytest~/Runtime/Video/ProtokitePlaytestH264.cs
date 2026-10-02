using System;
using System.Collections.Generic;

namespace Protokite.Playtest
{
    /// <summary>An encoder's H.264 units (each after a start code) read the way an MP4 file keeps them: each after its length, with the stream's settings kept once.</summary>
    internal static class ProtokitePlaytestH264
    {
        /// <summary>A unit holding the picture of a frame a player can start decoding at.</summary>
        internal const int KeyframeUnit = 5;
        /// <summary>The unit holding the stream's sequence settings (its size, profile and level).</summary>
        internal const int SequenceSettingsUnit = 7;
        /// <summary>The unit holding the stream's picture settings.</summary>
        internal const int PictureSettingsUnit = 8;
        /// <summary>A unit that only marks where a frame starts, which an MP4 file does not need.</summary>
        internal const int FrameStartMarkerUnit = 9;

        /// <summary>The bytes before each unit in a stored frame, giving its length.</summary>
        internal const int LengthBytes = 4;

        /// <summary>The type of a unit, from its first byte.</summary>
        internal static int UnitType(ArraySegment<byte> unit) => unit.Count == 0 ? -1 : unit.Array[unit.Offset] & 0x1F;

        /// <summary>Whether an encoder's output starts with a start code (00 00 01, or 00 00 00 01), as every H.264 byte stream does.</summary>
        internal static bool StartsWithAStartCode(byte[] stream, int length)
            => (length >= 3 && stream[0] == 0 && stream[1] == 0 && stream[2] == 1)
               || (length >= 4 && stream[0] == 0 && stream[1] == 0 && stream[2] == 0 && stream[3] == 1);

        /// <summary>The units of an encoder's output, each without its start code and without the zeros that may pad it.</summary>
        internal static List<ArraySegment<byte>> SplitUnits(byte[] stream, int length)
        {
            List<ArraySegment<byte>> units = new List<ArraySegment<byte>>();
            int start = -1;
            int position = 0;
            while (position + 2 < length)
            {
                if (stream[position] == 0 && stream[position + 1] == 0 && stream[position + 2] == 1)
                {
                    if (start >= 0)
                        AddUnit(units, stream, start, position);
                    position += 3;
                    start = position;
                    continue;
                }
                position++;
            }
            if (start >= 0)
                AddUnit(units, stream, start, length);
            return units;
        }

        // A four-byte start code leaves a zero at the end of the unit before it; a unit never ends in a zero byte of its own.
        private static void AddUnit(List<ArraySegment<byte>> units, byte[] stream, int start, int end)
        {
            while (end > start && stream[end - 1] == 0)
                end--;
            if (end > start)
                units.Add(new ArraySegment<byte>(stream, start, end - start));
        }

        /// <summary>The units an MP4 file stores for a frame, each after its length; settings and frame-start markers are left out.</summary>
        internal static byte[] FrameAsStored(List<ArraySegment<byte>> units)
        {
            int total = 0;
            foreach (ArraySegment<byte> unit in units)
            {
                if (IsPartOfTheFrame(unit))
                    total += LengthBytes + unit.Count;
            }
            byte[] stored = new byte[total];
            int position = 0;
            foreach (ArraySegment<byte> unit in units)
            {
                if (!IsPartOfTheFrame(unit))
                    continue;
                WriteBigEndian(stored, position, (uint)unit.Count);
                Array.Copy(unit.Array, unit.Offset, stored, position + LengthBytes, unit.Count);
                position += LengthBytes + unit.Count;
            }
            return stored;
        }

        /// <summary>Whether a unit is stored with the frame rather than kept apart or dropped.</summary>
        internal static bool IsPartOfTheFrame(ArraySegment<byte> unit)
        {
            int type = UnitType(unit);
            return type != SequenceSettingsUnit && type != PictureSettingsUnit && type != FrameStartMarkerUnit;
        }

        /// <summary>Whether a unit holds a picture (a slice of a frame), not settings or extra information about one.</summary>
        internal static bool IsPicture(ArraySegment<byte> unit)
        {
            int type = UnitType(unit);
            return type >= 1 && type <= KeyframeUnit;
        }

        /// <summary>Whether any of the units is the picture of a keyframe.</summary>
        internal static bool HoldsKeyframe(List<ArraySegment<byte>> units)
        {
            foreach (ArraySegment<byte> unit in units)
            {
                if (UnitType(unit) == KeyframeUnit)
                    return true;
            }
            return false;
        }

        /// <summary>The record an MP4 file keeps the stream's settings in (its "avcC" box's contents), or null without both settings units.</summary>
        internal static byte[] DecoderSettingsRecord(byte[] sequenceSettings, byte[] pictureSettings)
        {
            if (sequenceSettings == null || sequenceSettings.Length < 4 || pictureSettings == null || pictureSettings.Length < 1)
                return null;
            int profile = sequenceSettings[1];
            // The higher profiles also say how the colour is sampled: 4:2:0 at eight bits here, as the encoder is given NV12.
            bool highProfile = profile == 100 || profile == 110 || profile == 122 || profile == 144;
            byte[] record = new byte[6 + 2 + sequenceSettings.Length + 1 + 2 + pictureSettings.Length + (highProfile ? 4 : 0)];
            int position = 0;
            record[position++] = 1;
            record[position++] = sequenceSettings[1];
            record[position++] = sequenceSettings[2];
            record[position++] = sequenceSettings[3];
            record[position++] = 0xFC | (LengthBytes - 1);
            record[position++] = 0xE0 | 1;
            record[position++] = (byte)(sequenceSettings.Length >> 8);
            record[position++] = (byte)sequenceSettings.Length;
            Array.Copy(sequenceSettings, 0, record, position, sequenceSettings.Length);
            position += sequenceSettings.Length;
            record[position++] = 1;
            record[position++] = (byte)(pictureSettings.Length >> 8);
            record[position++] = (byte)pictureSettings.Length;
            Array.Copy(pictureSettings, 0, record, position, pictureSettings.Length);
            position += pictureSettings.Length;
            if (highProfile)
            {
                record[position++] = 0xFC | 1;
                record[position++] = 0xF8;
                record[position++] = 0xF8;
                record[position] = 0;
            }
            return record;
        }

        /// <summary>Whether a stored frame starts as every one this package writes does (a first unit that fits, with a unit's first byte); zeros the disk never filled in fail it.</summary>
        internal static bool LooksLikeAStoredFrame(byte[] bytes, int offset, int startLength, long frameLength)
        {
            if (startLength < LengthBytes + 1 || frameLength < LengthBytes + 1)
                return false;
            long firstUnit = ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
            byte header = bytes[offset + LengthBytes];
            int type = header & 0x1F;
            // The top bit of a unit's first byte is always clear, and types 1 to 23 are the ones a stream holds.
            return firstUnit >= 1 && LengthBytes + firstUnit <= frameLength && (header & 0x80) == 0 && type >= 1 && type <= 23;
        }

        private static void WriteBigEndian(byte[] bytes, int position, uint value)
        {
            bytes[position] = (byte)(value >> 24);
            bytes[position + 1] = (byte)(value >> 16);
            bytes[position + 2] = (byte)(value >> 8);
            bytes[position + 3] = (byte)value;
        }
    }
}
