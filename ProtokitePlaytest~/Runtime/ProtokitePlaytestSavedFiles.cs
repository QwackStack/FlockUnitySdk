using System;
using System.IO;
using System.Text;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Protokite.Playtest
{
    /// <summary>Every change the playtest makes to its saved files. A WebGL player keeps files in memory and copies them to the browser's storage only when asked, so each change here asks, once it is made.</summary>
    internal static class ProtokitePlaytestSavedFiles
    {
        private static readonly UTF8Encoding NoByteOrderMark = new UTF8Encoding(false);

        /// <summary>Runs on every request to copy the files to browser storage, on every platform, with the path that changed.</summary>
        internal static Action<string> CopyToBrowserStorageForTesting;

        internal static void WriteText(string path, string text)
        {
            try { File.WriteAllText(path, text, NoByteOrderMark); }
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

        internal static void SetAttributes(string path, FileAttributes attributes)
        {
            try { File.SetAttributes(path, attributes); }
            finally { CopyToBrowserStorage(path); }
        }

        internal static void CreateFolder(string path)
        {
            try { Directory.CreateDirectory(path); }
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
        // The Flock SDK's own call, so the page keeps one queue of copies: two queues could each copy at once, and an older copy
        // finishing last would put back a file deleted since.
        [DllImport("__Internal")]
        private static extern void FlockCopySavedFilesToBrowserStorage();
#endif
    }
}
