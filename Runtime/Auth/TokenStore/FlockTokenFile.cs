using System;
using System.IO;
using System.Text;

namespace Flock.Auth
{
    /// <summary>
    /// Both tokens in one file, sealed by the platform store's own protection, written whole to a temporary file and moved
    /// into place: a game ended part-way through a save keeps the pair it had, never a new access token beside an old refresh
    /// token. Earlier versions kept the two tokens in two files; those are read when the one file is not there yet, and
    /// removed by the next save.
    /// </summary>
    internal sealed class FlockTokenFile
    {
        internal const string FileName = "TokenData";
        private const string AccessFileName = "AccessData";
        private const string RefreshFileName = "RefreshData";
        private static readonly TimeSpan LeftOverAfter = TimeSpan.FromMinutes(1);

        private readonly string _folder;
        private readonly Func<byte[], byte[]> _seal;
        private readonly Func<byte[], byte[]> _open;
        private readonly Action<string> _warn;

        /// <param name="warn">Told, in one sentence, when a file that is there cannot be read as a saved sign-in; null says nothing.</param>
        internal FlockTokenFile(string folder, Func<byte[], byte[]> seal, Func<byte[], byte[]> open, Action<string> warn = null)
        {
            _folder = folder;
            _seal = seal;
            _open = open;
            _warn = warn;
        }

        internal string FilePath => Path.Combine(_folder, FileName);
        private string AccessFilePath => Path.Combine(_folder, AccessFileName);
        private string RefreshFilePath => Path.Combine(_folder, RefreshFileName);

        internal void Save(string accessToken, string refreshToken)
        {
            // A token never holds a line break, so one line each keeps them apart; a missing refresh token is kept as empty.
            byte[] sealedTokens = _seal(Encoding.UTF8.GetBytes((accessToken ?? string.Empty) + "\n" + (refreshToken ?? string.Empty)));
            FlockSavedFiles.CreateFolder(_folder);
            DeleteLeftOverTemporaryFiles(olderOnly: true);
            string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            FlockSavedFiles.WriteBytes(temporary, sealedTokens);
            if (File.Exists(FilePath))
                FlockSavedFiles.Replace(temporary, FilePath);
            else
                FlockSavedFiles.Move(temporary, FilePath);
            DeleteIfThere(AccessFilePath);
            DeleteIfThere(RefreshFilePath);
        }

        internal StoredTokens Load()
        {
            if (File.Exists(FilePath))
            {
                string[] lines = Encoding.UTF8.GetString(_open(File.ReadAllBytes(FilePath))).Split('\n');
                if (lines.Length != 2 || lines[0].Length == 0)
                {
                    // Said out loud: read as "no saved sign-in" with nothing in the log, a lost file looks like one never saved.
                    _warn?.Invoke($"The saved sign-in file {FilePath} could not be read as one: it holds {lines.Length} line(s) where 2 were expected" +
                                  (lines.Length == 2 ? " and the first is empty" : "") + ". The player signs in again.");
                    return null;
                }
                return new StoredTokens { AccessToken = lines[0], RefreshToken = lines[1].Length == 0 ? null : lines[1] };
            }
            if (!File.Exists(AccessFilePath) || !File.Exists(RefreshFilePath))
                return null;
            return new StoredTokens
            {
                AccessToken = Encoding.UTF8.GetString(_open(File.ReadAllBytes(AccessFilePath))),
                RefreshToken = Encoding.UTF8.GetString(_open(File.ReadAllBytes(RefreshFilePath)))
            };
        }

        internal void Clear()
        {
            DeleteIfThere(FilePath);
            DeleteIfThere(AccessFilePath);
            DeleteIfThere(RefreshFilePath);
            DeleteLeftOverTemporaryFiles(olderOnly: false);
        }

        // Another launch of the game may be writing one now, so a save leaves the recent ones alone.
        private void DeleteLeftOverTemporaryFiles(bool olderOnly)
        {
            if (!Directory.Exists(_folder))
                return;
            foreach (string file in Directory.GetFiles(_folder, FileName + ".*.tmp"))
            {
                if (olderOnly && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < LeftOverAfter)
                    continue;
                try { FlockSavedFiles.Delete(file); }
                catch (IOException) { }
            }
        }

        private static void DeleteIfThere(string path)
        {
            if (File.Exists(path))
                FlockSavedFiles.Delete(path);
        }
    }
}
