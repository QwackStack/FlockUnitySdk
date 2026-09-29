using System;
using UnityEngine;
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

        /// <summary>
        /// A press this soon after the question appears is ignored: a player still clicking at the game (firing, say) would
        /// otherwise answer a question they never read, with whichever button sits under a centred cursor.
        /// </summary>
        internal const float SecondsBeforeAnAnswerCounts = 0.5f;

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
            double shownAt = Time.realtimeSinceStartupAsDouble;
            Action<ProtokitePlaytestConsentChoice, EventBase> deliberate = (choice, press) =>
            {
                if (Time.realtimeSinceStartupAsDouble - shownAt < SecondsBeforeAnAnswerCounts)
                    return;
                if ((press is IPointerEvent || press is IMouseEvent) && theGameKeepsTheCursorLocked())
                {
                    pressIgnoredForALockedCursor();
                    return;
                }
                chosen(choice);
            };
            VisualElement view = new VisualElement { name = "protokite-consent-question" };
            view.Add(ProtokitePlaytestPanel.Text(Heading, 22, ProtokitePlaytestPanel.TextColour, bold: true));

            Label introduction = ProtokitePlaytestPanel.Text(Introduction, 13, ProtokitePlaytestPanel.HelpColour);
            introduction.style.marginTop = 8;
            introduction.style.marginBottom = 18;
            view.Add(introduction);

            foreach (Option option in Options)
                view.Add(BuildOption(option, deliberate));

            Label footnote = ProtokitePlaytestPanel.Text(Footnote, 12, ProtokitePlaytestPanel.HelpColour);
            footnote.style.marginTop = 6;
            view.Add(footnote);
            return view;
        }

        private static Button BuildOption(Option option, Action<ProtokitePlaytestConsentChoice, EventBase> chosen)
        {
            ProtokitePlaytestConsentChoice choice = option.Choice;
            Button button = new Button { name = ButtonName(choice), text = "" };
            // With the press that made it, so a mouse press is told from a key or pad press.
            button.clickable.clickedWithEventInfo += press => chosen(choice, press);
            button.style.flexDirection = FlexDirection.Column;
            button.style.alignItems = Align.Stretch;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.marginTop = 0;
            button.style.marginBottom = 10;
            button.style.paddingLeft = 14;
            button.style.paddingRight = 14;
            button.style.paddingTop = 11;
            button.style.paddingBottom = 11;
            button.style.backgroundColor = ProtokitePlaytestPanel.OptionColour;
            ProtokitePlaytestPanel.SetBorder(button.style, 1, ProtokitePlaytestPanel.OptionBorderColour);
            ProtokitePlaytestPanel.SetRadius(button.style, 6);

            Label title = ProtokitePlaytestPanel.Text(option.Title, 15, ProtokitePlaytestPanel.TextColour, bold: true);
            title.pickingMode = PickingMode.Ignore;
            Label explanation = ProtokitePlaytestPanel.Text(option.Explanation, 12, ProtokitePlaytestPanel.HelpColour);
            explanation.pickingMode = PickingMode.Ignore;
            explanation.style.marginTop = 3;
            button.Add(title);
            button.Add(explanation);

            // No theme gives hover or focus a look, so the button gives itself one.
            button.RegisterCallback<PointerEnterEvent>(_ => button.style.backgroundColor = ProtokitePlaytestPanel.OptionHighlightColour);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.style.backgroundColor = ProtokitePlaytestPanel.OptionColour);
            button.RegisterCallback<FocusInEvent>(_ => ProtokitePlaytestPanel.SetBorder(button.style, 2, ProtokitePlaytestPanel.OptionFocusBorderColour));
            button.RegisterCallback<FocusOutEvent>(_ => ProtokitePlaytestPanel.SetBorder(button.style, 1, ProtokitePlaytestPanel.OptionBorderColour));
            return button;
        }
    }
}
