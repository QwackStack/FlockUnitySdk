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

    /// <summary>Finishes a recording of one kind of file that stopped being written part of the way through, as a crash leaves it.</summary>
    internal interface IProtokitePlaytestRecordingFinisher
    {
        /// <summary>Finishes a file whose writing stopped part-way: whole frames kept, a torn one cut off, renamed to finishedPath; one with no whole frame is deleted.</summary>
        ProtokitePlaytestInterruptedRecordingResult FinishInterruptedRecording(string unfinishedPath, string finishedPath, out int framesKept, out string error);
    }

    /// <summary>The file a recording's frames are written into as they arrive, playable in a browser and up to the cut when the game stops mid-recording.</summary>
    internal interface IProtokitePlaytestRecordingFile : IProtokitePlaytestRecordingFinisher, IDisposable
    {
        /// <summary>The content type the file is uploaded as.</summary>
        string ContentType { get; }

        /// <summary>The ending a finished recording's file name takes, dot included.</summary>
        string FileExtension { get; }

        /// <summary>What writing a frame adds to the file on top of the frame's own bytes, for estimating the room a recording needs.</summary>
        int BytesAddedToEachFrame { get; }

        /// <summary>Exactly what writing this frame adds to the file, any header it carries included, so a size limit can be kept exactly. Any thread.</summary>
        long BytesFor(ProtokitePlaytestEncodedFrame frame);

        /// <summary>Bytes written so far, header included.</summary>
        long BytesWritten { get; }

        /// <summary>Frames written so far.</summary>
        int FramesWritten { get; }

        /// <summary>The last frame's time in milliseconds, or -1 before the first.</summary>
        long LastTimestampMs { get; }

        /// <summary>Creates the file at path, replacing one already there, for frames of this size each shown for frameDurationMs.</summary>
        bool Open(string path, int width, int height, long frameDurationMs, out string error);

        /// <summary>Appends one encoded frame; its time must be later than the last frame's.</summary>
        bool WriteFrame(ProtokitePlaytestEncodedFrame frame, out string error);

        /// <summary>Writes what only the end knows (the length) and closes the file.</summary>
        bool Close(out string error);
    }
}
