using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Protokite.Playtest
{
    /// <summary>
    /// The feedback form as the player sees it, built entirely from the published form: every question, label, help text and option
    /// comes from Protokite, so a studio editing its form needs no new build. A question of a kind this package does not know is a
    /// text box, as Protokite reads it. Problems are shown only once sending is tried: a form complaining while someone types reads
    /// as broken.
    /// </summary>
    internal sealed class ProtokitePlaytestFormView
    {
        internal const string SendButtonName = "protokite-form-send";
        internal const string CloseButtonName = "protokite-form-close";
        internal const string SendRecordingButtonName = "protokite-form-send-recording";
        internal const string RecordingNoteName = "protokite-form-recording-note";
        internal const string NotSentName = "protokite-form-not-sent";

        internal const string SendRecordingText = "Upload your recording";
        internal const string SendRecordingExplanation = "Stops recording for the rest of this session and sends what was recorded.";
        internal const string RecordingOnItsWay = "Your video is on its way. Recording has stopped for the rest of this session.";

        private readonly ProtokitePlaytestForm _form;
        private readonly Func<ProtokitePlaytestFormAnswers, string> _send;
        private readonly Action _close;
        private readonly Func<bool> _canSendRecording;
        private readonly Func<bool> _sendRecording;
        private readonly Func<bool> _theGameKeepsTheCursorLocked;
        private readonly Action _pressIgnoredForALockedCursor;
        private readonly double _shownAt;
        private readonly Dictionary<string, Label> _problemLabels = new Dictionary<string, Label>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action> _pressed = new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly Label _notSent;
        private readonly VisualElement _recordingRow;
        private readonly Label _recordingNote;
        private readonly ScrollView _questions;
        private bool _askedForTheRecording;

        /// <summary>
        /// The view over <paramref name="answers"/>. <paramref name="send"/> hears answers Protokite would take and says why they could
        /// not be sent, or null. While <paramref name="theGameKeepsTheCursorLocked"/> says so, a mouse press on Send, Close or the recording
        /// button is ignored and <paramref name="pressIgnoredForALockedCursor"/> told: it lands at the centre, not where the player aimed.
        /// </summary>
        internal ProtokitePlaytestFormView(ProtokitePlaytestForm form, ProtokitePlaytestFormAnswers answers, Func<ProtokitePlaytestFormAnswers, string> send,
            Action close, Func<bool> canSendRecording, Func<bool> sendRecording, Func<bool> theGameKeepsTheCursorLocked, Action pressIgnoredForALockedCursor)
        {
            _form = form;
            Answers = answers;
            _send = send;
            _close = close;
            _canSendRecording = canSendRecording;
            _sendRecording = sendRecording;
            _theGameKeepsTheCursorLocked = theGameKeepsTheCursorLocked;
            _pressIgnoredForALockedCursor = pressIgnoredForALockedCursor;
            _shownAt = Time.realtimeSinceStartupAsDouble;

            Root = new VisualElement { name = "protokite-form" };
            Root.style.flexShrink = 1;
            Root.style.flexDirection = FlexDirection.Column;
            // Escape in a text field puts back what it held when it was focused (measured): here it is kept from the field.
            Root.RegisterCallback<KeyDownEvent>(KeepEscapeFromTheTextField, TrickleDown.TrickleDown);

            Root.Add(ProtokitePlaytestPanel.Text(string.IsNullOrWhiteSpace(form.Title) ? "Feedback" : form.Title, 22, ProtokitePlaytestPanel.TextColour, bold: true));
            if (!string.IsNullOrWhiteSpace(form.Description))
            {
                Label description = ProtokitePlaytestPanel.Text(form.Description, 13, ProtokitePlaytestPanel.HelpColour);
                description.style.marginTop = 6;
                Root.Add(description);
            }

            // Scrolled by the wheel and to whichever question takes focus; with no theme a scroll bar would draw unstyled.
            _questions = new ScrollView(ScrollViewMode.Vertical) { name = "protokite-form-questions" };
            _questions.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _questions.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _questions.style.flexShrink = 1;
            _questions.style.marginTop = 14;
            _questions.RegisterCallback<FocusInEvent>(e =>
            {
                if (e.target is VisualElement focused)
                    _questions.ScrollTo(focused);
            });
            Root.Add(_questions);
            foreach (ProtokitePlaytestFormField field in form.Fields)
                _questions.Add(BuildQuestion(field));

            _notSent = ProtokitePlaytestPanel.Text("", 13, ProtokitePlaytestPanel.ProblemColour);
            _notSent.name = NotSentName;
            _notSent.style.marginTop = 8;
            _notSent.style.display = DisplayStyle.None;
            Root.Add(_notSent);

            _recordingRow = new VisualElement();
            _recordingRow.style.marginTop = 10;
            Button sendRecordingButton = ActionButton(SendRecordingButtonName, SendRecordingText, SendTheRecording);
            sendRecordingButton.tooltip = SendRecordingExplanation;
            _recordingRow.Add(sendRecordingButton);
            Label explanation = ProtokitePlaytestPanel.Text(SendRecordingExplanation, 12, ProtokitePlaytestPanel.HelpColour);
            explanation.style.marginTop = 3;
            _recordingRow.Add(explanation);
            Root.Add(_recordingRow);
            _recordingNote = ProtokitePlaytestPanel.Text(RecordingOnItsWay, 12, ProtokitePlaytestPanel.HelpColour);
            _recordingNote.name = RecordingNoteName;
            _recordingNote.style.marginTop = 10;
            Root.Add(_recordingNote);
            UpdateRecordingRow();
            // A recording can reach a limit while the form is open, so the offer follows it.
            Root.schedule.Execute(UpdateRecordingRow).Every(500);

            VisualElement buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.FlexEnd;
            buttons.style.marginTop = 14;
            buttons.Add(ActionButton(CloseButtonName, "Close", () => _close()));
            Button sendButton = ActionButton(SendButtonName, "Send", () => TrySend());
            sendButton.style.marginLeft = 10;
            buttons.Add(sendButton);
            Root.Add(buttons);
        }

        /// <summary>The whole form, for the panel to show.</summary>
        internal VisualElement Root { get; }

        /// <summary>What has been filled in so far.</summary>
        internal ProtokitePlaytestFormAnswers Answers { get; }

        /// <summary>The name of the element answering one question, for tests and probes.</summary>
        internal static string AnswerName(string fieldId) => "protokite-form-answer-" + fieldId;

        /// <summary>The name of the label showing one question's problem.</summary>
        internal static string ProblemName(string fieldId) => "protokite-form-problem-" + fieldId;

        /// <summary>The name of one choice under a question: a rating's score or a select's option, counted from 0.</summary>
        internal static string ChoiceName(string fieldId, int index) => "protokite-form-choice-" + fieldId + "-" + index;

        /// <summary>Checks the answers and hands them over when Protokite would take them; the problems shown, for tests.</summary>
        internal IReadOnlyList<ProtokitePlaytestFormProblem> TrySendForTesting() => TrySend();

        /// <summary>What pressing the named button does, past the moment and cursor rules, for tests of a view in no panel, where no event is dispatched.</summary>
        internal void PressForTesting(string buttonName) => _pressed[buttonName]();

        private IReadOnlyList<ProtokitePlaytestFormProblem> TrySend()
        {
            IReadOnlyList<ProtokitePlaytestFormProblem> problems = Answers.FindProblems(_form);
            HashSet<string> withProblems = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProtokitePlaytestFormProblem problem in problems)
            {
                withProblems.Add(problem.FieldId);
                if (_problemLabels.TryGetValue(problem.FieldId, out Label label))
                {
                    label.text = problem.Message;
                    label.style.display = DisplayStyle.Flex;
                }
            }
            foreach (KeyValuePair<string, Label> label in _problemLabels)
            {
                if (!withProblems.Contains(label.Key))
                    label.Value.style.display = DisplayStyle.None;
            }
            if (problems.Count > 0)
            {
                ShowNotSent("Some answers need a look: see the notes above, then send again.");
                return problems;
            }
            string whyNot = _send(Answers);
            if (whyNot != null)
                ShowNotSent("Your report could not be sent: " + whyNot);
            return problems;
        }

        private void ShowNotSent(string text)
        {
            _notSent.text = text;
            _notSent.style.display = DisplayStyle.Flex;
        }

        private void SendTheRecording()
        {
            // Remembered here: one recording a launch, so asking twice could only send the same video again. The note only when sending began.
            _askedForTheRecording = true;
            bool began = _sendRecording();
            _recordingNote.style.display = began ? DisplayStyle.Flex : DisplayStyle.None;
            _recordingRow.style.display = DisplayStyle.None;
        }

        private void UpdateRecordingRow()
        {
            _recordingRow.style.display = !_askedForTheRecording && _canSendRecording() ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_askedForTheRecording)
                _recordingNote.style.display = DisplayStyle.None;
        }

        private void KeepEscapeFromTheTextField(KeyDownEvent press)
        {
            if (press.keyCode != KeyCode.Escape)
                return;
            Focusable focused = Root.panel?.focusController?.focusedElement;
            if (!(focused is VisualElement element) || (!(element is TextField) && element.GetFirstAncestorOfType<TextField>() == null))
                return;
            // Stopped only: blurring the field here, mid-dispatch, left the next click lost or handled after the keys that followed it (measured).
            press.StopImmediatePropagation();
        }

        private VisualElement BuildQuestion(ProtokitePlaytestFormField field)
        {
            VisualElement question = new VisualElement { name = "protokite-form-question-" + field.Id };
            question.style.marginBottom = 14;

            Label label = ProtokitePlaytestPanel.Text(field.Label + (field.Required ? " *" : ""), 14, ProtokitePlaytestPanel.TextColour, bold: true);
            question.Add(label);
            if (!string.IsNullOrWhiteSpace(field.HelpText))
            {
                Label help = ProtokitePlaytestPanel.Text(field.HelpText, 12, ProtokitePlaytestPanel.HelpColour);
                help.style.marginTop = 2;
                question.Add(help);
            }

            VisualElement answer = BuildAnswer(field);
            answer.style.marginTop = 6;
            question.Add(answer);

            Label problem = ProtokitePlaytestPanel.Text("", 12, ProtokitePlaytestPanel.ProblemColour);
            problem.name = ProblemName(field.Id);
            problem.style.marginTop = 3;
            problem.style.display = DisplayStyle.None;
            question.Add(problem);
            _problemLabels[field.Id] = problem;
            return question;
        }

        private VisualElement BuildAnswer(ProtokitePlaytestFormField field)
        {
            if (ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Rating))
                return BuildChoices(field, new[] { "1", "2", "3", "4", "5" }, index => Answers.GetRating(field.Id) == index + 1,
                    index => Answers.SetRating(field.Id, Answers.GetRating(field.Id) == index + 1 ? 0 : index + 1));
            if (ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Select))
            {
                IReadOnlyList<string> options = field.Options;
                string[] titles = new string[options.Count];
                for (int i = 0; i < options.Count; i++)
                    titles[i] = options[i];
                // Picking the chosen one again takes it back, so an optional question can be left unanswered after all.
                return BuildChoices(field, titles, index => string.Equals(Answers.GetText(field.Id), options[index], StringComparison.Ordinal),
                    index => Answers.SetChosenOption(field.Id, string.Equals(Answers.GetText(field.Id), options[index], StringComparison.Ordinal) ? "" : options[index]));
            }
            if (ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Checkbox))
                return BuildCheckbox(field);
            // Text, many-line text, and every kind this package does not know, which Protokite reads as text.
            return BuildText(field, ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.TextArea));
        }

        private VisualElement BuildText(ProtokitePlaytestFormField field, bool manyLines)
        {
            TextField text = new TextField { name = AnswerName(field.Id), multiline = manyLines };
#if UNITY_2022_1_OR_NEWER
            // A click puts the caret where it lands rather than selecting everything: a release handled after the first keys (measured,
            // IL2CPP) selected what they typed for the next key to replace, and an answer brought back unsent would go on one key press.
            text.selectAllOnFocus = false;
            text.selectAllOnMouseUp = false;
#endif
            text.SetValueWithoutNotify(Answers.GetText(field.Id));
            text.style.marginLeft = 0;
            text.style.marginRight = 0;
            text.style.height = manyLines ? 84 : 32;
            // With no theme the box a player types into has no size or look, and a click would miss it (measured): given both here.
            VisualElement input = text.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.flexGrow = 1;
                input.style.backgroundColor = ProtokitePlaytestPanel.OptionColour;
                input.style.color = ProtokitePlaytestPanel.TextColour;
                input.style.paddingLeft = 6;
                input.style.paddingRight = 6;
                input.style.paddingTop = 4;
                input.style.paddingBottom = 4;
                input.style.whiteSpace = manyLines ? WhiteSpace.Normal : WhiteSpace.NoWrap;
                ProtokitePlaytestPanel.SetBorder(input.style, 1, ProtokitePlaytestPanel.OptionBorderColour);
                ProtokitePlaytestPanel.SetRadius(input.style, 4);
                text.RegisterCallback<FocusInEvent>(_ => ProtokitePlaytestPanel.SetBorder(input.style, 1, ProtokitePlaytestPanel.OptionFocusBorderColour));
                text.RegisterCallback<FocusOutEvent>(_ => ProtokitePlaytestPanel.SetBorder(input.style, 1, ProtokitePlaytestPanel.OptionBorderColour));
            }
            text.RegisterValueChangedCallback(change => Answers.SetText(field.Id, change.newValue));
            return text;
        }

        private VisualElement BuildChoices(ProtokitePlaytestFormField field, string[] titles, Func<int, bool> isChosen, Action<int> choose)
        {
            VisualElement row = new VisualElement { name = AnswerName(field.Id) };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            List<Button> buttons = new List<Button>();
            Action redraw = () =>
            {
                for (int i = 0; i < buttons.Count; i++)
                    buttons[i].style.backgroundColor = isChosen(i) ? ProtokitePlaytestPanel.OptionChosenColour : ProtokitePlaytestPanel.OptionColour;
            };
            for (int i = 0; i < titles.Length; i++)
            {
                int index = i;
                Button choice = new Button { name = ChoiceName(field.Id, i), text = titles[i] };
                Action pressed = () =>
                {
                    choose(index);
                    redraw();
                };
                choice.clicked += pressed;
                _pressed[choice.name] = pressed;
                StyleButton(choice);
                choice.style.marginRight = 6;
                choice.style.marginBottom = 6;
                buttons.Add(choice);
                row.Add(choice);
            }
            redraw();
            return row;
        }

        private VisualElement BuildCheckbox(ProtokitePlaytestFormField field)
        {
            // Recorded unticked straight away: Protokite counts a checkbox as answered either way, so a required one is satisfied unticked.
            if (!Answers.IsAnswered(field.Id))
                Answers.SetChecked(field.Id, false);
            Button box = new Button { name = AnswerName(field.Id), text = "" };
            StyleButton(box);
            box.style.width = 26;
            box.style.height = 26;
            Action redraw = () => box.style.backgroundColor = Answers.IsChecked(field.Id) ? ProtokitePlaytestPanel.OptionChosenColour : ProtokitePlaytestPanel.OptionColour;
            Action pressed = () =>
            {
                Answers.SetChecked(field.Id, !Answers.IsChecked(field.Id));
                redraw();
            };
            box.clicked += pressed;
            _pressed[box.name] = pressed;
            redraw();
            return box;
        }

        // Send, Close and the recording: a press in the first half second, or one under a cursor the game keeps locking, is ignored.
        private Button ActionButton(string name, string text, Action pressed)
        {
            Button button = new Button { name = name, text = text };
            button.clickable.clickedWithEventInfo += press =>
            {
                if (Time.realtimeSinceStartupAsDouble - _shownAt < ProtokitePlaytestPanel.SecondsBeforeAnAnswerCounts)
                    return;
                if ((press is IPointerEvent || press is IMouseEvent) && _theGameKeepsTheCursorLocked())
                {
                    _pressIgnoredForALockedCursor();
                    return;
                }
                pressed();
            };
            _pressed[name] = pressed;
            StyleButton(button);
            button.style.paddingLeft = 16;
            button.style.paddingRight = 16;
            return button;
        }

        // No theme gives a button a look, hover or focus, so each gives itself one.
        private static void StyleButton(Button button)
        {
            button.style.marginLeft = 0;
            button.style.marginTop = 0;
            button.style.paddingTop = 6;
            button.style.paddingBottom = 6;
            button.style.paddingLeft = 10;
            button.style.paddingRight = 10;
            button.style.color = ProtokitePlaytestPanel.TextColour;
            button.style.fontSize = 13;
            button.style.backgroundColor = ProtokitePlaytestPanel.OptionColour;
            ProtokitePlaytestPanel.SetBorder(button.style, 1, ProtokitePlaytestPanel.OptionBorderColour);
            ProtokitePlaytestPanel.SetRadius(button.style, 5);
            button.RegisterCallback<FocusInEvent>(_ => ProtokitePlaytestPanel.SetBorder(button.style, 2, ProtokitePlaytestPanel.OptionFocusBorderColour));
            button.RegisterCallback<FocusOutEvent>(_ => ProtokitePlaytestPanel.SetBorder(button.style, 1, ProtokitePlaytestPanel.OptionBorderColour));
        }
    }
}
