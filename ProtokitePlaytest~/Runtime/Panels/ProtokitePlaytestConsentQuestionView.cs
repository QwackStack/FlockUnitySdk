using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Protokite.Playtest
{
    /// <summary>The consent question's words and buttons, and the pop-up its link opens. The panel draws it; the answer goes to whoever built it.</summary>
    internal static class ProtokitePlaytestConsentQuestionView
    {
        /// <summary>One answer as the player is offered it.</summary>
        internal readonly struct Option
        {
            public readonly ProtokitePlaytestConsentChoice Choice;
            public readonly string Title;

            public Option(ProtokitePlaytestConsentChoice choice, string title)
            {
                Choice = choice;
                Title = title;
            }
        }

        /// <summary>One part of what the pop-up says is collected.</summary>
        internal readonly struct Collected
        {
            public readonly string Title;
            public readonly string Explanation;

            public Collected(string title, string explanation)
            {
                Title = title;
                Explanation = explanation;
            }
        }

        internal const string Heading = "Help the developers improve this game?";
        internal const string Introduction = "We may record your screen and how the game runs while you play. Only the game's developers see it.";
        internal const string WhatIsCollectedLink = "What's collected?";
        internal const string WhatIsCollectedLinkName = "protokite-consent-what-is-collected-link";
        internal const string WhatIsCollectedName = "protokite-consent-what-is-collected";
        internal const string WhatIsCollectedHeading = "What's collected";
        internal const string BackName = "protokite-consent-back";
        internal const string QwacksLogoName = "protokite-consent-qwacks-logo";

        /// <summary>The name <see cref="Resources.Load(string)"/> finds the Qwacks logo under, drawn at the top of the question.</summary>
        internal const string QwacksLogoResource = "ProtokitePlaytestQwacksLogo";

        /// <summary>The two answers in the order offered: everything, then nothing. Read by the buttons and the tests alike.</summary>
        internal static readonly Option[] Options =
        {
            new Option(ProtokitePlaytestConsentChoice.VideoAndPlayData, "Yes, help the developers"),
            new Option(ProtokitePlaytestConsentChoice.Nothing, "Not now")
        };

        /// <summary>What the pop-up explains, in order.</summary>
        internal static readonly Collected[] WhatIsCollected =
        {
            new Collected("Your screen", "A video of your screen while you play, if this game records one."),
            new Collected("How the game runs", "Frame rate, memory and the levels you load."),
            new Collected("Who sees it", "Only the game's developers, to find what to fix."),
            new Collected("Not now", "Nothing is recorded or sent. Feedback you choose to send still reaches the developers."),
            new Collected("Your other choices", "This does not change any privacy choice the game asks you about."),
            new Collected("Your answer", "Saved on this device for next time.")
        };

        /// <summary>The name a button is found by: its answer's wire spelling.</summary>
        internal static string ButtonName(ProtokitePlaytestConsentChoice choice) => "protokite-consent-" + ProtokitePlaytestConsent.ToWire(choice);

        /// <summary>
        /// The view, drawn on <paramref name="panel"/>; <paramref name="chosen"/> hears a deliberate answer. While the game keeps the cursor
        /// locked, a mouse press is ignored and <paramref name="pressIgnoredForALockedCursor"/> told: it lands at the centre, not where the player aimed.
        /// </summary>
        internal static VisualElement Build(ProtokitePlaytestPanel panel, Action<ProtokitePlaytestConsentChoice> chosen, Action pressIgnoredForALockedCursor)
        {
            Action<ProtokitePlaytestConsentChoice, EventBase> deliberate = ProtokitePlaytestPanel.DeliberateAnswers(chosen, () => panel.TheGameKeepsTheCursorLocked, pressIgnoredForALockedCursor);
            // A second click on Back lands on the answer under it, so a press this soon after the pop-up closes is ignored too.
            double answersCountFrom = 0;
            List<Button> answers = new List<Button>();
            foreach (Option option in Options)
            {
                ProtokitePlaytestConsentChoice choice = option.Choice;
                answers.Add(ProtokitePlaytestPanel.AnswerButton(ButtonName(choice), option.Title, null, press =>
                {
                    if (Time.realtimeSinceStartupAsDouble >= answersCountFrom)
                        deliberate(choice, press);
                    else
                        ProtokitePlaytestPanel.RefusePress(press);
                }, offeredFirst: answers.Count == 0));
            }
            Button link = ProtokitePlaytestPanel.LinkButton(WhatIsCollectedLinkName, WhatIsCollectedLink, () => panel.ShowOnTop(BuildWhatIsCollected(() =>
            {
                panel.CloseOnTop();
                answersCountFrom = Time.realtimeSinceStartupAsDouble + ProtokitePlaytestPanel.SecondsBeforeAnAnswerCounts;
            })));
            VisualElement view = ProtokitePlaytestPanel.QuestionView("protokite-consent-question", Heading, Introduction, answers, null, link);
            Texture2D logo = Resources.Load<Texture2D>(QwacksLogoResource);
            if (logo != null)
                view.Insert(0, QwacksLogo(logo));
            return view;
        }

        // Centred above the heading, at a fixed size whatever the image's own.
        private static Image QwacksLogo(Texture2D logo)
        {
            Image image = new Image { name = QwacksLogoName, image = logo, scaleMode = ScaleMode.ScaleToFit };
            image.pickingMode = PickingMode.Ignore;
            image.style.width = 48;
            image.style.height = 48;
            image.style.alignSelf = Align.Center;
            image.style.marginBottom = 12;
            return image;
        }

        private static VisualElement BuildWhatIsCollected(Action back)
        {
            VisualElement view = new VisualElement { name = WhatIsCollectedName };
            Label heading = ProtokitePlaytestPanel.Text(WhatIsCollectedHeading, 20, ProtokitePlaytestPanel.TextColour, bold: true);
            heading.style.marginBottom = 14;
            view.Add(heading);
            // Scrolled on a short screen, with Back below it always in sight; with no theme a scroll bar would draw unstyled.
            ScrollView parts = new ScrollView(ScrollViewMode.Vertical);
            parts.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            parts.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            parts.style.flexShrink = 1;
            view.Add(parts);
            foreach (Collected part in WhatIsCollected)
            {
                parts.Add(ProtokitePlaytestPanel.Text(part.Title, 14, ProtokitePlaytestPanel.TextColour, bold: true));
                Label explanation = ProtokitePlaytestPanel.Text(part.Explanation, 12, ProtokitePlaytestPanel.HelpColour);
                explanation.style.marginTop = 2;
                explanation.style.marginBottom = 12;
                parts.Add(explanation);
            }
            Button backButton = ProtokitePlaytestPanel.AnswerButton(BackName, "Back", null, _ => back());
            backButton.style.marginTop = 6;
            backButton.style.marginBottom = 0;
            view.Add(backButton);
            return view;
        }
    }
}
