using System;

namespace Protokite.Playtest
{
    /// <summary>The kind of file recordings are written to, and the kinds a cut-off recording can be finished from; nothing outside this file names them.</summary>
    internal static class ProtokitePlaytestRecordingFiles
    {
        /// <summary>A new file, not yet open: fragmented MP4, which a browser plays and a crash cannot spoil.</summary>
        public static IProtokitePlaytestRecordingFile Create() => new ProtokitePlaytestMp4File();

        /// <summary>The content type this build's recordings are written and uploaded as.</summary>
        public static string ContentTypeWritten
        {
            get
            {
                using (IProtokitePlaytestRecordingFile file = Create())
                    return file.ContentType;
            }
        }

        /// <summary>The content type a finished recording is uploaded as, read from its file's ending; null for a kind Protokite does not take.</summary>
        // By ending rather than by what this build writes, so a WebM recording an earlier version kept still goes.
        public static string ContentTypeFor(string videoPath)
        {
            if (videoPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                return "video/webm";
            if (videoPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return "video/mp4";
            return null;
        }

        /// <summary>What can finish a cut-off recording to be kept at finishedPath, by its ending; null for a kind of recording this package never wrote.</summary>
        public static IProtokitePlaytestRecordingFinisher ForFinishing(string finishedPath)
        {
            if (finishedPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return new ProtokitePlaytestMp4File();
            if (finishedPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                return new ProtokitePlaytestWebmFinisher();
            return null;
        }
    }
}
