using System;
using System.IO;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Flock
{
    /// <summary>Every change the SDK makes to its saved files. A WebGL player keeps files in memory and copies them to the browser's storage only when asked, so each change here asks, once it is made.</summary>
    internal static class FlockSavedFiles
    {
        /// <summary>Runs on every request to copy the files to browser storage, on every platform, with the path that changed.</summary>
        internal static Action<string> CopyToBrowserStorageForTesting;

        internal static void WriteText(string path, string text)
        {
            try { File.WriteAllText(path, text); }
            finally { CopyToBrowserStorage(path); }
        }

        internal static void WriteBytes(string path, byte[] bytes)
        {
            try { File.WriteAllBytes(path, bytes); }
            finally { CopyToBrowserStorage(path); }
        }

        internal static void Move(string fromPath, string toPath)
        {
            try { File.Move(fromPath, toPath); }
            finally { CopyToBrowserStorage(toPath); }
        }

        /// <summary>Moves the file over one that is there, in one step, keeping no backup.</summary>
        internal static void Replace(string fromPath, string toPath)
        {
            try { File.Replace(fromPath, toPath, null); }
            finally { CopyToBrowserStorage(toPath); }
        }

        internal static void Delete(string path)
        {
            try { File.Delete(path); }
            finally { CopyToBrowserStorage(path); }
        }

        internal static void SetLastWriteTime(string path, DateTime utcTime)
        {
            try { File.SetLastWriteTimeUtc(path, utcTime); }
            finally { CopyToBrowserStorage(path); }
        }

        internal static void CreateFolder(string path)
        {
            try { Directory.CreateDirectory(path); }
            finally { CopyToBrowserStorage(path); }
        }

        internal static void DeleteFolder(string path, bool withEverythingInIt)
        {
            try { Directory.Delete(path, withEverythingInIt); }
            finally { CopyToBrowserStorage(path); }
        }

        /// <summary>Opens a lock file shared with nobody, for as long as the stream stays open.</summary>
        internal static FileStream OpenLock(string path, FileMode mode)
        {
            try { return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None); }
            finally { CopyToBrowserStorage(path); }
        }

        // Asks a WebGL player to copy the saved files to the browser's storage, where they outlive the page; does nothing elsewhere.
        private static void CopyToBrowserStorage(string changedPath)
        {
            CopyToBrowserStorageForTesting?.Invoke(changedPath);
#if UNITY_WEBGL && !UNITY_EDITOR
            FlockCopySavedFilesToBrowserStorage();
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void FlockCopySavedFilesToBrowserStorage();
#endif
    }
}
