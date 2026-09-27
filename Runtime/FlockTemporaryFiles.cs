using System;
using System.IO;

namespace Flock
{
    /// <summary>Saves a file by writing a temporary file of its own beside it and moving that over, so a crash leaves the old file or the new one, never half of one.</summary>
    // Two copies of a game share every folder under persistentDataPath, so each write gets a temporary file no other
    // write uses, and a temporary file only counts as left over once it is older than LeftOverAfter: a fresh one may be
    // another process's write in progress.
    internal static class FlockTemporaryFiles
    {
        internal const string Extension = ".tmp";

        /// <summary>A temporary file older than this was left by a write that never finished.</summary>
        internal static readonly TimeSpan LeftOverAfter = TimeSpan.FromMinutes(1);

        private static Action<string> _beforeNextMove;

        /// <summary>A temporary path beside the destination that no other write uses. Touches nothing on disk.</summary>
        internal static string MakePath(string destinationPath)
        {
            return destinationPath + "." + Guid.NewGuid().ToString("N") + Extension;
        }

        /// <summary>Writes the text to a temporary file of its own and moves it over the destination. Throws when either step fails, after deleting the temporary file.</summary>
        internal static void Save(string destinationPath, string text)
        {
            string temporaryPath = MakePath(destinationPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                File.WriteAllText(temporaryPath, text);
                MoveIntoPlace(temporaryPath, destinationPath);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }

        /// <summary>Writes the bytes to a temporary file of its own and moves it over the destination. Throws when either step fails, after deleting the temporary file.</summary>
        internal static void Save(string destinationPath, byte[] bytes)
        {
            string temporaryPath = MakePath(destinationPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                File.WriteAllBytes(temporaryPath, bytes);
                MoveIntoPlace(temporaryPath, destinationPath);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }

        /// <summary>Writes the text over a destination that is still there, through a temporary file of its own. Throws, after deleting the temporary file, when the destination is gone.</summary>
        internal static void Replace(string destinationPath, string text)
        {
            string temporaryPath = MakePath(destinationPath);
            try
            {
                File.WriteAllText(temporaryPath, text);
                File.Replace(temporaryPath, destinationPath, null);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }

        /// <summary>Moves a finished temporary file over the destination in one step, replacing what is there.</summary>
        internal static void MoveIntoPlace(string temporaryPath, string destinationPath)
        {
            Action<string> hook = _beforeNextMove;
            _beforeNextMove = null;
            hook?.Invoke(temporaryPath);

            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, null);
                return;
            }

            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another writer put the destination there first; the newer save still wins.
                File.Replace(temporaryPath, destinationPath, null);
            }
        }

        /// <summary>Deletes the temporary files in the folder older than LeftOverAfter. Returns how many were deleted.</summary>
        internal static int DeleteLeftOverFiles(string folder, bool includeSubfolders)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(folder))
                    return 0;

                SearchOption depth = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                DateTime leftOverBefore = DateTime.UtcNow - LeftOverAfter;
                foreach (string path in Directory.EnumerateFiles(folder, "*" + Extension, depth))
                {
                    // Suffix checked by hand: the wildcard also matches longer extensions on Windows.
                    if (!path.EndsWith(Extension, StringComparison.Ordinal))
                        continue;
                    if (File.GetLastWriteTimeUtc(path) >= leftOverBefore)
                        continue;
                    if (TryDelete(path))
                        deleted++;
                }
            }
            catch (Exception)
            {
                // A folder another process is changing can fail part-way; the next sweep picks up the rest.
            }
            return deleted;
        }

        /// <summary>Whether the path is a temporary file this class names.</summary>
        internal static bool IsTemporaryFile(string path)
        {
            return path.EndsWith(Extension, StringComparison.Ordinal);
        }

        /// <summary>Runs the hook once, just before the next move into place, where a test starts a second writer.</summary>
        internal static void SetBeforeNextMoveForTesting(Action<string> hook)
        {
            _beforeNextMove = hook;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
