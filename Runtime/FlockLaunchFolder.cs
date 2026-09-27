using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flock
{
    /// <summary>A folder one launch of the game keeps its own files in, held by a lock file that no other launch can open while this one runs.</summary>
    // Whether a launch still runs cannot be read from a time or a process id. The owner keeps the lock file open, shared
    // with nobody, and the operating system lets go of it however the owner ends. Another launch touches the folder only
    // after opening that lock itself.
    internal sealed class FlockLaunchFolder : IDisposable
    {
        internal const string LockFileName = "in-use.lock";

        private FileStream _lock;

        /// <summary>The folder's full path.</summary>
        internal string Path { get; }

        private FlockLaunchFolder(string path, FileStream folderLock)
        {
            Path = path;
            _lock = folderLock;
        }

        /// <summary>Makes a folder of its own under the parent, named for the time and eight random hex digits, and holds its lock. Null when it could not.</summary>
        internal static FlockLaunchFolder Create(string parentFolder, string namePrefix = "")
        {
            string name = namePrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(parentFolder, name));
            try
            {
                Directory.CreateDirectory(folder);
                FileStream folderLock = OpenLock(System.IO.Path.Combine(folder, LockFileName), FileMode.OpenOrCreate);
                return new FlockLaunchFolder(folder, folderLock);
            }
            catch (Exception)
            {
                // A folder with no lock is never claimed, so it would stay forever.
                try { Directory.Delete(folder, true); }
                catch (Exception) { }
                return null;
            }
        }

        /// <summary>Holds the folder when the launch that made it has let go of its lock. Null while the lock is still held, and for a folder with no lock.</summary>
        internal static FlockLaunchFolder ClaimEnded(string folder)
        {
            string fullFolder = System.IO.Path.GetFullPath(folder);
            string lockPath = System.IO.Path.Combine(fullFolder, LockFileName);
            // A folder with no lock was not made by a launch, or its launch already deleted everything but the folder.
            if (!File.Exists(lockPath))
                return null;

            try
            {
                return new FlockLaunchFolder(fullFolder, OpenLock(lockPath, FileMode.Open));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The folders directly under the parent, as full paths, oldest name first.</summary>
        internal static List<string> FindFolders(string parentFolder)
        {
            if (!Directory.Exists(parentFolder))
                return new List<string>();

            List<string> folders = Directory.EnumerateDirectories(parentFolder).Select(System.IO.Path.GetFullPath).ToList();
            folders.Sort((a, b) => string.CompareOrdinal(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            return folders;
        }

        /// <summary>Deletes everything in the folder, its lock last, then the folder, and lets go of it. When anything could not be deleted the lock file stays, so a later launch finds the folder again. Returns whether the folder is gone.</summary>
        internal bool DeleteEverything()
        {
            bool everythingElseDeleted = true;
            try
            {
                foreach (string file in Directory.EnumerateFiles(Path).ToList())
                {
                    if (string.Equals(System.IO.Path.GetFileName(file), LockFileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(file); }
                    catch (Exception) { everythingElseDeleted = false; }
                }
                foreach (string subfolder in Directory.EnumerateDirectories(Path).ToList())
                {
                    try { Directory.Delete(subfolder, true); }
                    catch (Exception) { everythingElseDeleted = false; }
                }
            }
            catch (Exception)
            {
                everythingElseDeleted = false;
            }

            Dispose();
            if (!everythingElseDeleted)
                return false;

            try
            {
                File.Delete(System.IO.Path.Combine(Path, LockFileName));
                Directory.Delete(Path, false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Lets go of the lock and leaves every file where it is, the way a launch that crashed looks to the next one.</summary>
        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }

        // Shared with nobody: while this handle is open, no other launch can open the lock.
        private static FileStream OpenLock(string lockPath, FileMode mode)
        {
            return new FileStream(lockPath, mode, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
