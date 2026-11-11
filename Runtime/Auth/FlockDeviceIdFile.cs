using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Flock.Auth
{
    /// <summary>This install's device id: a GUID the SDK makes once and keeps in Flock/device_id.txt, read once a launch.</summary>
    // Every copy of the game on a machine (the Editor and a built player too) shares the file, so the first launch to make it
    // wins: the file is only ever made new, in one write, never written over, and a launch that finds it made reads that id.
    // A file holding no GUID at all is moved aside before a new one is made, and a launch that moved another launch's new id
    // aside puts it back. An id written another way (upper case, a line break or a byte-order mark around it) is used as
    // written, since the server may already know it so.
    internal static class FlockDeviceIdFile
    {
        internal const string FileName = "device_id.txt";

        // A file this young that is empty, part of an id or held may be another launch's save under way, so it is read again.
        private static readonly TimeSpan YoungFile = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(1);
        private const int Attempts = 4;

        private static readonly object Gate = new object();
        private static string _id;
        private static string _folderForTesting;

        /// <summary>Runs once, just before the next save (a new file made or one moved aside), where a test has another launch save first.</summary>
        internal static Action BeforeNextSaveForTesting;

        // With domain reload off, a new play session would otherwise keep the last one's id after its file was deleted.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (Gate)
                _id = null;
        }

        /// <summary>Keeps the file in this folder instead of the game's and forgets the id read, the way a new launch starts; null goes back to the game's.</summary>
        internal static void UseFolderForTesting(string folder)
        {
            lock (Gate)
            {
                _folderForTesting = folder;
                _id = null;
            }
        }

        /// <summary>The id, read or made the first time it is asked for and the same for the rest of the launch; <paramref name="warn"/> hears why when it cannot be kept.</summary>
        internal static string Read(Action<string> warn)
        {
            lock (Gate)
            {
                if (_id == null)
                    _id = ReadOrMake(Path.Combine(_folderForTesting ?? FlockUtil.FlockFilePath, FileName), warn);
                return _id;
            }
        }

        /// <summary>The GUID the text holds, trimmed of whitespace and a byte-order mark but otherwise as written; null when it holds none.</summary>
        internal static string IdIn(string text)
        {
            string trimmed = Trimmed(text);
            return Guid.TryParse(trimmed, out Guid _) ? trimmed : null;
        }

        private static string ReadOrMake(string path, Action<string> warn)
        {
            DeleteLeftOverTemporaryFiles(path);
            string newId = Guid.NewGuid().ToString("D");
            string failure = null;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                string found = ReadWhenSettled(path, out bool unreadable, out string readFailure);
                if (found != null)
                    return found;
                if (unreadable)
                {
                    // It may be held by something else, and replacing it would change this install's id for good.
                    warn?.Invoke($"The device id file {path} is there but could not be read ({readFailure}). This launch uses a new device id that is not saved, " +
                                 "so a device sign-in now is not this device's usual player. Close whatever holds the file (another copy of the game, a backup or " +
                                 "antivirus tool) and start the game again.");
                    return newId;
                }
                if (File.Exists(path))
                {
                    MoveAside(path, out failure);
                    continue;
                }
                if (TryCreate(path, newId, out failure))
                    return newId;
                // Still no file: nothing can be saved there. Otherwise another launch made it first, and the next pass reads it.
                if (!File.Exists(path))
                    break;
            }
            warn?.Invoke($"A new device id could not be saved to {path} ({failure ?? "the file kept changing under another copy of the game"}). This launch uses it " +
                         "until it ends and the next launch makes another, so each launch signs in as a new player. Let the game write to that folder, or pass a " +
                         "device id of your own to the device sign-in calls.");
            return newId;
        }

        // Null when there is no file or it holds no GUID; unreadable when it is there but cannot be read.
        private static string ReadWhenSettled(string path, out bool unreadable, out string failure)
        {
            DateTime giveUpAt = DateTime.UtcNow + LongestWait;
            while (true)
            {
                unreadable = false;
                failure = null;
                if (!File.Exists(path))
                    return null;
                string contents = null;
                try
                {
                    contents = File.ReadAllText(path);
                    string id = IdIn(contents);
                    if (id != null)
                        return id;
                }
                catch (Exception ex)
                {
                    if (!File.Exists(path))
                        return null;
                    unreadable = true;
                    failure = ex.Message;
                }
                bool mayBeUnderWay = unreadable || CouldBePartOfAnId(contents);
                if (!mayBeUnderWay || !CanWait || DateTime.UtcNow >= giveUpAt || !IsYoung(path))
                    return null;
                Thread.Sleep(20);
            }
        }

        // Made new in one write and never over a file that is there: of two launches making it at once, exactly one succeeds.
        private static bool TryCreate(string path, string id, out string failure)
        {
            failure = null;
            try
            {
                FlockSavedFiles.CreateFolder(Path.GetDirectoryName(path));
                Interlocked.Exchange(ref BeforeNextSaveForTesting, null)?.Invoke();
                FlockSavedFiles.CreateNew(path, id);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        // Moves a file holding no GUID out of the way. When what was moved holds an id after all (another launch made it in
        // between, or was still writing it), that id is put back.
        private static void MoveAside(string path, out string failure)
        {
            failure = null;
            string aside = FlockTemporaryFiles.MakePath(path);
            try
            {
                Interlocked.Exchange(ref BeforeNextSaveForTesting, null)?.Invoke();
                FlockSavedFiles.Move(path, aside);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return;
            }
            string movedId = ReadWhenSettled(aside, out bool _, out string _);
            // A put-back that fails leaves the moved file to the sweep; whoever made the file meanwhile is read next.
            if (movedId == null || TryCreate(path, movedId, out failure))
                TryDelete(aside);
        }

        private static bool CanWait
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                // One page writes its own files; a young empty file there is one a closed page left.
                return false;
#else
                return true;
#endif
            }
        }

        private static bool IsYoung(string path)
        {
            try
            {
                return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < YoungFile;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Empty, or the start of an id written so far.
        private static bool CouldBePartOfAnId(string contents)
        {
            if (contents == null)
                return false;
            string trimmed = Trimmed(contents);
            if (trimmed.Length >= 36)
                return false;
            foreach (char c in trimmed)
            {
                if (!Uri.IsHexDigit(c) && c != '-')
                    return false;
            }
            return true;
        }

        private static string Trimmed(string text) => text.Trim().Trim('﻿').Trim();

        // A launch that ended between moving a file aside and deleting it leaves it; a fresh one may be another launch's under
        // way, so only old ones go.
        private static void DeleteLeftOverTemporaryFiles(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder))
                    return;
                foreach (string file in Directory.GetFiles(folder, Path.GetFileName(path) + ".*" + FlockTemporaryFiles.Extension))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > FlockTemporaryFiles.LeftOverAfter)
                        TryDelete(file);
                }
            }
            catch (Exception)
            {
                // Swept by a later launch; reading the id does not depend on it.
            }
        }

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                    FlockSavedFiles.Delete(file);
            }
            catch (Exception)
            {
                // Left for a later sweep.
            }
        }
    }
}
