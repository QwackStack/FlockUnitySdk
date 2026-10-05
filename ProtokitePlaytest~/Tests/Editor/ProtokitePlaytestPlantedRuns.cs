using System.Collections.Generic;
using System.IO;

namespace Protokite.Playtest.Tests
{
    /// <summary>
    /// Run folders a test lays out by hand, the way an earlier launch leaves them, with names the test chooses. The file names are
    /// written out here rather than taken from the code under test, so a change to the layout on disk fails a test.
    /// </summary>
    internal static class ProtokitePlaytestPlantedRuns
    {
        /// <summary>Plants a run whose launch has ended: its lock file is there and nobody holds it. Videos are MP4 unless another ending is given.</summary>
        public static string Plant(string recordingsFolder, ProtokitePlaytestRecordingKind kind, string name, long? reservedBytes = null,
            string sessionId = null, byte[] finishedVideo = null, byte[] cutOffVideo = null, string videoEnding = ".mp4")
        {
            string folder = Path.Combine(recordingsFolder, kind == ProtokitePlaytestRecordingKind.TestVideo ? "TestVideos" : "Playtest", name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "in-use.lock"), new byte[0]);
            if (reservedBytes.HasValue)
                File.WriteAllText(Path.Combine(folder, "reserved-bytes.txt"), reservedBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (sessionId != null)
                File.WriteAllText(Path.Combine(folder, "session.json"),
                    "{\"protokite_session_id\":\"" + sessionId + "\",\"protokite_api_url\":\"http://protokite.test\",\"flock_game_version_id\":\"test-gvid\"}");
            if (finishedVideo != null)
                File.WriteAllBytes(VideoPath(folder, kind, videoEnding), finishedVideo);
            if (cutOffVideo != null)
                File.WriteAllBytes(VideoPath(folder, kind, videoEnding) + ".part", cutOffVideo);
            return Path.GetFullPath(folder);
        }

        /// <summary>Where a run's finished video is kept.</summary>
        public static string VideoPath(string runFolder, ProtokitePlaytestRecordingKind kind, string videoEnding = ".mp4")
            => Path.Combine(runFolder, (kind == ProtokitePlaytestRecordingKind.TestVideo ? "test-recording-" : "recording-") + Path.GetFileName(runFolder) + videoEnding);

        /// <summary>Holds a run's lock the way its running launch does, until disposed.</summary>
        public static FileStream HoldLock(string runFolder)
            => new FileStream(Path.Combine(runFolder, "in-use.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        /// <summary>An MP4 file of whole frames, closed as a finished recording is.</summary>
        public static byte[] FinishedVideo(string scratchFolder, int frames)
            => ProtokitePlaytestMp4TestFiles.MakeVideoBytes(scratchFolder, frames, 40, closed: true);

        /// <summary>An MP4 file a crash cut off: whole frames, then the next frame's fragment header whose bytes never reached the disk.</summary>
        public static byte[] CutOffVideo(string scratchFolder, int frames)
        {
            List<byte> bytes = new List<byte>(ProtokitePlaytestMp4TestFiles.MakeVideoBytes(scratchFolder, frames, 40, closed: false));
            ProtokitePlaytestMp4TestFiles.AppendFragmentHeader(bytes, (uint)frames + 1, frames * ProtokitePlaytestMp4TestFiles.FrameMs, 40, false);
            return bytes.ToArray();
        }

        /// <summary>A WebM file an earlier version of the package was writing when a crash cut it off.</summary>
        public static byte[] CutOffWebm(string scratchFolder, int frames)
        {
            List<byte> bytes = new List<byte>(ProtokitePlaytestWebmTestFiles.MakeVideoBytes(scratchFolder, ProtokitePlaytestWebmFinisher.Codec.Vp8, frames, 40, closed: false));
            ProtokitePlaytestWebmTestFiles.AppendFrameHeader(bytes, frames, 40);
            return bytes.ToArray();
        }

        /// <summary>A WebM file an earlier version of the package finished.</summary>
        public static byte[] FinishedWebm(string scratchFolder, int frames)
            => ProtokitePlaytestWebmTestFiles.MakeVideoBytes(scratchFolder, ProtokitePlaytestWebmFinisher.Codec.Vp8, frames, 40, closed: true);

        /// <summary>The run folders directly under a kind's folder.</summary>
        public static string[] Runs(string recordingsFolder, ProtokitePlaytestRecordingKind kind)
        {
            string folder = Path.Combine(recordingsFolder, kind == ProtokitePlaytestRecordingKind.TestVideo ? "TestVideos" : "Playtest");
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : new string[0];
        }
    }
}
