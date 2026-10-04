using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Protokite.Playtest
{
    /// <summary>The question that follows the consent question on a phone: which networks recordings may upload on. The panel draws it; the answer goes to whoever built it.</summary>
    internal static class ProtokitePlaytestUploadNetworkQuestionView
    {
        internal const string Heading = "When your recordings may upload";

        internal const string Introduction = "The playtest uploads its recording of your screen to the game's studio. Choose whether it may use your mobile data "
            + "for that, or waits until you are on Wi-Fi. Recording goes on either way, and a recording waits on this device until it can be sent.";

        internal const string Footnote = "Your answer is kept on this device and used every time you play.";

        internal const string WiFiOnlyTitle = "Upload on Wi-Fi only";
        internal const string WiFiOnlyExplanation = "Recordings wait on this device until you are on Wi-Fi. Nothing is uploaded over your mobile data.";
        internal const string WiFiAndMobileDataTitle = "Upload on Wi-Fi or mobile data";

        /// <summary>The name a button is found by: its answer's wire spelling.</summary>
        internal static string ButtonName(ProtokitePlaytestUploadNetworkChoice choice) => "protokite-upload-network-" + ProtokitePlaytestUploadNetwork.ToWire(choice);

        /// <summary>What uploading on mobile data costs the player, in the megabytes a minute of video comes to at the recording's bitrate.</summary>
        internal static string WiFiAndMobileDataExplanation(int megabytesAMinute)
            => $"Recordings upload as soon as there is a connection, which can use about {megabytesAMinute} MB of your mobile data for each minute you play.";

        /// <summary>The view, the answer that spends none of the player's data first, with the consent question's guards on a deliberate answer.</summary>
        internal static VisualElement Build(Action<ProtokitePlaytestUploadNetworkChoice> chosen, int megabytesAMinute, Func<bool> theGameKeepsTheCursorLocked,
            Action pressIgnoredForALockedCursor)
        {
            Action<ProtokitePlaytestUploadNetworkChoice, EventBase> deliberate = ProtokitePlaytestPanel.DeliberateAnswers(chosen, theGameKeepsTheCursorLocked, pressIgnoredForALockedCursor);
            List<Button> answers = new List<Button>
            {
                ProtokitePlaytestPanel.AnswerButton(ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiOnly), WiFiOnlyTitle, WiFiOnlyExplanation,
                    press => deliberate(ProtokitePlaytestUploadNetworkChoice.WiFiOnly, press)),
                ProtokitePlaytestPanel.AnswerButton(ButtonName(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData), WiFiAndMobileDataTitle,
                    WiFiAndMobileDataExplanation(megabytesAMinute), press => deliberate(ProtokitePlaytestUploadNetworkChoice.WiFiAndMobileData, press))
            };
            return ProtokitePlaytestPanel.QuestionView("protokite-upload-network-question", Heading, Introduction, answers, Footnote);
        }
    }
}
