using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Protokite.Playtest.Tests.ProtokitePlaytestPlantedRuns;

namespace Protokite.Playtest.Tests
{
    /// <summary>A recording's run folder, what a later launch does with runs whose launch ended, and the disk budget, on runs planted by hand.</summary>
    public class ProtokitePlaytestRecordingsFolderTests
    {
        private const ProtokitePlaytestRecordingKind Playtest = ProtokitePlaytestRecordingKind.Playtest;
        private const ProtokitePlaytestRecordingKind TestVideo = ProtokitePlaytestRecordingKind.TestVideo;

        private string _root;
        private string _scratch;
        private readonly List<IDisposable> _held = new List<IDisposable>();

        [SetUp]
        public void SetUp()
        {
            string folder = Path.Combine(Path.GetTempPath(), "protokite_recordings_" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(folder, "Recordings");
            _scratch = Path.Combine(folder, "Scratch");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_scratch);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (IDisposable held in _held)
                held.Dispose();
            _held.Clear();
            ProtokitePlaytestRecordingsFolder.BeforeNextMakingRoomForTesting = null;
            string folder = Path.GetDirectoryName(_root);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }

        private T Held<T>(T disposable) where T : class, IDisposable
        {
            if (disposable != null)
                _held.Add(disposable);
            return disposable;
        }

        private static long Total(params string[] runs) => runs.Sum(run => ProtokitePlaytestRecordingRun.BytesOnDisk(run));

        // A run's folder

