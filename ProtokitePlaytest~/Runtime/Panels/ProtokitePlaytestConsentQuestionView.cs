using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Protokite.Playtest
{
    /// <summary>The consent question's words and buttons. The panel draws it; the answer goes to whoever built it.</summary>
    internal static class ProtokitePlaytestConsentQuestionView
    {
        /// <summary>One answer as the player is offered it.</summary>
        internal readonly struct Option
        {
            public readonly ProtokitePlaytestConsentChoice Choice;
            public readonly string Title;
            public readonly string Explanation;

            public Option(ProtokitePlaytestConsentChoice choice, string title, string explanation)
            {
                Choice = choice;
                Title = title;
                Explanation = explanation;
            }
        }

        internal const string Heading = "What this playtest may collect";

        // Says in front of the player that this is not the game's own privacy or analytics question.
        internal const string Introduction = "You are playing a playtest build of this game. Choose what the playtest may collect while you play. "
            + "This is the playtest's own question: it is separate from any privacy or analytics choice the game itself asks you about, "
            + "and your answer here changes nothing else.";

        internal const string Footnote = "Your answer is kept on this device and used every time you play.";

        /// <summary>The four answers in the order offered: everything, each half on its own, then nothing. Read by the buttons and the tests alike.</summary>
        internal static readonly Option[] Options =
        {
            new Option(ProtokitePlaytestConsentChoice.VideoAndPlayData, "Record my screen and collect play data",
                "The playtest records what is on screen while I play, and collects how the game runs for me: frame rate, memory and the levels I load."),
            new Option(ProtokitePlaytestConsentChoice.VideoOnly, "Record my screen only",
                "The playtest records what is on screen. It collects nothing about how the game runs."),
            new Option(ProtokitePlaytestConsentChoice.PlayDataOnly, "Collect play data only",
                "The playtest collects how the game runs for me. Nothing on my screen is recorded."),
            new Option(ProtokitePlaytestConsentChoice.Nothing, "Collect nothing",
                "The playtest collects nothing: nothing is recorded, no play data is sent, and no playtest session is made. A feedback report you choose to send still reaches the studio.")
        };

        /// <summary>The name a button is found by: its answer's wire spelling.</summary>
        internal static string ButtonName(ProtokitePlaytestConsentChoice choice) => "protokite-consent-" + ProtokitePlaytestConsent.ToWire(choice);

        /// <summary>
        /// The view; <paramref name="chosen"/> hears a deliberate answer. While <paramref name="theGameKeepsTheCursorLocked"/> says so, a
        /// mouse press is ignored and <paramref name="pressIgnoredForALockedCursor"/> told: it lands at the centre, not where the player aimed.
        /// </summary>
        internal static VisualElement Build(Action<ProtokitePlaytestConsentChoice> chosen, Func<bool> theGameKeepsTheCursorLocked, Action pressIgnoredForALockedCursor)
        {
            Action<ProtokitePlaytestConsentChoice, EventBase> deliberate = ProtokitePlaytestPanel.DeliberateAnswers(chosen, theGameKeepsTheCursorLocked, pressIgnoredForALockedCursor);
            List<Button> answers = new List<Button>();
            foreach (Option option in Options)
            {
                ProtokitePlaytestConsentChoice choice = option.Choice;
                answers.Add(ProtokitePlaytestPanel.AnswerButton(ButtonName(choice), option.Title, option.Explanation, press => deliberate(choice, press)));
            }
            return ProtokitePlaytestPanel.QuestionView("protokite-consent-question", Heading, Introduction, answers, Footnote);
        }
    }
}
