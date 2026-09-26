using System;

namespace Protokite.Playtest
{
    /// <summary>What finishing a recording that stopped being written part of the way through did.</summary>
    internal enum ProtokitePlaytestInterruptedRecordingResult
    {
        /// <summary>Its whole frames are kept under the finished name.</summary>
        Finished,
        /// <summary>It held no whole frame, or no header this kind of file starts with, so it was deleted.</summary>
        HeldNoFrame,
        /// <summary>It could not be opened, changed, renamed or deleted, or its codec is one this build cannot check, and is left as it was.</summary>
        CouldNotFinish,
    }

    /// <summary>
    /// The file a recording's encoded frames are written into as they arrive, in a format a browser plays, and still playable
    /// up to the cut when the game stops mid-recording. The upload sends it with its own content type.
    /// </summary>
    internal interface IProtokitePlaytestRecordingFile : IDisposable
    {
        /// <summary>The content type the file is uploaded as.</summary>
        string ContentType { get; }

        /// <summary>The ending a finished recording's file name takes, dot included.</summary>
        string FileExtension { get; }

        /// <summary>What writing a frame adds to the file on top of the frame's own bytes, so a size limit can be kept exactly.</summary>
        int BytesAddedToEachFrame { get; }

        /// <summary>Bytes written so far, header included.</summary>
        long BytesWritten { get; }

        /// <summary>Frames written so far.</summary>
        int FramesWritten { get; }

        /// <summary>The last frame's time in milliseconds, or -1 before the first.</summary>
        long LastTimestampMs { get; }

        /// <summary>Creates the file at path, replacing one already there, and writes its header.</summary>
        bool Open(string path, ProtokitePlaytestVideoCodec codec, int width, int height, out string error);

        /// <summary>Appends one encoded frame; its time must be later than the last frame's.</summary>
        bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error);

        /// <summary>Writes what only the end knows (the length) and closes the file.</summary>
        bool Close(out string error);

        /// <summary>
        /// Finishes a file whose writing stopped part of the way through: whole frames are kept, a partly written one is cut
        /// off, and the file is renamed to finishedPath. One holding no whole frame is deleted.
        /// </summary>
        ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error);
    }
}
