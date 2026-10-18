using System;
using System.IO;
using System.Linq;
using System.Text;
using Flock.Auth;
using NUnit.Framework;

namespace Flock.Tests.Editor
{
    // The Windows and Android token stores keep both tokens in one sealed file, written whole and moved into place. Each test
    // uses a folder of its own, never the game's: the editor and a Windows player of this project share persistentDataPath.
    public class FlockTokenFileTests
    {
        private string _folder;

        // Stands in for the platform's protection: reversible, and never the plain bytes.
        private static byte[] Seal(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
        private static byte[] Open(byte[] sealedBytes) => Seal(sealedBytes);

        private FlockTokenFile File() => new FlockTokenFile(_folder, null, Seal, Open);

        [SetUp]
        public void SetUp() => _folder = Path.Combine(Path.GetTempPath(), "FlockTokenFileTests", Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        // Both tokens go in one file, so a game ended part-way through a save keeps a pair it had.
        [Test]
        public void BothTokens_AreKeptInOneFile_AndReadBack()
        {
            File().Save("access-1", "refresh-1");

            Assert.AreEqual(new[] { FlockTokenFile.FileName }, Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray());
            StoredTokens loaded = File().Load();
            Assert.AreEqual("access-1", loaded.AccessToken);
            Assert.AreEqual("refresh-1", loaded.RefreshToken);
            Assert.IsFalse(Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(Path.Combine(_folder, FlockTokenFile.FileName))).Contains("access-1"),
                "The file holds the sealed bytes, not the tokens.");
        }

        // A save cut off after writing its temporary file and before moving it leaves the pair before it.
        [Test]
        public void ASaveCutOffBeforeItsMove_LeavesThePairBeforeIt()
        {
            File().Save("access-1", "refresh-1");
            System.IO.File.WriteAllBytes(Path.Combine(_folder, FlockTokenFile.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp"),
                Seal(Encoding.UTF8.GetBytes("access-2\nrefresh-2")));

            StoredTokens loaded = File().Load();
            Assert.AreEqual("access-1", loaded.AccessToken);
            Assert.AreEqual("refresh-1", loaded.RefreshToken);
        }

        // A sign-in with no refresh token is saved, not thrown away.
        [Test]
        public void AMissingRefreshToken_IsSavedAndReadBackAsMissing()
        {
            Assert.DoesNotThrow(() => File().Save("access-1", null));

            StoredTokens loaded = File().Load();
            Assert.AreEqual("access-1", loaded.AccessToken);
            Assert.IsNull(loaded.RefreshToken);
        }

        // An earlier version's two files are read until the next save, which replaces them with the one file.
        [Test]
        public void AnEarlierVersionsTwoFiles_AreReadThenReplacedByTheNextSave()
        {
            Directory.CreateDirectory(_folder);
            System.IO.File.WriteAllBytes(Path.Combine(_folder, "AccessData"), Seal(Encoding.UTF8.GetBytes("old-access")));
            System.IO.File.WriteAllBytes(Path.Combine(_folder, "RefreshData"), Seal(Encoding.UTF8.GetBytes("old-refresh")));

            StoredTokens loaded = File().Load();
            Assert.AreEqual("old-access", loaded.AccessToken);
            Assert.AreEqual("old-refresh", loaded.RefreshToken);

            File().Save("access-2", "refresh-2");
            Assert.AreEqual(new[] { FlockTokenFile.FileName }, Directory.GetFiles(_folder).Select(Path.GetFileName).ToArray());
            Assert.AreEqual("access-2", File().Load().AccessToken);
        }

        // Clearing removes the file, an earlier version's files and any save cut off part-way.
        [Test]
        public void Clear_RemovesEverySavedFile()
        {
            File().Save("access-1", "refresh-1");
            System.IO.File.WriteAllBytes(Path.Combine(_folder, "AccessData"), new byte[] { 1 });
            System.IO.File.WriteAllBytes(Path.Combine(_folder, FlockTokenFile.FileName + ".abc.tmp"), new byte[] { 1 });

            File().Clear();

            Assert.IsEmpty(Directory.GetFiles(_folder));
            Assert.IsNull(File().Load());
        }

        // A file that is there but cannot be read as a saved sign-in is said out loud: read silently as "no saved sign-in", a lost
        // file looks like one never saved.
        [Test]
        public void AFileThatCannotBeReadIsReportedAndReadsAsNoSignIn()
        {
            Directory.CreateDirectory(_folder);
            System.IO.File.WriteAllBytes(Path.Combine(_folder, FlockTokenFile.FileName), Seal(Encoding.UTF8.GetBytes("only-one-line")));
            string warned = null;
            FlockTokenFile file = new FlockTokenFile(_folder, null, Seal, Open, message => warned = message);

            Assert.IsNull(file.Load());
            StringAssert.Contains("1 line(s) where 2 were expected", warned);
            StringAssert.Contains(FlockTokenFile.FileName, warned);

            // The control: a whole file is read back with nothing said.
            warned = null;
            file.Save("access-1", "refresh-1");
            Assert.AreEqual("access-1", file.Load().AccessToken);
            Assert.IsNull(warned);
        }
    }
}
