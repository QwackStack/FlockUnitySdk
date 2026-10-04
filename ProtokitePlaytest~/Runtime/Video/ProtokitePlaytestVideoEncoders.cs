using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>An H.264 encoder the platform offers on this machine: Windows on a PC, Android on a phone.</summary>
    internal sealed class ProtokitePlaytestEncoderFound
    {
        public string Name;

        /// <summary>Whether it runs on video hardware (a graphics card's, or a phone's) rather than on the processor.</summary>
        public bool InHardware;

        /// <summary>The graphics card maker's PCI vendor ID, 0 when Windows does not say or on a phone.</summary>
        public int VendorId;

        /// <summary>Whether it is a phone's encoder, which decides how it is named.</summary>
        public bool OnAPhone;

        /// <summary>Whether it takes NV12 frames, the layout the capture makes.</summary>
        public bool TakesNv12 = true;

        /// <summary>Whether it can hold a constant bitrate, which keeps a busy picture's file at the bitrate asked for.</summary>
        public bool TakesConstantBitrate;

        /// <summary>Whether it takes frames of this width and height at this rate; null when the platform does not say.</summary>
        public Func<int, int, int, bool> TakesSizeAndRate;

        /// <summary>What the platform lent to answer <see cref="TakesSizeAndRate"/>, released when the launch's check is forgotten.</summary>
        public IDisposable HeldFromThePlatform;

        public override string ToString()
            => Name + (OnAPhone
                ? InHardware ? " (the phone's hardware encoder)" : " (Android's software encoder, on the processor)"
                : InHardware ? " (on the graphics card)" : " (Windows' software encoder, on the processor)");
    }

    /// <summary>The encoder recordings use on this platform, and whether this machine has one; nothing outside this file and the encoders' own names them.</summary>
    internal static class ProtokitePlaytestVideoEncoders
    {
        internal const string NoVideoOnThisPlatform = "video is recorded on 64-bit Windows and Android only.";
        internal const string MediaFoundationMissing = "Windows' Media Foundation, which encodes the video, is missing on this PC (a Windows N edition without its Media Feature Pack).";
        internal const string NoGraphicsCardEncoder = "this PC's graphics card has no H.264 encoder that Windows offers. Turn on Allow Software Encoder in Protokite > Playtest > " +
                                                      "Settings to record with Windows' own encoder on the processor instead, which costs the game frame rate.";
        internal const string NoEncoderAtAll = "Windows offers no H.264 encoder on this PC.";
        internal const string NoAnswerInTime = "Windows did not say within 10 seconds which video encoders this PC has.";

        internal const string NoHardwareEncoderOnThisPhone = "this phone has no hardware H.264 encoder that takes the frames the capture makes. Turn on Android Allow Software " +
                                                             "Encoder in Protokite > Playtest > Settings to record with Android's software encoder on the processor instead, which " +
                                                             "costs the game frame rate and battery.";
        internal const string NoEncoderOnThisPhone = "this phone offers no H.264 encoder.";
        internal const string NoEncoderTakesTheFrames = "this phone's H.264 encoders take none of the frames the capture makes (NV12).";
        internal const string NoAnswerInTimeOnThisPhone = "Android did not say within 10 seconds which video encoders this phone has.";
        internal const string VideoTurnedOffOnAndroid = "Record Video On Android is off in Protokite > Playtest > Settings.";

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

        /// <summary>Stands in for what the platform offers, on the asking thread, so a test can act out a machine with no encoder, or none reachable (the list null, with why).</summary>
        internal static Func<KeyValuePair<List<ProtokitePlaytestEncoderFound>, string>> LookForEncodersForTesting;

        /// <summary>How long the platform is given to answer, when a test sets it; 10 seconds otherwise.</summary>
        internal static TimeSpan? LongestWaitForAnAnswerForTesting;

        /// <summary>Makes this process decide as an Android player does, so the editor's tests can drive a phone's rules.</summary>
        internal static bool ActAsAndroidForTesting;

        /// <summary>Whether this launch has asked the platform, or is asking; for tests.</summary>
        internal static bool AskedThisLaunchForTesting
        {
            get
            {
                lock (CheckLock)
                    return _checked || _looking != null;
            }
        }

        /// <summary>Whether this process decides as an Android player.</summary>
        internal static bool OnAndroid => IsBuiltForAndroid || ActAsAndroidForTesting;

        /// <summary>Whether this build and this process should have video: an Android player, or a Windows build not running as 32-bit or ARM.</summary>
        internal static bool ShouldHaveVideo
        {
            get
            {
                // The phone's encoder is the system's, reached the same way on every Android processor.
                if (OnAndroid)
                    return true;
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

        /// <summary>The H.264 encoders this machine offers, in the platform's order; null, with why, when it can record none. Asked once a launch, waited for 10 seconds from the asking at most.</summary>
        internal static List<ProtokitePlaytestEncoderFound> EncodersOnThisPc(out string whyNone)
        {
            Thread looking = StartLookingForEncoders();
            if (looking != null)
                looking.Join(TimeLeftToAnswer());
            lock (CheckLock)
            {
                whyNone = _checked ? _whyNoneFound : OnAndroid ? NoAnswerInTimeOnThisPhone : NoAnswerInTime;
                return _checked ? _found : null;
            }
        }

        /// <summary>Whether asking for an encoder now takes no wait: the platform has answered, or its 10 seconds to answer are up. Starts the asking when nothing has.</summary>
        internal static bool FinishedLookingForEncoders() => StartLookingForEncoders() == null || TimeLeftToAnswer() == TimeSpan.Zero;

        private static TimeSpan TimeLeftToAnswer()
        {
            lock (CheckLock)
            {
                TimeSpan left = _stopWaitingForTheAnswerAt - DateTime.UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        /// <summary>Starts asking the platform which encoders this machine has, on a thread of its own, unless that is done or under way; the thread asking, or null once answered.</summary>
        // A thread of its own, so Unity's main thread never has COM started for it nor waits for the answer (tens of milliseconds on either platform).
        internal static Thread StartLookingForEncoders()
        {
            lock (CheckLock)
            {
                // A build that records no video has nothing to ask.
                if (_checked || !ShouldHaveVideo)
                    return null;
                if (_looking != null)
                    return _looking;
                int launch = _launch;
                bool onAndroid = OnAndroid;
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
                        why = (onAndroid ? "Android could not say which video encoders this phone has: " : "Windows could not say which video encoders this PC has: ") + ex.Message;
                    }
                    lock (CheckLock)
                    {
                        // A new launch began while the platform was asked; its own check answers it. What this answer holds is left to
                        // the collector: giving a Java object back needs a thread attached to Java, and this one no longer is.
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

        /// <summary>An encoder for a recording, or null with why when this machine can record none with these settings; the first "no video" of a launch is logged.</summary>
        public static IProtokitePlaytestVideoEncoder Create(bool allowSoftwareEncoder, out string whyNot)
        {
            whyNot = WhyThisPcRecordsNoVideo(allowSoftwareEncoder);
            if (whyNot == null)
                return NewEncoder(allowSoftwareEncoder);
            lock (CheckLock)
            {
                if (_reportedNoVideo)
                    return null;
                _reportedNoVideo = true;
            }
            // The reason may be the machine's rather than the build's, so the line names neither.
            string line = LogPrefix + "This launch records no playtest video: " + whyNot + " Everything else in the playtest still runs.";
            // Expected where video is not built at all; a build that should have it says so louder.
            if (ShouldHaveVideo)
                Debug.LogWarning(line);
            else
                Debug.Log(line);
            return null;
        }

        /// <summary>Whether the platform has already answered that this machine records no video with these settings; never asks, and never waits.</summary>
        internal static bool AnsweredThatItRecordsNoVideo(bool allowSoftwareEncoder)
        {
            lock (CheckLock)
            {
                if (!_checked)
                    return false;
            }
            // Answered, so this finds the answer without waiting.
            return WhyThisPcRecordsNoVideo(allowSoftwareEncoder) != null;
        }

        /// <summary>Why this machine records no video with these settings, or null when it can.</summary>
        internal static string WhyThisPcRecordsNoVideo(bool allowSoftwareEncoder)
        {
            if (!ShouldHaveVideo)
                return NoVideoOnThisPlatform;
            List<ProtokitePlaytestEncoderFound> found = EncodersOnThisPc(out string whyNone);
            if (found == null)
                return whyNone;
            if (OnAndroid)
                return ChoosePhoneEncoder(found, allowSoftwareEncoder, out string whyNot) == null ? whyNot : null;
            if (found.Exists(encoder => encoder.InHardware))
                return null;
            if (!allowSoftwareEncoder)
                return NoGraphicsCardEncoder;
            return found.Count > 0 ? null : NoEncoderAtAll;
        }

        /// <summary>The phone encoder a recording uses: the first hardware one that takes NV12, else the first software one when allowed; null, with why.</summary>
        internal static ProtokitePlaytestEncoderFound ChoosePhoneEncoder(List<ProtokitePlaytestEncoderFound> found, bool allowSoftwareEncoder, out string whyNot)
        {
            whyNot = null;
            if (found.Count == 0)
            {
                whyNot = NoEncoderOnThisPhone;
                return null;
            }
            ProtokitePlaytestEncoderFound hardware = found.Find(encoder => encoder.InHardware && encoder.TakesNv12);
            if (hardware != null)
                return hardware;
            ProtokitePlaytestEncoderFound software = allowSoftwareEncoder ? found.Find(encoder => !encoder.InHardware && encoder.TakesNv12) : null;
            if (software != null)
                return software;
            // Said for what the studio can change: turning the software encoder on helps only where one takes the frames.
            bool anyTakesTheFrames = found.Exists(encoder => encoder.TakesNv12);
            whyNot = !anyTakesTheFrames ? NoEncoderTakesTheFrames : NoHardwareEncoderOnThisPhone;
            return null;
        }

        /// <summary>Fits the video to what a phone's encoder takes: smaller sizes, then 15 and 10 frames a second, with a note; false, with why, when none.</summary>
        // The settings are left as they were when it takes none; an encoder that does not say what it takes is left to try.
        internal static bool FitTheEncoder(IProtokitePlaytestVideoEncoder encoder, ProtokitePlaytestVideoSettings settings, int screenWidth, int screenHeight,
            out string note, out string whyNot)
        {
            note = null;
            whyNot = null;
            ProtokitePlaytestEncoderFound found = (encoder as ProtokitePlaytestAndroidVideoEncoder)?.Found;
            if (found?.TakesSizeAndRate == null
                || !ProtokitePlaytestVideoSettings.FitVideoSize(screenWidth, screenHeight, settings.MaxVideoWidth, settings.MaxVideoHeight, out int askedWidth, out int askedHeight))
                return true;
            int askedRate = settings.FramesPerSecond;
            int smallestWidth = askedWidth;
            int smallestHeight = askedHeight;
            foreach (int rate in RatesToTry(askedRate))
            {
                foreach (int eighths in new[] { 8, 6, 4, 3 })
                {
                    int boxWidth = Math.Max(ProtokitePlaytestVideoSettings.SideMultiple, settings.MaxVideoWidth * eighths / 8);
                    int boxHeight = Math.Max(ProtokitePlaytestVideoSettings.SideMultiple, settings.MaxVideoHeight * eighths / 8);
                    ProtokitePlaytestVideoSettings.FitVideoSize(screenWidth, screenHeight, boxWidth, boxHeight, out int width, out int height);
                    smallestWidth = Math.Min(smallestWidth, width);
                    smallestHeight = Math.Min(smallestHeight, height);
                    if (!found.TakesSizeAndRate(width, height, rate))
                        continue;
                    if (width != askedWidth || height != askedHeight || rate != askedRate)
                        note = $"{found} does not take {askedWidth}x{askedHeight} at {askedRate} frames a second, so this recording is {width}x{height} at {rate}.";
                    settings.MaxVideoWidth = boxWidth;
                    settings.MaxVideoHeight = boxHeight;
                    settings.FramesPerSecond = rate;
                    return true;
                }
            }
            whyNot = $"{found} takes none of the sizes from {askedWidth}x{askedHeight} down to {smallestWidth}x{smallestHeight}, at {askedRate} frames a second or fewer.";
            return false;
        }

        private static IEnumerable<int> RatesToTry(int asked)
        {
            yield return asked;
            if (asked > 15)
                yield return 15;
            if (asked > 10)
                yield return 10;
        }

        /// <summary>Forgets what was checked and reported, as a new launch does; statics outlive Play Mode with domain reload off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForNewLaunch()
        {
            lock (CheckLock)
            {
                _launch++;
                Release(_found);
                _checked = false;
                _found = null;
                _whyNoneFound = null;
                _looking = null;
                _stopWaitingForTheAnswerAt = DateTime.MinValue;
                _reportedNoVideo = false;
            }
        }

        // Gives back what the platform lent for a check that is forgotten.
        private static void Release(List<ProtokitePlaytestEncoderFound> found)
        {
            if (found == null)
                return;
            foreach (ProtokitePlaytestEncoderFound encoder in found)
            {
                try
                {
                    encoder.HeldFromThePlatform?.Dispose();
                }
                catch (Exception)
                {
                    // Nothing more can be done with an object the platform will not take back.
                }
                encoder.HeldFromThePlatform = null;
                encoder.TakesSizeAndRate = null;
            }
        }

        private static IProtokitePlaytestVideoEncoder NewEncoder(bool allowSoftwareEncoder)
        {
            if (!OnAndroid)
                return NewWindowsEncoder();
            List<ProtokitePlaytestEncoderFound> found = EncodersOnThisPc(out _);
            ProtokitePlaytestEncoderFound chosen = found == null ? null : ChoosePhoneEncoder(found, allowSoftwareEncoder, out _);
            return chosen == null ? null : new ProtokitePlaytestAndroidVideoEncoder(chosen);
        }

        private static List<ProtokitePlaytestEncoderFound> LookForEncodersOnThisThread(out string whyNone)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return ProtokitePlaytestMediaCodec.H264EncodersOnThisThread(out whyNone);
#else
            return LookForWindowsEncodersOnThisThread(out whyNone);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        internal const bool IsBuiltForAndroid = true;
#else
        internal const bool IsBuiltForAndroid = false;
#endif

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        internal static readonly bool IsBuiltWithVideo = true;

        private static IProtokitePlaytestVideoEncoder NewWindowsEncoder() => new ProtokitePlaytestWindowsVideoEncoder();

        private static List<ProtokitePlaytestEncoderFound> LookForWindowsEncodersOnThisThread(out string whyNone)
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
        internal static readonly bool IsBuiltWithVideo = IsBuiltForAndroid;

        private static IProtokitePlaytestVideoEncoder NewWindowsEncoder() => null;

        private static List<ProtokitePlaytestEncoderFound> LookForWindowsEncodersOnThisThread(out string whyNone)
        {
            whyNone = NoVideoOnThisPlatform;
            return null;
        }
#endif
    }
}
