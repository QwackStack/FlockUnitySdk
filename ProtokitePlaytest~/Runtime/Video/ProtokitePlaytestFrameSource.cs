using System;
using System.Collections.Generic;

namespace Protokite.Playtest
{
    /// <summary>One captured frame, in the encoder's pixel layout, in a block the recording hands back to the pool once encoded.</summary>
    internal sealed class ProtokitePlaytestCapturedFrame
    {
        public ProtokitePlaytestCapturedFrame(byte[] pixels, long timestampMs)
        {
            Pixels = pixels;
            TimestampMs = timestampMs;
        }

        public byte[] Pixels { get; }
        public long TimestampMs { get; }
    }

    /// <summary>
    /// Where a recording's frames come from. The recording asks for a frame at each capture time on the main thread; the frame
    /// arrives some frames later, when the graphics card has handed it back, and is taken on the main thread too.
    /// </summary>
    internal interface IProtokitePlaytestFrameSource : IDisposable
    {
        /// <summary>The size every frame comes at.</summary>
        int Width { get; }
        int Height { get; }

        /// <summary>False while as many frames are on their way as the source can hold; a frame asked for then would be dropped.</summary>
        bool IsReadyForAnotherFrame { get; }

        /// <summary>Asks for the frame the game has just drawn, to be shown at <paramref name="timestampMs"/>. Call at the end of a frame.</summary>
        void CaptureFrame(long timestampMs);

        /// <summary>Moves the frames that have arrived since the last call into <paramref name="frames"/>, oldest first.</summary>
        void TakeCapturedFrames(List<ProtokitePlaytestCapturedFrame> frames);

        /// <summary>Takes a frame's block back once the frame is encoded, so the next frame can use it. Any thread.</summary>
        void ReturnBlock(byte[] pixels);

        /// <summary>Waits for the frames still on their way and moves them into <paramref name="frames"/>; nothing is captured afterwards.</summary>
        void Stop(List<ProtokitePlaytestCapturedFrame> frames);

        /// <summary>Frames the graphics card could not hand back.</summary>
        int FramesLostOnTheGraphicsCard { get; }

        /// <summary>Frames that arrived when every block was still waiting to be encoded, so were dropped.</summary>
        int FramesDroppedForWantOfABlock { get; }
    }

    /// <summary>A fixed number of frame blocks, shared by the main thread that fills them and the encoding thread that empties them.</summary>
    internal sealed class ProtokitePlaytestFrameBlocks
    {
        private readonly object _lock = new object();
        private readonly Stack<byte[]> _free = new Stack<byte[]>();
        private readonly HashSet<byte[]> _ours = new HashSet<byte[]>();
        private readonly HashSet<byte[]> _freeSet = new HashSet<byte[]>();

        public ProtokitePlaytestFrameBlocks(int count, int blockBytes)
        {
            for (int i = 0; i < count; i++)
            {
                byte[] block = new byte[blockBytes];
                _ours.Add(block);
                _free.Push(block);
                _freeSet.Add(block);
            }
        }

        /// <summary>A free block, or null when every block is in use.</summary>
        public byte[] Take()
        {
            lock (_lock)
            {
                if (_free.Count == 0)
                    return null;
                byte[] block = _free.Pop();
                _freeSet.Remove(block);
                return block;
            }
        }

        /// <summary>Gives a block back. One that is not this pool's, or is already free, is ignored: two frames never share a block.</summary>
        public void Return(byte[] block)
        {
            lock (_lock)
            {
                if (block == null || !_ours.Contains(block) || !_freeSet.Add(block))
                    return;
                _free.Push(block);
            }
        }

        public int FreeCount
        {
            get
            {
                lock (_lock)
                    return _free.Count;
            }
        }
    }
}