        [Test]
        public void ARunHasAFolderOfItsOwnThatNoOtherLaunchCanOpenWhileItRuns()
        {
            ProtokitePlaytestRecordingRun run = Held(ProtokitePlaytestRecordingRun.Start(_root, Playtest, 12345, out string error));
            Assert.IsNotNull(run, error);
            StringAssert.IsMatch(@"^\d{8}-\d{6}-[0-9a-f]{8}$", run.Name, "Named for its UTC start and eight random hex digits");
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_root, "Playtest", run.Name)), run.FolderPath);
            Assert.AreEqual(Path.Combine(run.FolderPath, "recording-" + run.Name + ".webm"), run.VideoPath(".webm"));
            Assert.AreEqual("12345", File.ReadAllText(Path.Combine(run.FolderPath, "reserved-bytes.txt")));

            Assert.Catch<IOException>(() => new FileStream(Path.Combine(run.FolderPath, "in-use.lock"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete).Dispose(), "Nobody else opens the lock, whatever they would share");
            Assert.IsNull(ProtokitePlaytestRecordingRun.ClaimEnded(run.FolderPath, Playtest), "A run its launch still holds cannot be claimed");

            run.Dispose();
            using (ProtokitePlaytestRecordingRun claimed = ProtokitePlaytestRecordingRun.ClaimEnded(run.FolderPath, Playtest))
            {
                Assert.IsNotNull(claimed, "Once its launch lets go, a later one claims it");
                Assert.IsNull(ProtokitePlaytestRecordingRun.ClaimEnded(run.FolderPath, Playtest), "By one launch at a time");
            }
        }

        [Test]
        public void ATestVideoIsKeptApartFromPlaytestRecordings()
        {
            ProtokitePlaytestRecordingRun run = Held(ProtokitePlaytestRecordingRun.Start(_root, TestVideo, 1, out string error));
            Assert.IsNotNull(run, error);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_root, "TestVideos", run.Name)), run.FolderPath);
            Assert.AreEqual(Path.Combine(run.FolderPath, "test-recording-" + run.Name + ".webm"), run.VideoPath(".webm"));
        }

        [Test]
        public void TheSessionIsSavedWithWhatAnUploadNeedsAndReadBack()
        {
            ProtokitePlaytestRecordingRun run = Held(ProtokitePlaytestRecordingRun.Start(_root, Playtest, 1, out string error));
            Assert.IsTrue(run.SaveSession(new ProtokitePlaytestSavedSession
            {
                PlaytestSessionId = "pk-1", ProtokiteApiUrl = "http://protokite.test/", FlockGameVersionId = "gvid-1"
            }, out error), error);
            JObject saved = JObject.Parse(File.ReadAllText(Path.Combine(run.FolderPath, "session.json")));
            CollectionAssert.AreEquivalent(new[] { "protokite_session_id", "protokite_api_url", "flock_game_version_id" }, saved.Properties().Select(p => p.Name));
            Assert.AreEqual("pk-1", (string)saved["protokite_session_id"]);
            Assert.AreEqual("http://protokite.test/", (string)saved["protokite_api_url"]);
            Assert.AreEqual("gvid-1", (string)saved["flock_game_version_id"]);

            ProtokitePlaytestSavedSession read = ProtokitePlaytestRecordingRun.ReadSession(run.FolderPath, out string couldNotRead);
            Assert.IsNull(couldNotRead);
            Assert.AreEqual("pk-1", read.PlaytestSessionId);
            Assert.AreEqual("http://protokite.test/", read.ProtokiteApiUrl);
            Assert.AreEqual("gvid-1", read.FlockGameVersionId);

            Assert.IsTrue(run.SaveSession(new ProtokitePlaytestSavedSession { PlaytestSessionId = "pk-2", ProtokiteApiUrl = "http://protokite.test/" }, out error), error);
            saved = JObject.Parse(File.ReadAllText(Path.Combine(run.FolderPath, "session.json")));
            Assert.AreEqual("pk-2", (string)saved["protokite_session_id"], "Saved over the last one");
            Assert.IsNull(saved["flock_game_version_id"], "A build that sent no Game Version ID saves none, rather than an empty one");
            Assert.IsEmpty(Directory.GetFiles(run.FolderPath, "*.tmp"), "Each save's temporary file became the file");
        }

        // What a later launch does with runs whose launch ended

        [Test]
        public void ACutOffPlaytestRecordingIsFinishedAndKeptToBeUploaded()
        {
            string run = Plant(_root, Playtest, "20260101-000000-00000001", 1000000, "pk-1", cutOffVideo: CutOffVideo(_scratch, 5));
            ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);

            string video = VideoPath(run, Playtest);
            Assert.IsFalse(File.Exists(video + ".part"), "Finished, not left cut off");
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(video, out WebmFileRead read), "It reads as a whole WebM file");
            Assert.AreEqual(5, read.Frames.Count, "Every whole frame, without the one cut off");
            Assert.IsTrue(read.SegmentSizeWritten);
            Assert.AreEqual(1, found.CutOffVideosFinished);
            Assert.AreEqual(5, found.FramesKeptInFinishedVideos);
            Assert.AreEqual(1, found.RecordingsWaitingToUpload);
            Assert.IsTrue(File.Exists(Path.Combine(run, "session.json")), "Kept with its session");
            Assert.IsTrue(File.Exists(Path.Combine(run, "in-use.lock")), "Still a run a later launch finds");
        }

        [Test]
        public void APlaytestRecordingNoSessionStartedForIsDeletedCutOffOrNot()
        {
            string finished = Plant(_root, Playtest, "20260101-000000-00000001", finishedVideo: FinishedVideo(_scratch, 3));
            string cutOff = Plant(_root, Playtest, "20260101-000000-00000002", cutOffVideo: CutOffVideo(_scratch, 3));
            string noUsableSession = Plant(_root, Playtest, "20260101-000000-00000003", finishedVideo: FinishedVideo(_scratch, 3));
            File.WriteAllText(Path.Combine(noUsableSession, "session.json"), "{\"protokite_session_id\":\"\"}");

            ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsFalse(Directory.Exists(finished), "Nothing to upload it to");
            Assert.IsFalse(Directory.Exists(cutOff));
            Assert.IsFalse(Directory.Exists(noUsableSession), "A session file naming no session is no session");
            Assert.AreEqual(3, found.RecordingsDeletedWithNoSession);
            Assert.AreEqual(0, found.CutOffVideosFinished, "Deleted as it was, not finished first");
        }

        [Test]
        public void ATestVideoIsKeptWithNoSessionAndFinishedWhenCutOff()
        {
            string whole = Plant(_root, TestVideo, "20260101-000000-00000001", finishedVideo: FinishedVideo(_scratch, 3));
            string cutOff = Plant(_root, TestVideo, "20260101-000000-00000002", cutOffVideo: CutOffVideo(_scratch, 4));

            ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsTrue(File.Exists(VideoPath(whole, TestVideo)), "A test video needs no session");
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(VideoPath(cutOff, TestVideo), out WebmFileRead read));
            Assert.AreEqual(4, read.Frames.Count);
            Assert.AreEqual(2, found.TestVideosKept);
            Assert.AreEqual(1, found.CutOffVideosFinished);
            Assert.AreEqual(0, found.RecordingsDeletedWithNoSession);
        }

        [Test]
        public void ARunLeftWithNoVideoIsRemoved()
        {
            string sessionOnly = Plant(_root, Playtest, "20260101-000000-00000001", 10, "pk-1");
            string headerOnly = Plant(_root, Playtest, "20260101-000000-00000002", 10, "pk-2", cutOffVideo: CutOffVideo(_scratch, 0));
            string testVideoNothing = Plant(_root, TestVideo, "20260101-000000-00000003", 10);
            string temporaryFileOnly = Plant(_root, TestVideo, "20260101-000000-00000004");
            File.WriteAllText(Path.Combine(temporaryFileOnly, "reserved-bytes.txt.0123456789abcdef.tmp"), "10");

            ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsFalse(Directory.Exists(sessionOnly), "A session with no recording has nothing to upload");
            Assert.IsFalse(Directory.Exists(headerOnly), "A cut-off file holding no whole frame is no video");
            Assert.IsFalse(Directory.Exists(testVideoNothing));
            Assert.IsFalse(Directory.Exists(temporaryFileOnly));
            Assert.AreEqual(4, found.EmptyRunsRemoved);
            Assert.AreEqual(0, found.RecordingsWaitingToUpload + found.TestVideosKept);
        }

        [Test]
        public void AnUnfinishedCopyBesideAFinishedVideoIsDeletedAndTheFinishedOneKept()
        {
            // A finished name is only ever given to a whole file: the copy beside it is what a rename left, never newer.
            string run = Plant(_root, Playtest, "20260101-000000-00000001", 10, "pk-1", FinishedVideo(_scratch, 5), CutOffVideo(_scratch, 3));

            ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsFalse(File.Exists(VideoPath(run, Playtest) + ".part"));
            Assert.IsTrue(ProtokitePlaytestWebmTestFiles.Read(VideoPath(run, Playtest), out WebmFileRead read));
            Assert.AreEqual(5, read.Frames.Count, "The whole file is kept, not replaced by the shorter copy");
            Assert.AreEqual(0, found.CutOffVideosFinished);
            Assert.AreEqual(1, found.RecordingsWaitingToUpload);
        }

        [Test]
        public void TemporaryFilesAnEndedLaunchLeftAreDeleted()
        {
            string run = Plant(_root, Playtest, "20260101-000000-00000001", 10, "pk-1", FinishedVideo(_scratch, 2));
            File.WriteAllText(Path.Combine(run, "session.json.0123456789abcdef.tmp"), "{");
            File.WriteAllText(Path.Combine(run, "reserved-bytes.txt.fedcba9876543210.tmp"), "1");

            ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsEmpty(Directory.GetFiles(run, "*.tmp"), "Its launch has ended, so no save of its is in progress");
            Assert.IsTrue(File.Exists(VideoPath(run, Playtest)));
            Assert.IsTrue(File.Exists(Path.Combine(run, "session.json")));
        }

        [Test]
        public void ARunItsLaunchStillHoldsIsLeftAlone()
        {
            string cutOff = Plant(_root, Playtest, "20260101-000000-00000001", 10, "pk-1", cutOffVideo: CutOffVideo(_scratch, 3));
            string noSession = Plant(_root, Playtest, "20260101-000000-00000002", finishedVideo: FinishedVideo(_scratch, 3));
            string empty = Plant(_root, TestVideo, "20260101-000000-00000003");
            FileStream cutOffLock = HoldLock(cutOff);
            FileStream noSessionLock = HoldLock(noSession);
            FileStream emptyLock = HoldLock(empty);

            ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsTrue(File.Exists(VideoPath(cutOff, Playtest) + ".part"), "A video still being written is not finished by another launch");
            Assert.IsTrue(File.Exists(VideoPath(noSession, Playtest)), "Its session may start any moment");
            Assert.IsTrue(Directory.Exists(empty), "Its recording may not have begun");
            Assert.AreEqual(0, found.CutOffVideosFinished + found.RecordingsDeletedWithNoSession + found.EmptyRunsRemoved);

            // Control: once their launches let go, the same pass does its work.
            cutOffLock.Dispose();
            noSessionLock.Dispose();
            emptyLock.Dispose();
            found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.AreEqual(1, found.CutOffVideosFinished);
            Assert.AreEqual(1, found.RecordingsDeletedWithNoSession);
            Assert.AreEqual(1, found.EmptyRunsRemoved);
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)] // A file another program holds open cannot be deleted on Windows alone.
        public void WhatCannotBeFinishedOrDeletedIsLeftForTheNextLaunch()
        {
            string noSession = Plant(_root, Playtest, "20260101-000000-00000001", finishedVideo: FinishedVideo(_scratch, 2));
            string cutOff = Plant(_root, Playtest, "20260101-000000-00000002", 10, "pk-1", cutOffVideo: CutOffVideo(_scratch, 2));
            using (new FileStream(VideoPath(noSession, Playtest), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (new FileStream(VideoPath(cutOff, Playtest) + ".part", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ProtokitePlaytestEarlierRecordings found = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
                Assert.AreEqual(2, found.LeftForTheNextLaunch.Count, string.Join("\n", found.LeftForTheNextLaunch));
                Assert.IsTrue(File.Exists(Path.Combine(noSession, "in-use.lock")), "Its lock stays, so the next launch finds it again");
                Assert.IsTrue(File.Exists(VideoPath(cutOff, Playtest) + ".part"));
                Assert.AreEqual(0, found.RecordingsWaitingToUpload, "A run left as it was is not reported kept");
            }

            ProtokitePlaytestEarlierRecordings next = ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            Assert.IsFalse(Directory.Exists(noSession), "The next launch does it");
            Assert.AreEqual(1, next.CutOffVideosFinished);
            Assert.IsEmpty(next.LeftForTheNextLaunch);
        }

        [Test]
        public void NothingOutsideTheRunsIsTouchedOrCounted()
        {
            // What an earlier version left straight in the recordings folder, a folder with no lock, and a folder of another name.
            string loose = Path.Combine(_root, "recording-20260926-101010-abcdef01.webm");
            File.WriteAllBytes(loose, new byte[1000]);
            File.WriteAllBytes(loose + ".part", new byte[1000]);
            string notARun = Path.Combine(_root, "Playtest", "20260101-000000-00000001");
            Directory.CreateDirectory(notARun);
            File.WriteAllBytes(Path.Combine(notARun, "recording-20260101-000000-00000001.webm"), new byte[1000]);
            string elsewhere = Path.Combine(_root, "Other", "20260101-000000-00000002");
            Directory.CreateDirectory(elsewhere);
            File.WriteAllBytes(Path.Combine(elsewhere, "in-use.lock"), new byte[0]);
            File.WriteAllBytes(Path.Combine(elsewhere, "recording-20260101-000000-00000002.webm"), new byte[1000]);

            ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root);
            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 1, 1, 0);
            Assert.IsTrue(File.Exists(loose) && File.Exists(loose + ".part"), "An earlier version's recordings are left where they are");
            Assert.IsTrue(File.Exists(Path.Combine(notARun, "recording-20260101-000000-00000001.webm")));
            Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "recording-20260101-000000-00000002.webm")));
            Assert.AreEqual(0, room.BytesUsedByOtherRuns, "And none of it is counted");
        }

        // The disk budget

        [Test]
        public void MakingRoomDeletesTestVideosFirstThenTheOldestRecordingWaitingToUpload()
        {
            string oldUpload = Plant(_root, Playtest, "20260101-000000-00000001", sessionId: "pk-1", finishedVideo: new byte[300000]);
            string oldTest = Plant(_root, TestVideo, "20260102-000000-00000002", finishedVideo: new byte[200000]);
            string newTest = Plant(_root, TestVideo, "20260103-000000-00000003", finishedVideo: new byte[200000]);
            string newUpload = Plant(_root, Playtest, "20260104-000000-00000004", sessionId: "pk-4", finishedVideo: new byte[300000]);
            long total = Total(oldUpload, oldTest, newTest, newUpload);

            // 300 KB wanted, 50 KB short.
            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, total + 250000, 300000, 0);
            Assert.IsFalse(Directory.Exists(oldTest), "The oldest test video goes first");
            Assert.IsTrue(Directory.Exists(newTest), "One was enough");
            Assert.IsTrue(Directory.Exists(oldUpload), "A recording waiting to upload goes after every test video, however old");
            Assert.AreEqual(1, room.TestVideosDeleted);
            Assert.AreEqual(0, room.WaitingRecordingsDeleted);
            long left = Total(oldUpload, newTest, newUpload);
            Assert.AreEqual(left, room.BytesUsedByOtherRuns);
            Assert.AreEqual(total + 250000 - left, room.BytesLeft);

            // 450 KB short: the other test video is not enough, so the oldest upload goes too, and the newer one stays.
            room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, left + 300000 - 450000, 300000, 0);
            Assert.IsFalse(Directory.Exists(newTest));
            Assert.IsFalse(Directory.Exists(oldUpload), "The oldest upload next");
            Assert.IsTrue(Directory.Exists(newUpload));
            Assert.AreEqual(1, room.TestVideosDeleted);
            Assert.AreEqual(1, room.WaitingRecordingsDeleted);
        }

        [Test]
        public void MakingRoomCountsARunStillInUseAtItsReservationAndNeverDeletesIt()
        {
            // Its launch has just made it and still runs, with nothing written yet: only the lock stands between it and deletion.
            string running = Plant(_root, TestVideo, "20260101-000000-00000001", 400000);
            Held(HoldLock(running));

            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 1000000, 100000, 0);
            Assert.AreEqual(600000, room.BytesLeft, "At the 400 KB it reserved, though it holds nothing yet");

            room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 450000, 100000, 0);
            Assert.IsTrue(Directory.Exists(running), "Never deleted, though it is the oldest and a test video");
            Assert.AreEqual(0, room.TestVideosDeleted);
            Assert.AreEqual(50000, room.BytesLeft);

            // One that has saved no reservation yet counts at what the counting launch's own recording may grow to.
            File.Delete(Path.Combine(running, "reserved-bytes.txt"));
            room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 1000000, 100000, 700000);
            Assert.AreEqual(300000, room.BytesLeft);
        }

        [Test]
        public void ARunHeldWithItsVideoFinishedCountsAtWhatItTakesAndIsNotDeleted()
        {
            // Being uploaded, or its launch still running after its recording finished: it grows no more.
            string uploading = Plant(_root, Playtest, "20260101-000000-00000001", 800000, "pk-1", finishedVideo: new byte[200000]);
            Held(HoldLock(uploading));
            // A stray file is not a finished video: a run holding only one still counts at its reservation.
            string starting = Plant(_root, Playtest, "20260101-000000-00000002", 300000);
            File.WriteAllBytes(Path.Combine(starting, "desktop.ini"), new byte[10]);
            Held(HoldLock(starting));

            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 2000000, 100000, 0);
            Assert.AreEqual(2000000 - Total(uploading) - 300000, room.BytesLeft, "The finished one at the 200 KB it takes, not the 800 KB it once reserved");

            room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 400000, 100000, 0);
            Assert.IsTrue(File.Exists(VideoPath(uploading, Playtest)), "Held, so never deleted");
            Assert.AreEqual(0, room.WaitingRecordingsDeleted);
        }

        [Test]
        public void ARunTheFinishingPassHoldsIsNotDeleted()
        {
            string held = Plant(_root, TestVideo, "20260101-000000-00000001", 500000, finishedVideo: new byte[100000]);
            ProtokitePlaytestRoomMade room = null;
            ProtokitePlaytestRecordingsFolder.FinishEndedRuns(_root, folder => room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 150000, 200000, 0));

            Assert.IsNotNull(room, "Precondition: room was made while the pass held the run");
            Assert.AreEqual(150000 - Total(held), room.BytesLeft, "Its video is finished, so it counts at what it takes");
            Assert.AreEqual(0, room.TestVideosDeleted, "Room was short, yet the run the pass holds stays");
            Assert.IsTrue(File.Exists(VideoPath(held, TestVideo)), "Not deleted while the pass works on it");
        }

        [Test]
        public void MakingRoomNeverDeletesARunWithAnUnfinishedVideo()
        {
            // Its launch has ended, but finishing it is the finishing pass's work: making room neither claims nor deletes it.
            string cutOff = Plant(_root, TestVideo, "20260101-000000-00000001", 500000, cutOffVideo: new byte[300000]);

            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 600000, 200000, 0);
            Assert.AreEqual(100000, room.BytesLeft, "Counted at the room it reserved, not the 300 KB it holds");

            room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, null, 450000, 200000, 0);
            Assert.IsTrue(File.Exists(VideoPath(cutOff, TestVideo) + ".part"), "Not deleted, though a test video and room is wanted");
            Assert.AreEqual(0, room.TestVideosDeleted);
            Assert.AreEqual(0, room.BytesLeft);
        }

        [Test]
        public void TheNewRunIsNotCountedAgainstItself()
        {
            ProtokitePlaytestRecordingRun run = Held(ProtokitePlaytestRecordingRun.Start(_root, Playtest, 600000, out string error));
            Assert.IsNotNull(run, error);

            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, run, 1000000, 600000, 0);
            Assert.AreEqual(1000000, room.BytesLeft, "The room it reserved is the room it is making, not room another run takes");
            Assert.IsTrue(Directory.Exists(run.FolderPath));
        }

        [Test]
        public void AFiveSecondTestVideoNeverDeletesARecordingWaitingToUpload()
        {
            string upload = Plant(_root, Playtest, "20260101-000000-00000001", sessionId: "pk-1", finishedVideo: new byte[7 * 1024 * 1024]);
            ProtokitePlaytestVideoSettings settings = new ProtokitePlaytestVideoSettings { MaxSeconds = 5.0, DiskBudgetBytes = 9L * 1024 * 1024 };
            long wanted = settings.BytesToMakeRoomFor(ProtokitePlaytestWebmFile.FrameHeaderBytes);
            ProtokitePlaytestRecordingRun run = Held(ProtokitePlaytestRecordingRun.Start(_root, TestVideo, wanted, out string error));
            Assert.IsNotNull(run, error);

            ProtokitePlaytestRoomMade room = ProtokitePlaytestRecordingsFolder.MakeRoom(_root, run, settings.DiskBudgetBytes, wanted, settings.MaxBytes);
            Assert.IsTrue(File.Exists(VideoPath(upload, Playtest)), "Five seconds at its bitrate fit beside it; its 1.5 GB size limit is no reason to delete it");
            Assert.AreEqual(0, room.WaitingRecordingsDeleted);
            Assert.GreaterOrEqual(room.BytesLeft, wanted);
        }
    }
}
