using System;
using System.Threading;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Which networks the player lets this playtest upload its recordings on: asked on a phone, after an answer that lets the screen be recorded.</summary>
    public enum ProtokitePlaytestUploadNetworkChoice
    {
        /// <summary>The player has not answered: recordings upload on any network, as in a build that never asks. A phone build that asks puts the question, and its Protokite session waits for the answer.</summary>
        NotAnswered,
        /// <summary>Recordings wait on the device until it is on Wi-Fi (or any network that is not its mobile data).</summary>
        WiFiOnly,
        /// <summary>Recordings upload on any network, mobile data included.</summary>
        WiFiAndMobileData
    }

    /// <summary>What each answer allows and how it is spelt, with no panel, file or network near it.</summary>
    internal static class ProtokitePlaytestUploadNetwork
    {
        private static readonly (ProtokitePlaytestUploadNetworkChoice Choice, string Wire)[] Spellings =
        {
            // What a session start says when the player was not asked, or has not answered; never saved.
            (ProtokitePlaytestUploadNetworkChoice.NotAnswered, "not_asked"),
            (ProtokitePlaytestUploadNetworkChoice.WiFiOnly, "wifi_only"),
            (ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, "wifi_and_mobile_data")
        };

        /// <summary>Whether a recording may upload on the device's network: Wi-Fi only waits while it is on its mobile data or on no network at all.</summary>
        // Unity reports a phone's mobile data as carrier data and every other network (Wi-Fi, Ethernet, tethering) as local. The network
        // is read only for an answer that can hold uploads back.
        internal static bool AllowsUploadOn(ProtokitePlaytestUploadNetworkChoice choice, Func<NetworkReachability> network)
            => choice != ProtokitePlaytestUploadNetworkChoice.WiFiOnly || network() == NetworkReachability.ReachableViaLocalAreaNetwork;

        /// <summary>How the answer is spelt in the saved file and in what a session start sends.</summary>
        internal static string ToWire(ProtokitePlaytestUploadNetworkChoice choice)
        {
            foreach ((ProtokitePlaytestUploadNetworkChoice Choice, string Wire) spelling in Spellings)
            {
                if (spelling.Choice == choice)
                    return spelling.Wire;
            }
            return Spellings[0].Wire;
        }

        /// <summary>Letter for letter: any other spelling, another letter case included, is NotAnswered.</summary>
        internal static ProtokitePlaytestUploadNetworkChoice FromWire(string wire)
        {
            foreach ((ProtokitePlaytestUploadNetworkChoice Choice, string Wire) spelling in Spellings)
            {
                if (string.Equals(wire, spelling.Wire, StringComparison.Ordinal))
                    return spelling.Choice;
            }
            return ProtokitePlaytestUploadNetworkChoice.NotAnswered;
        }

        /// <summary>One sentence saying what the answer allows, for the log.</summary>
        internal static string Describe(ProtokitePlaytestUploadNetworkChoice choice)
        {
            switch (choice)
            {
                case ProtokitePlaytestUploadNetworkChoice.WiFiOnly: return "The player lets this playtest upload recordings on Wi-Fi only; on mobile data they wait on the device.";
                case ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData: return "The player lets this playtest upload recordings on Wi-Fi and on mobile data.";
                default: return "The player's answer about which networks recordings upload on is forgotten, so they upload on any network; a phone build that asks puts the question again.";
            }
        }

        /// <summary>About how many megabytes a minute of video sends at this bitrate, and never less than 1.</summary>
        internal static int MegabytesAMinute(int bitrateKbps) => Math.Max(1, (int)Math.Round(bitrateKbps * 60.0 / 8.0 / 1000.0));
    }

    /// <summary>The player's answer about upload networks, in a file beside the consent answer's, so it holds from one launch to the next.</summary>
    internal sealed class ProtokitePlaytestUploadNetworkFile : ProtokitePlaytestAnswerFile<ProtokitePlaytestUploadNetworkChoice>
    {
        internal ProtokitePlaytestUploadNetworkFile(string path)
            : base(path, "playtest_upload_network", ProtokitePlaytestUploadNetwork.ToWire, ProtokitePlaytestUploadNetwork.FromWire,
                ProtokitePlaytestUploadNetworkChoice.NotAnswered)
        {
        }

        /// <summary>ProtokitePlaytest/playtest_upload_network.json in the game's persistent data folder. Main thread only.</summary>
        internal static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, "ProtokitePlaytest", "playtest_upload_network.json");
    }

    public static partial class ProtokitePlaytest
    {
        private static ProtokitePlaytestUploadNetworkChoice? _savedUploadNetwork;
        private static bool _askedToChangeUploadNetwork;
        private static bool _saidUploadsWaitForWiFi;
        private static CancellationTokenSource _stopUploadsOffWiFi;
        // Made once: a method group turned into a delegate on every call would make garbage each frame uploads wait or run.
        private static readonly Func<NetworkReachability> ReadTheNetwork = CurrentNetwork;

        /// <summary>Where the player's answer about upload networks is kept, when a test sets it; the game's persistent data folder otherwise.</summary>
        internal static string UploadNetworkFilePathForTesting;

        /// <summary>Stands in for the network Unity reports, so tests move the device between Wi-Fi, mobile data and no network.</summary>
        internal static Func<NetworkReachability> NetworkForTesting;

        /// <summary>What the player answered about the networks recordings upload on, on this device, or NotAnswered when they have not. Main thread only.</summary>
        public static ProtokitePlaytestUploadNetworkChoice PlayersUploadNetworkAnswer => SavedUploadNetwork();

        /// <summary>
        /// Saves which networks the player lets recordings upload on and applies it at once (NotAnswered forgets it, so a phone build that asks
        /// puts the question again); false when it could not be saved, though a choice still holds for this launch. Main thread only.
        /// </summary>
        public static bool SetPlaytestUploadNetwork(ProtokitePlaytestUploadNetworkChoice choice)
        {
            ProtokitePlaytestUploadNetworkFile file = new ProtokitePlaytestUploadNetworkFile(UploadNetworkFilePath);
            bool saved = file.Save(choice);
            // Held for the launch even when it could not be kept: it is what the player chose, and asking again at once would ask for ever.
            _savedUploadNetwork = choice;
            _askedToChangeUploadNetwork = false;
            _saidUploadsWaitForWiFi = false;
            if (saved)
                Debug.Log(LogPrefix + ProtokitePlaytestUploadNetwork.Describe(choice) + (choice == ProtokitePlaytestUploadNetworkChoice.NotAnswered ? "" : " The answer is kept in " + file.Path + "."));
            else if (choice == ProtokitePlaytestUploadNetworkChoice.NotAnswered)
                Debug.LogWarning(LogPrefix + $"The player's answer about which networks recordings upload on could not be removed from {file.Path}, so the next launch reads it again.");
            else
                Debug.LogWarning(LogPrefix + $"The player's answer about which networks recordings upload on could not be saved to {file.Path}; it holds for this launch, and the next launch asks again.");
            _stateAtLastRefresh = null;
            Refresh();
            return saved;
        }

        private static string UploadNetworkFilePath => UploadNetworkFilePathForTesting ?? ProtokitePlaytestUploadNetworkFile.DefaultPath;

        // Read once a launch, as the consent answer is.
        private static ProtokitePlaytestUploadNetworkChoice SavedUploadNetwork()
        {
            if (!_savedUploadNetwork.HasValue)
                _savedUploadNetwork = new ProtokitePlaytestUploadNetworkFile(UploadNetworkFilePath).Read();
            return _savedUploadNetwork.Value;
        }

        private static NetworkReachability CurrentNetwork() => NetworkForTesting?.Invoke() ?? Application.internetReachability;

        /// <summary>Whether the player's answer holds recordings back on the network the device is on now. Main thread only.</summary>
        internal static bool UploadsWaitForWiFi() => !ProtokitePlaytestUploadNetwork.AllowsUploadOn(SavedUploadNetwork(), ReadTheNetwork);

        // The question is about a phone's recordings: put only where a build that asks records the phone's screen and the player let it.
        // Record Video On Android is read as the recording reads it: off at launch holds for the launch, and off now is off.
        private static bool UploadNetworkQuestionFitsThisPlayer(ProtokitePlaytestSettings settings)
            => settings != null && AsksThePlayer(settings) && ProtokitePlaytestVideoEncoders.OnAndroid && settings.RecordVideoOnAndroid
               && !_videoTurnedOffOnThisPlatform && ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent());

        // ...and this launch's playtest turns video on, on a phone that has not already said it can record none, so the answer matters now.
        private static bool UploadNetworkQuestionFitsThisLaunch(ProtokitePlaytestSettings settings)
            => VideoIsOnInTheLoadedConfig() && UploadNetworkQuestionFitsThisPlayer(settings)
               && !ProtokitePlaytestVideoEncoders.AnsweredThatItRecordsNoVideo(settings.AndroidAllowSoftwareEncoder);

        /// <summary>Whether the question waits for its first answer this launch; the Protokite session start waits with it, so it carries the answer.</summary>
        private static bool UploadNetworkAnswerIsDue()
            => SavedUploadNetwork() == ProtokitePlaytestUploadNetworkChoice.NotAnswered && UploadNetworkQuestionFitsThisLaunch(ProtokitePlaytestSettings.Load());

        // While the question is due, and while the config that decides whether it is put is still on its way: recordings earlier launches
        // left are ready within frames of a launch, the config a moment later.
        private static bool UploadNetworkAnswerMayStillCome()
        {
            if (SavedUploadNetwork() != ProtokitePlaytestUploadNetworkChoice.NotAnswered)
                return false;
            ProtokitePlaytestSettings settings = ProtokitePlaytestSettings.Load();
            if (!UploadNetworkQuestionFitsThisPlayer(settings))
                return false;
            return Status == ProtokitePlaytestStatus.FetchingPlaytestConfig || UploadNetworkQuestionFitsThisLaunch(settings);
        }

        /// <summary>Whether recordings wait to upload because of the player's network answer, given or still to come: none of them is deleted to make room then.</summary>
        private static bool RecordingsWaitForThePlayersNetwork() => UploadsWaitForWiFi() || UploadNetworkAnswerMayStillCome();

        // Said once each time uploads begin to wait, so a device moving between networks is not logged every frame.
        private static void SayUploadsWaitForWiFi()
        {
            if (_saidUploadsWaitForWiFi)
                return;
            _saidUploadsWaitForWiFi = true;
            string onWhat = CurrentNetwork() == NetworkReachability.NotReachable ? "on no network" : "on mobile data";
            Debug.Log(LogPrefix + $"Recordings wait on this device before they upload: the player chose Wi-Fi only, and the device is {onWhat}. " +
                      "They upload once it is on Wi-Fi, in this launch or a later one.");
        }

        // An upload begins on a network the player allows: the token it stops on when the device leaves Wi-Fi (one for every upload
        // under way, replaced once used), and a wait that comes later is said again.
        private static CancellationToken BeginUploadOnAnAllowedNetwork()
        {
            if (_stopUploadsOffWiFi == null)
                _stopUploadsOffWiFi = new CancellationTokenSource();
            _saidUploadsWaitForWiFi = false;
            return _stopUploadsOffWiFi.Token;
        }

        /// <summary>Once a frame: an upload under way stops when the player's answer no longer allows the network, and its recording is kept for Wi-Fi.</summary>
        private static void StopUploadsTheNetworkNoLongerAllows()
        {
            if (_stopUploadsOffWiFi == null || !UploadsUnderWay() || !UploadsWaitForWiFi())
                return;
            // Not disposed: an upload may still read its token.
            _stopUploadsOffWiFi.Cancel();
            _stopUploadsOffWiFi = null;
            Debug.Log(LogPrefix + "A recording's upload stopped: the player allows Wi-Fi only, and the device is not on Wi-Fi now. The recording is kept and sent again on Wi-Fi.");
        }

        private static bool UploadsUnderWay()
            => (ThisLaunchsUpload != null && !ThisLaunchsUpload.IsCompleted) || (EarlierUploads != null && !EarlierUploads.IsCompleted);

        private static void ResetUploadNetworkForNewLaunch()
        {
            _savedUploadNetwork = null;
            _askedToChangeUploadNetwork = false;
            _saidUploadsWaitForWiFi = false;
            // The launch's own cancel has stopped whatever this was for.
            _stopUploadsOffWiFi = null;
        }
    }
}
