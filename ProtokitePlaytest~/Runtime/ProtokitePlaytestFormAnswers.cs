using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Protokite.Playtest
{
    /// <summary>The scores a rating question takes.</summary>
    public static class ProtokitePlaytestRatings
    {
        /// <summary>The lowest score.</summary>
        public const int Lowest = 1;
        /// <summary>The highest score.</summary>
        public const int Highest = 5;
    }

    /// <summary>Something wrong with one answer, named so a form can show it against its question.</summary>
    public sealed class ProtokitePlaytestFormProblem
    {
        /// <summary>The question's id.</summary>
        public string FieldId { get; }

        /// <summary>What to tell the player, in their own terms.</summary>
        public string Message { get; }

        internal ProtokitePlaytestFormProblem(string fieldId, string message)
        {
            FieldId = fieldId;
            Message = message;
        }
    }

    /// <summary>What a player filled in on a feedback form, by Protokite's own rules, for <see cref="ProtokitePlaytest.SendFeedbackForm"/>; a game drawing its own form fills one in.</summary>
    // Protokite's rules: a required checkbox is answered unticked, an empty optional answer is left out, and an unknown kind is answered as text.
    public sealed class ProtokitePlaytestFormAnswers
    {
        private sealed class Answer
        {
            public string Text = "";
            public int Rating;
            public bool Checked;
        }

        // Letter for letter, as Protokite keeps question ids apart.
        private readonly Dictionary<string, Answer> _answers = new Dictionary<string, Answer>(StringComparer.Ordinal);

        /// <summary>Records a text, many-line text or unknown-kind answer.</summary>
        public void SetText(string fieldId, string text) => Record(fieldId).Text = text ?? "";

        /// <summary>Records a rating; 0 takes it back, which reads as unanswered.</summary>
        public void SetRating(string fieldId, int rating) => Record(fieldId).Rating = rating;

        /// <summary>Records a checkbox, ticked or not. Unticked is an answer, not the lack of one.</summary>
        public void SetChecked(string fieldId, bool isChecked) => Record(fieldId).Checked = isChecked;

        /// <summary>Records the option picked for a select question; null or empty takes it back.</summary>
        public void SetChosenOption(string fieldId, string option) => Record(fieldId).Text = option ?? "";

        /// <summary>Forgets the answer to one question.</summary>
        public void Remove(string fieldId)
        {
            if (fieldId != null)
                _answers.Remove(fieldId);
        }

        /// <summary>Forgets every answer.</summary>
        public void Clear() => _answers.Clear();

        /// <summary>The text or chosen option recorded for a question, or empty.</summary>
        public string GetText(string fieldId) => Find(fieldId)?.Text ?? "";

        /// <summary>The rating recorded for a question, or 0.</summary>
        public int GetRating(string fieldId) => Find(fieldId)?.Rating ?? 0;

        /// <summary>Whether a question's checkbox was recorded ticked.</summary>
        public bool IsChecked(string fieldId) => Find(fieldId)?.Checked ?? false;

        /// <summary>Whether anything was recorded for this question, which is not the same as it holding something.</summary>
        public bool IsAnswered(string fieldId) => Find(fieldId) != null;

        /// <summary>Every question Protokite would turn this form away over, all at once and in the studio's order; empty means it would be taken.</summary>
        public IReadOnlyList<ProtokitePlaytestFormProblem> FindProblems(ProtokitePlaytestForm form)
        {
            List<ProtokitePlaytestFormProblem> problems = new List<ProtokitePlaytestFormProblem>();
            if (form == null)
                return problems;
            // The form's own questions: an answer to one it no longer asks is dropped by Protokite, never refused.
            foreach (ProtokitePlaytestFormField field in form.Fields)
            {
                Answer answer = Find(field.Id);
                bool empty = IsEmpty(field, answer);
                if (empty)
                {
                    if (field.Required)
                        problems.Add(new ProtokitePlaytestFormProblem(field.Id, "This one is needed."));
                    continue;
                }
                if (IsKind(field, ProtokitePlaytestFormFieldTypes.Rating))
                {
                    if (answer.Rating < ProtokitePlaytestRatings.Lowest || answer.Rating > ProtokitePlaytestRatings.Highest)
                        problems.Add(new ProtokitePlaytestFormProblem(field.Id, $"Choose a rating from {ProtokitePlaytestRatings.Lowest} to {ProtokitePlaytestRatings.Highest}."));
                }
                else if (IsKind(field, ProtokitePlaytestFormFieldTypes.Select))
                {
                    // Trimmed and letter for letter, as Protokite compares it.
                    if (!Contains(field.Options, answer.Text.Trim()))
                        problems.Add(new ProtokitePlaytestFormProblem(field.Id, "Choose one of the options given."));
                }
            }
            return problems;
        }

        /// <summary>
        /// The answers as Protokite keeps them: a rating as a number, a checkbox as true or false, everything else as trimmed text.
        /// An empty answer and an answer to a question the form does not ask are left out, as Protokite leaves them out.
        /// </summary>
        internal JObject ToWire(ProtokitePlaytestForm form)
        {
            JObject wire = new JObject();
            if (form == null)
                return wire;
            foreach (ProtokitePlaytestFormField field in form.Fields)
            {
                Answer answer = Find(field.Id);
                if (IsEmpty(field, answer))
                    continue;
                if (IsKind(field, ProtokitePlaytestFormFieldTypes.Rating))
                    wire[field.Id] = answer.Rating;
                else if (IsKind(field, ProtokitePlaytestFormFieldTypes.Checkbox))
                    wire[field.Id] = answer.Checked;
                else
                    wire[field.Id] = answer.Text.Trim();
            }
            return wire;
        }

        // Protokite's own emptiness test, read as the question's kind: a recorded checkbox is never empty, an unchosen rating always is.
        private static bool IsEmpty(ProtokitePlaytestFormField field, Answer answer)
        {
            if (answer == null)
                return true;
            if (IsKind(field, ProtokitePlaytestFormFieldTypes.Checkbox))
                return false;
            if (IsKind(field, ProtokitePlaytestFormFieldTypes.Rating))
                return answer.Rating == 0;
            // Text, many-line text, select, and every kind this package does not know, which Protokite reads as text.
            return string.IsNullOrWhiteSpace(answer.Text);
        }

        internal static bool IsKind(ProtokitePlaytestFormField field, string kind) => string.Equals(field.Type, kind, StringComparison.Ordinal);

        private static bool Contains(IReadOnlyList<string> options, string chosen)
        {
            foreach (string option in options)
            {
                if (string.Equals(option, chosen, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private Answer Find(string fieldId) => fieldId != null && _answers.TryGetValue(fieldId, out Answer answer) ? answer : null;

        private Answer Record(string fieldId)
        {
            if (fieldId == null)
                throw new ArgumentNullException(nameof(fieldId));
            if (!_answers.TryGetValue(fieldId, out Answer answer))
            {
                answer = new Answer();
                _answers[fieldId] = answer;
            }
            return answer;
        }
    }
}
