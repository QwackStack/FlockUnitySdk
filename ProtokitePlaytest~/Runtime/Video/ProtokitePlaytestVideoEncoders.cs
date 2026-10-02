using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>An H.264 encoder Windows offers on this PC.</summary>
    internal sealed class ProtokitePlaytestEncoderFound
    {
        public string Name;

        /// <summary>Whether it runs on a graphics card's video engine rather than on the processor.</summary>
        public bool OnGraphicsCard;

        /// <summary>The graphics card maker's PCI vendor ID, 0 when Windows does not say.</summary>
        public int VendorId;

        public override string ToString() => Name + (OnGraphicsCard ? " (on the graphics card)" : " (Windows' software encoder, on the processor)");
    }

    /// <summary>The encoder recordings use on this platform, and whether this PC has one; nothing outside this file and the encoder's own names it.</summary>
    internal static class ProtokitePlaytestVideoEncoders
    {
        internal const string OnlyOn64BitWindows = "video is recorded on 64-bit Windows only.";
        internal const string MediaFoundationMissing = "Windows' Media Foundation, which encodes the video, is missing on this PC (a Windows N edition without its Media Feature Pack).";
        internal const string NoGraphicsCardEncoder = "this PC's graphics card has no H.264 encoder that Windows offers. Turn on Allow Software Encoder in Protokite > Playtest > " +
                                                      "Settings to record with Windows' own encoder on the processor instead, which costs the game frame rate.";
        internal const string NoEncoderAtAll = "Windows offers no H.264 encoder on this PC.";

        internal const string NoAnswerInTime = "Windows did not say within 10 seconds which video encoders this PC has.";

        private const string LogPrefix = "[Protokite Playtest] ";
        private static readonly TimeSpan LongestWaitForAnAnswer = TimeSpan.FromSeconds(10);
        private static readonly object CheckLock = new object();
        private static List<ProtokitePlaytestEncoderFound> _found;
        private static string _whyNoneFound;
        private static bool _checked;
        private static Thread _looking;
        private static DateTime _stopWaitingForTheAnswerAt;
        // Counts launches, so a check an earlier launch started never answers this one.
        private static int _launch;
        private static bool _reportedNoVideo;

        /// <summary>Stands in for the process's architecture, so a test can act out a 32-bit or ARM64 Windows build.</summary>
        internal static Func<Architecture> ReadProcessArchitectureForTesting;

        /// <summary>Stands in for what Windows offers, on the asking thread, so a test can act out a PC with no encoder, or no Media Foundation (the list null, with why).</summary>
        internal static Func<KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>> LookForEncodersForTesting;

        /// <summary>How long Windows is given to answer, when a test sets it; 10 seconds otherwise.</summary>
        internal static TimeSpan? LongestWaitForAnAnswerForTesting;

        /// <summary>Whether this launch has asked Windows, or is asking; for tests.</summary>
        internal static bool AskedThisLaunchForTesting
        {
            get
            {
                lock (CheckLock)
                    return _checked || _looking != null;
            }
        }

        /// <summary>Whether this build and this process should have video: built for Windows, and not running as 32-bit or ARM.</summary>
        internal static bool ShouldHaveVideo
        {
            get
            {
                if (!IsBuiltWithVideo)
                    return false;
                Architecture architecture;
                try
                {
                    architecture = ReadProcessArchitectureForTesting != null ? ReadProcessArchitectureForTesting()
                        : IntPtr.Size == 8 ? RuntimeInformation.ProcessArchitecture : Architecture.X86;
                }
                catch (Exception)
                {
                    return true;
                }
                // Only a known other architecture is turned away, so an unexpected answer still tries.
                return architecture != Architecture.X86 && architecture != Architecture.Arm && architecture != Architecture.Arm64;
            }
        }

        /// <summary>The H.264 encoders this PC offers, graphics card ones first; null, with why, when it can record none. Asked of Windows once a launch, waited for 10 seconds from the asking at most.</summary>
        internal static List<ProtokitePlaytestEncoderFound> EncodersOnThisPc(out string whyNone)
        {
            Thread looking = StartLookingForEncoders();
            if (looking != null)
                looking.Join(TimeLeftToAnswer());
            lock (CheckLock)
            {
                whyNone = _checked ? _whyNoneFound : NoAnswerInTime;
                return _checked ? _found : null;
            }
        }

        /// <summary>Whether asking for an encoder now takes no wait: Windows has answered, or its 10 seconds to answer are up. Starts the asking when nothing has.</summary>
        internal static bool FinishedLookingForEncoders() => StartLookingForEncoders() == null || TimeLeftToAnswer() == TimeSpan.Zero;

        private static TimeSpan TimeLeftToAnswer()
        {
            lock (CheckLock)
            {
                TimeSpan left = _stopWaitingForTheAnswerAt - DateTime.UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        /// <summary>Starts asking Windows which encoders this PC has, on a thread of its own, unless that is done or under way; the thread asking, or null once answered.</summary>
        // A thread of its own, so Unity's main thread never has COM started for it, and the answer (tens of milliseconds) is not waited for there.
        internal static Thread StartLookingForEncoders()
        {
            lock (CheckLock)
            {
                // A build that records no video has nothing to ask Windows.
                if (_checked || !ShouldHaveVideo)
                    return null;
                if (_looking != null)
                    return _looking;
                int launch = _launch;
                Func<KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>> lookForTesting = LookForEncodersForTesting;
                _stopWaitingForTheAnswerAt = DateTime.UtcNow + (LongestWaitForAnAnswerForTesting ?? LongestWaitForAnAnswer);
                _looking = new Thread(() =>
                {
                    List<ProtokitePlaytestEncoderFound> found;
                    string why;
                    // Nothing may escape a thread of its own, and the launch is always answered.
                    try
                    {
                        if (lookForTesting != null)
                        {
                            KeyValuePair<List<ProtokitePlaytestEncoderFound>, string> answer = lookForTesting();
                            found = answer.Key;
                            why = answer.Value;
                        }
                        else
                        {
                            found = LookForEncodersOnThisThread(out why);
                        }
                    }
                    catch (Exception ex)
                    {
                        found = null;
                        why = "Windows could not say which video encoders this PC has: " + ex.Message;
                    }
                    lock (CheckLock)
                    {
                        // A new launch began while Windows was asked; its own check answers it.
                        if (launch != _launch)
                            return;
                        Remember(found, why);
                        _looking = null;
                    }
                }) { IsBackground = true, Name = "Protokite Playtest encoder check" };
                _looking.Start();
                return _looking;
            }
        }

        private static void Remember(List<ProtokitePlaytestEncoderFound> found, string whyNone)
        {
            _found = found;
            _whyNoneFound = found == null ? whyNone : null;
            _checked = true;
        }

        /// <summary>An encoder for a recording, or null with why when this PC can record none (no graphics card encoder, the software one not allowed); the first "no video" of a launch is logged.</summary>
        public static IProtokitePlaytestVideoEncoder Create(bool allowSoftwareEncoder, out string whyNot)
        {
            whyNot = WhyThisPcRecordsNoVideo(allowSoftwareEncoder);
            if (whyNot == null)
                return NewEncoder();
            lock (CheckLock)
            {
                if (_reportedNoVideo)
                    return null;
                _reportedNoVideo = true;
            }
            // The reason may be the PC's rather than the build's, so the line names neither.
            string line = LogPrefix + "This launch records no playtest video: " + whyNot + " Everything else in the playtest still runs.";
            // Expected where video is not built at all; a 64-bit Windows build that should have it says so louder.
            if (ShouldHaveVideo)
                Debug.LogWarning(line);
            else
                Debug.Log(line);
            return null;
        }

        /// <summary>Why this PC records no video with these settings, or null when it can.</summary>
        internal static string WhyThisPcRecordsNoVideo(bool allowSoftwareEncoder)
        {
            if (!ShouldHaveVideo)
                return OnlyOn64BitWindows;
            List<ProtokitePlaytestEncoderFound> found = EncodersOnThisPc(out string whyNone);
            if (found == null)
                return whyNone;
            if (found.Exists(encoder => encoder.OnGraphicsCard))
                return null;
            if (!allowSoftwareEncoder)
                return NoGraphicsCardEncoder;
            return found.Count > 0 ? null : NoEncoderAtAll;
        }

        /// <summary>Forgets what was checked and reported, as a new launch does; statics outlive Play Mode with domain reload off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForNewLaunch()
        {
            lock (CheckLock)
            {
                _launch++;
                _checked = false;
                _found = null;
                _whyNoneFound = null;
                _looking = null;
                _stopWaitingForTheAnswerAt = DateTime.MinValue;
                _reportedNoVideo = false;
            }
        }

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        internal static readonly bool IsBuiltWithVideo = true;

        private static IProtokitePlaytestVideoEncoder NewEncoder() => new ProtokitePlaytestWindowsVideoEncoder();

        private static List<ProtokitePlaytestEncoderFound> LookForEncodersOnThisThread(out string whyNone)
        {
            bool comStarted = false;
            try
            {
                comStarted = ProtokitePlaytestWindowsVideoEncoder.StartCom();
                int result = ProtokitePlaytestMediaFoundation.MFStartup(ProtokitePlaytestMediaFoundation.MediaFoundationVersion, ProtokitePlaytestMediaFoundation.StartupFull);
                if (result < 0)
                {
                    whyNone = $"Windows' Media Foundation, which encodes the video, would not start ({ProtokitePlaytestMediaFoundation.Hex(result)}).";
                    return null;
                }
                try
                {
                    List<ProtokitePlaytestEncoderFound> found = new List<ProtokitePlaytestEncoderFound>();
                    foreach (KeyValuePair<ProtokitePlaytestMediaFoundation.IMFActivate, ProtokitePlaytestEncoderFound> encoder in ProtokitePlaytestWindowsVideoEncoder.Offered(true, 0))
                    {
                        found.Add(encoder.Value);
                        ProtokitePlaytestMediaFoundation.Release(encoder.Key);
                    }
                    whyNone = null;
                    return found;
                }
                finally
                {
                    ProtokitePlaytestMediaFoundation.MFShutdown();
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                whyNone = MediaFoundationMissing;
                return null;
            }
            catch (Exception ex)
            {
                whyNone = "Windows could not say which video encoders this PC has: " + ex.Message;
                return null;
            }
            finally
            {
                if (comStarted)
                    ProtokitePlaytestMediaFoundation.CoUninitialize();
            }
        }
#else
        internal static readonly bool IsBuiltWithVideo = false;

        private static IProtokitePlaytestVideoEncoder NewEncoder() => null;

        private static List<ProtokitePlaytestEncoderFound> LookForEncodersOnThisThread(out string whyNone)
        {
            whyNone = OnlyOn64BitWindows;
            return null;
        }
#endif
    }
}
