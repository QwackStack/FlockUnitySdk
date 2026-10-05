using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Flock;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Protokite.Playtest.Tests
{
    /// <summary>Every change to the playtest's saved files asks a WebGL player to copy them to browser storage, after the change is made.</summary>
    public class ProtokitePlaytestSavedFilesTests
    {
        private string _folder;
        private readonly List<string> _seenAtEachCopy = new List<string>();
        private readonly List<ProtokitePlaytestConsentChoice> _answerAtEachCopy = new List<ProtokitePlaytestConsentChoice>();

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "protokite_saved_files_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _seenAtEachCopy.Clear();
            _answerAtEachCopy.Clear();
            // Only this test's own changes: another test's sending may still be finishing.
            ProtokitePlaytestSavedFiles.CopyToBrowserStorageForTesting = changedPath =>
            {
                if (!changedPath.StartsWith(_folder, StringComparison.Ordinal))
                    return;
                lock (_seenAtEachCopy)
                {
                    _seenAtEachCopy.Add(WhatIsOnDisk());
                    _answerAtEachCopy.Add(new ProtokitePlaytestConsentFile(In("playtest_consent.json")).Read());
                }
            };
        }

        [TearDown]
        public void TearDown()
        {
            ProtokitePlaytestSavedFiles.CopyToBrowserStorageForTesting = null;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        // The files and folders under the test's folder, as one sorted line, so each copy records what it would have copied.
        private string WhatIsOnDisk() =>
            string.Join(",", Directory.GetFileSystemEntries(_folder, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(_folder.Length + 1).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal));

        private string In(string name) => Path.Combine(_folder, name);

        private static ProtokitePlaytestFormSubmission Submission() => new ProtokitePlaytestFormSubmission
        {
            PlaytestSessionId = "",
            DeviceId = "device",
            ProtokiteApiUrl = "http://protokite.test",
            FlockGameVersionId = "test-gvid",
            Answers = new JObject { ["steps"] = "jump" }
        };

        [Test]
        public void EveryChangeAsksForACopyOnceItIsMade()
        {
            ProtokitePlaytestSavedFiles.CreateFolder(In("forms"));
            ProtokitePlaytestSavedFiles.WriteText(In("forms/a.json"), "a");
            ProtokitePlaytestSavedFiles.WriteText(In("forms/b.tmp"), "b");
            ProtokitePlaytestSavedFiles.Move(In("forms/b.tmp"), In("forms/b.json"));
            ProtokitePlaytestSavedFiles.WriteText(In("forms/c.tmp"), "c");
            ProtokitePlaytestSavedFiles.Replace(In("forms/c.tmp"), In("forms/a.json"));
            ProtokitePlaytestSavedFiles.SetAttributes(In("forms/a.json"), FileAttributes.ReadOnly);
            ProtokitePlaytestSavedFiles.SetAttributes(In("forms/a.json"), FileAttributes.Normal);
            ProtokitePlaytestSavedFiles.Delete(In("forms/a.json"));
            ProtokitePlaytestSavedFiles.Delete(In("forms/b.json"));

            CollectionAssert.AreEqual(new[]
            {
                "forms",
                "forms,forms/a.json",
                "forms,forms/a.json,forms/b.tmp",
                "forms,forms/a.json,forms/b.json",
                "forms,forms/a.json,forms/b.json,forms/c.tmp",
                "forms,forms/a.json,forms/b.json",
                "forms,forms/a.json,forms/b.json",
                "forms,forms/a.json,forms/b.json",
                "forms,forms/b.json",
                "forms"
            }, _seenAtEachCopy, "One copy per change, each seeing the change made");
        }

        [Test]
        public void TextIsWrittenAsUtf8WithNoByteOrderMark()
        {
            ProtokitePlaytestSavedFiles.WriteText(In("a.txt"), "é");
            CollectionAssert.AreEqual(new byte[] { 0xC3, 0xA9 }, File.ReadAllBytes(In("a.txt")), "The bytes every store wrote before");
        }

        [Test]
        public void AChangeThatFailsStillAsksForACopy()
        {
            Assert.Throws<FileNotFoundException>(() => ProtokitePlaytestSavedFiles.Move(In("missing.txt"), In("moved.txt")));
            Assert.AreEqual(1, _seenAtEachCopy.Count, "A change that fails part-way may have changed something");
        }

        // The case that matters most: a player who replaces their answer, or takes it back, must not be collected from under the old one next visit.
        [Test]
        public void AnAnswerThePlayerReplacedOrTookBackIsGoneFromTheLastCopy()
        {
            ProtokitePlaytestConsentFile file = new ProtokitePlaytestConsentFile(In("playtest_consent.json"));
            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.VideoAndPlayData));
            Assert.AreEqual(ProtokitePlaytestConsentChoice.VideoAndPlayData, _answerAtEachCopy.Last(), "Precondition: the first answer was copied");

            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.Nothing));
            Assert.AreEqual(ProtokitePlaytestConsentChoice.Nothing, _answerAtEachCopy.Last(), "The answer that replaced it is what the browser keeps");
            Assert.AreEqual("playtest_consent.json", _seenAtEachCopy.Last(), "No temporary file left in the last copy");

            Assert.IsTrue(file.Save(ProtokitePlaytestConsentChoice.NotAnswered));
            Assert.AreEqual("", _seenAtEachCopy.Last(), "An answer taken back is gone from the last copy");
        }

        [Test]
        public void ANewDeviceIdIsInTheLastCopy()
        {
            ProtokitePlaytestDeviceIdFile file = new ProtokitePlaytestDeviceIdFile(In("device_id.txt"));
            Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Created, file.ReadOrCreate(out string created));
            Assert.AreEqual("device_id.txt", _seenAtEachCopy.Last(), "The new id, and no temporary file, in the last copy");

            File.WriteAllText(In("device_id.txt"), "not a device id");
            int copiesBefore = _seenAtEachCopy.Count;
            Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Replaced, file.ReadOrCreate(out string replaced));
            Assert.AreNotEqual(created, replaced);
            Assert.Greater(_seenAtEachCopy.Count, copiesBefore, "Precondition: the replacement asked for copies");
            Assert.AreEqual("device_id.txt", _seenAtEachCopy.Last(), "The replacement moved in before the last copy");

            int copiesAfterReplacing = _seenAtEachCopy.Count;
            Assert.AreEqual(ProtokitePlaytestDeviceIdFileResult.Read, file.ReadOrCreate(out string read));
            Assert.AreEqual(replaced, read);
            Assert.AreEqual(copiesAfterReplacing, _seenAtEachCopy.Count, "Reading changes nothing, so asks for nothing");
        }

        [Test]
        public void AKeptFormIsInTheLastCopyAndOneForgottenIsNot()
        {
            ProtokitePlaytestKeptForms forms = new ProtokitePlaytestKeptForms(In("FeedbackForms"));
            string path = forms.Keep(Submission(), out string error);
            Assert.IsNotNull(path, error);
            string kept = "FeedbackForms,FeedbackForms/" + Path.GetFileName(path);
            Assert.AreEqual(kept, _seenAtEachCopy.Last(), "The whole form, and no temporary file, in the last copy");

            // Let go however the test ends, so a failure here is reported rather than the folder's delete in TearDown.
            using (FileStream claim = ProtokitePlaytestKeptForms.Claim(path))
            {
                Assert.IsNotNull(claim);
                Assert.IsTrue(ProtokitePlaytestKeptForms.ForgetClaimed(claim, path));
            }
            Assert.AreEqual("FeedbackForms", _seenAtEachCopy.Last(), "A form sent is gone from the last copy, so a later visit does not send it again");
        }

        [Test]
        public void TemporaryFilesSweptAwayAreGoneFromTheLastCopy()
        {
            Directory.CreateDirectory(In("FeedbackForms"));
            string stray = In("FeedbackForms/20260929-100000-000-aaaaaaaa.json.0123.tmp");
            File.WriteAllText(stray, "{");
            File.SetLastWriteTimeUtc(stray, DateTime.UtcNow - TimeSpan.FromMinutes(2));

            string path = new ProtokitePlaytestKeptForms(In("FeedbackForms")).Keep(Submission(), out string error);
            Assert.IsNotNull(path, error);
            Assert.IsFalse(File.Exists(stray), "Precondition: the old temporary file was swept");
            // Before the new form's own temporary file is written: the only copy that can see the folder empty is the sweep's.
            CollectionAssert.Contains(_seenAtEachCopy, "FeedbackForms", "The sweep's delete asked for a copy once made");
            StringAssert.DoesNotContain(".tmp", _seenAtEachCopy.Last());
        }

        // Static File and Directory changes, opens that can write or create (opening an existing file only to read changes nothing),
        // and FileInfo or DirectoryInfo changes. Their Create and Replace share names with other types' calls, so they are found only
        // on a FileInfo or DirectoryInfo made on the same line.
        private static readonly Regex FileChange = new Regex(
            @"(?<![\w.])(File\.(Delete|Move|Replace|WriteAll\w*|AppendAll\w*|AppendText|Copy|Create|CreateText|OpenWrite|Set\w+)\(|Directory\.(Delete|Move|CreateDirectory|Set\w+)\()"
            + @"|(new FileStream|(?<![\w.])File\.Open)\((?![^;]*FileMode\.Open\b[^;]*FileAccess\.Read\b)|new StreamWriter\(|\.(MoveTo|CopyTo|CreateSubdirectory)\(|\.Delete\((true|false)?\)"
            + @"|\.(CreateText|AppendText|OpenWrite)\(\)|\.Open\(FileMode(?!\.Open\b[^;]*FileAccess\.Read\b)|(FileInfo|DirectoryInfo)\([^;]*\)\.(Create|Replace)\(");

        private static string RuntimeFolder()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtokitePlaytest).Assembly);
            if (package == null)
                Assert.Ignore("The playtest is not installed as a package here, so its source cannot be read.");
            return Path.Combine(package.resolvedPath, "Runtime");
        }

        // Each code line (comments left out) that matches, as "file:line: text".
        private static List<string> LinesMatching(string runtime, IEnumerable<string> files)
        {
            List<string> found = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].TrimStart().StartsWith("//") && FileChange.IsMatch(lines[i]))
                        found.Add($"{file.Substring(runtime.Length + 1).Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
                }
            }
            return found;
        }

        // Recording runs on 64-bit Windows only, and nothing under Video/ is reached in a WebGL player, so its files are left out.
        [Test]
        public void NothingTheWebPlayerRunsChangesASavedFileExceptThroughProtokitePlaytestSavedFiles()
        {
            string runtime = RuntimeFolder();
            string[] every = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories);
            string owner = every.Single(file => Path.GetFileName(file) == "ProtokitePlaytestSavedFiles.cs");
            string[] video = every.Where(file => file.Substring(runtime.Length + 1).Replace('\\', '/').StartsWith("Video/", StringComparison.Ordinal)).ToArray();

            Assert.GreaterOrEqual(LinesMatching(runtime, new[] { owner }).Count, 6, "Control: the scan finds the changes ProtokitePlaytestSavedFiles makes");
            Assert.IsNotEmpty(LinesMatching(runtime, video), "Control: the files left out are where this scan expects, and do change files");
            Assert.IsTrue(FileChange.IsMatch("new FileInfo(path).Delete();") && FileChange.IsMatch("File.Open(path, FileMode.Open)")
                && FileChange.IsMatch("new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)")
                && FileChange.IsMatch("new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read)"), "Control: changes, and opens that can write or create, are found");
            Assert.IsTrue(FileChange.IsMatch("StreamWriter writer = info.CreateText();") && FileChange.IsMatch("info.Open(FileMode.Append)")
                && FileChange.IsMatch("new DirectoryInfo(folder).Create();") && FileChange.IsMatch("new FileInfo(from).Replace(to, null);"), "Control: FileInfo and DirectoryInfo changes are found");
            Assert.IsFalse(FileChange.IsMatch("ProtokitePlaytestSavedFiles.Delete(path);") || FileChange.IsMatch("File.ReadAllText(path)")
                || FileChange.IsMatch("new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete)") || FileChange.IsMatch("info.Open(FileMode.Open, FileAccess.Read)")
                || FileChange.IsMatch("using (SHA1 sha = SHA1.Create())") || FileChange.IsMatch("path.Replace('a', 'b')"), "Control: reads, the owner and other types' calls are not");

            string[] scanned = every.Except(video).Where(file => file != owner).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "ProtokitePlaytestConsent.cs", "ProtokitePlaytestIdentity.cs", "ProtokitePlaytestFormSubmission.cs", "ProtokitePlaytestForms.cs" },
                scanned.Select(Path.GetFileName).ToArray(), "Control: the files that keep the playtest's saved files are scanned");
            List<string> found = LinesMatching(runtime, scanned);
            Assert.IsEmpty(found, "Change saved files through ProtokitePlaytestSavedFiles, so a WebGL player copies them to browser storage:\n" + string.Join("\n", found));
        }

        // The page keeps one queue of copies, the Flock SDK's, so the playtest asks through the SDK's own call; a rename there would otherwise
        // show only as a WebGL build that fails to link.
        [Test]
        public void TheCopyIsAskedThroughTheCallTheFlockSdkDefines()
        {
            string ownerSource = File.ReadAllText(Path.Combine(RuntimeFolder(), "ProtokitePlaytestSavedFiles.cs"));
            MatchCollection imported = Regex.Matches(ownerSource, @"\[DllImport\(""__Internal""\)\]\s*private static extern void (\w+)\(\);");
            Assert.AreEqual(1, imported.Count, "Control: the one call the playtest imports is found");
            string call = imported[0].Groups[1].Value;
            Assert.AreEqual(2, Regex.Matches(ownerSource, @"\b" + Regex.Escape(call) + @"\(\);").Count, "Declared once and called once: the imported call is what asks for the copy");

            UnityEditor.PackageManager.PackageInfo flock = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FlockClient).Assembly);
            if (flock == null)
                Assert.Ignore("The Flock SDK is not installed as a package here, so its WebGL library cannot be read.");
            string[] libraries = Directory.GetFiles(Path.Combine(flock.resolvedPath, "Runtime"), "*.jslib", SearchOption.AllDirectories);
            Assert.IsNotEmpty(libraries, "Control: the Flock SDK's WebGL libraries are where this test expects");
            Assert.IsTrue(libraries.Any(library => Regex.IsMatch(File.ReadAllText(library), @"(?m)^\s*" + Regex.Escape(call) + @"\s*:\s*function\b")),
                $"The Flock SDK defines {call}, the call the playtest asks for its copies through");
        }
    }
}
