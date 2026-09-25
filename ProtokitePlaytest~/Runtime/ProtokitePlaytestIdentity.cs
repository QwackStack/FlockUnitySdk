using System;
using System.IO;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>The longest values Protokite's session start takes; a longer one would make it refuse the whole start.</summary>
    internal static class ProtokitePlaytestIdentityLimits
    {
        public const int SteamIdLength = 64;
        public const int PlayerNameLength = 200;
        public const int FlockSessionIdLength = 26;
    }

    /// <summary>What reading the device id file found.</summary>
    internal enum ProtokitePlaytestDeviceIdFileResult
    {
        /// <summary>The file held a device id, used as it is.</summary>
        Read,
        /// <summary>There was no file, so a new device id was made and saved.</summary>
        Created,
        /// <summary>The file held something other than a device id, so a new one replaced it.</summary>
        Replaced,
        /// <summary>The file exists but could not be read; it is left alone and no device id is used.</summary>
        Unreadable,
        /// <summary>A new device id could not be saved, so none is used: an id that changed every launch would show one player as many.</summary>
        CouldNotSave
    }

    /// <summary>This install's device id, kept in a small file so it stays the same from one launch to the next.</summary>
    internal sealed class ProtokitePlaytestDeviceIdFile
    {
        private const string TemporarySuffix = ".tmp";
        private static readonly TimeSpan TemporaryFileAge = TimeSpan.FromMinutes(1);

        internal ProtokitePlaytestDeviceIdFile(string path) => Path = path;

        internal string Path { get; }

        /// <summary>ProtokitePlaytest/device_id.txt in the game's persistent data folder.</summary>
        internal static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, "ProtokitePlaytest", "device_id.txt");

        /// <summary>Reads the device id (a lower-case GUID), making one when there is none; empty unless Read, Created or Replaced.</summary>
        internal ProtokitePlaytestDeviceIdFileResult ReadOrCreate(out string deviceId)
        {
            deviceId = "";
            DeleteLeftOverTemporaryFiles();
            if (!File.Exists(Path))
                return SaveNewDeviceId(out deviceId) ? ProtokitePlaytestDeviceIdFileResult.Created : ProtokitePlaytestDeviceIdFileResult.CouldNotSave;

            string contents;
            try
            {
                contents = File.ReadAllText(Path);
            }
            catch (Exception)
            {
                // It may be held for a moment by something else. Replacing it would change this install's id for good.
                return ProtokitePlaytestDeviceIdFileResult.Unreadable;
            }
            if (IsDeviceId(contents))
            {
                deviceId = contents;
                return ProtokitePlaytestDeviceIdFileResult.Read;
            }
            return SaveNewDeviceId(out deviceId) ? ProtokitePlaytestDeviceIdFileResult.Replaced : ProtokitePlaytestDeviceIdFileResult.CouldNotSave;
        }

        /// <summary>A lower-case GUID with hyphens, and nothing around it.</summary>
        internal static bool IsDeviceId(string text)
            => Guid.TryParseExact(text, "D", out Guid guid) && guid != Guid.Empty && guid.ToString("D") == text;

        // Written to a temporary file of its own and moved into place, so a crash part-way never leaves half an id, and two
        // launches saving at once never share one. Whatever the file holds afterwards is the id used: when another launch
        // moved its id in first, this launch takes that one.
        private bool SaveNewDeviceId(out string deviceId)
        {
            deviceId = "";
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(temporary, Guid.NewGuid().ToString("D"));
                if (File.Exists(Path))
                    File.Replace(temporary, Path, null);
                else
                    File.Move(temporary, Path);
            }
            catch (Exception)
            {
                TryDelete(temporary);
                // Another launch may have saved its id between the check and the move; that one is then read below.
                if (!File.Exists(Path))
                    return false;
            }

            try
            {
                string contents = File.ReadAllText(Path);
                if (!IsDeviceId(contents))
                    return false;
                deviceId = contents;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A launch that crashed between writing a new id and moving it leaves its temporary file. A fresh one may belong to a
        // launch saving right now, so only old ones go.
        private void DeleteLeftOverTemporaryFiles()
        {
            string folder = System.IO.Path.GetDirectoryName(Path);
            if (!Directory.Exists(folder))
                return;
            string prefix = System.IO.Path.GetFileName(Path) + ".";
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, prefix + "*" + TemporarySuffix);
            }
            catch (Exception)
            {
                // A folder that cannot be listed is swept by a later launch; reading the id does not depend on it.
                return;
            }
            foreach (string file in files)
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TemporaryFileAge)
                    TryDelete(file);
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // Left for a later sweep.
            }
        }
    }

    /// <summary>Checks on identifiers other systems issued.</summary>
    internal static class ProtokitePlaytestIds
    {
        /// <summary>Not empty, not too long, no whitespace. Never trims: whitespace in an issued id means something went wrong there.</summary>
        internal static bool IsUsable(string id, int maxLength)
        {
            if (string.IsNullOrEmpty(id) || id.Length > maxLength)
                return false;
            foreach (char c in id)
            {
                if (char.IsWhiteSpace(c))
                    return false;
            }
            return true;
        }
    }
}
