using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Protokite.Playtest.Tests
{
    /// <summary>The feedback form's inert half: the answer rules, read off Protokite's own validator, what is kept on disk, and the form as drawn.</summary>
    public class ProtokitePlaytestFormTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "protokite_form_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        internal static ProtokitePlaytestFormField Field(string id, string type, bool required = true, params string[] options)
            => new ProtokitePlaytestFormField { Id = id, Type = type, Label = "Question " + id, Required = required, Options = options };

        internal static ProtokitePlaytestForm Form(params ProtokitePlaytestFormField[] fields)
            => new ProtokitePlaytestForm { Id = "form-1", Title = "How was it?", Description = "Tell the studio.", Fields = fields };

        private static List<string> ProblemIds(IReadOnlyList<ProtokitePlaytestFormProblem> problems)
        {
            List<string> ids = new List<string>();
            foreach (ProtokitePlaytestFormProblem problem in problems)
                ids.Add(problem.FieldId);
            return ids;
        }

        // The answer rules

        [Test]
        public void ARequiredCheckboxIsAnsweredUntickedAndSentAsFalse()
        {
            ProtokitePlaytestForm form = Form(Field("agree", ProtokitePlaytestFormFieldTypes.Checkbox));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            Assert.AreEqual(new[] { "agree" }, ProblemIds(answers.FindProblems(form)).ToArray(), "Nothing recorded is not an answer");

            answers.SetChecked("agree", false);
            Assert.IsEmpty(answers.FindProblems(form), "Protokite counts an unticked box as present");
            JObject wire = answers.ToWire(form);
            Assert.AreEqual(JTokenType.Boolean, wire["agree"].Type);
            Assert.IsFalse((bool)wire["agree"], "Sent as false, not left out");
        }

        [Test]
        public void AnEmptyOptionalAnswerIsLeftOutNotSentEmpty()
        {
            ProtokitePlaytestForm form = Form(Field("steps", ProtokitePlaytestFormFieldTypes.TextArea, false),
                Field("score", ProtokitePlaytestFormFieldTypes.Rating, false), Field("kind", ProtokitePlaytestFormFieldTypes.Select, false, "Bug"),
                Field("name", ProtokitePlaytestFormFieldTypes.Text, false));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("steps", "  \n ");
            answers.SetRating("score", 0);
            answers.SetChosenOption("kind", "");
            answers.SetText("name", "");

            Assert.IsEmpty(answers.FindProblems(form));
            Assert.AreEqual(0, answers.ToWire(form).Count, "Protokite stores nothing for them, so nothing is sent");
        }

        [Test]
        public void AQuestionOfAKindThisPackageDoesNotKnowIsAnsweredAsText()
        {
            ProtokitePlaytestForm form = Form(Field("count", "number"));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            Assert.AreEqual(new[] { "count" }, ProblemIds(answers.FindProblems(form)).ToArray(), "Needed, and empty");

            answers.SetText("count", " 42 ");
            Assert.IsEmpty(answers.FindProblems(form));
            JObject wire = answers.ToWire(form);
            Assert.AreEqual(JTokenType.String, wire["count"].Type);
            Assert.AreEqual("42", (string)wire["count"], "Trimmed text, as Protokite keeps it");
        }

        [Test]
        public void ARatingRunsFromOneToFiveAndZeroIsUnanswered()
        {
            ProtokitePlaytestForm form = Form(Field("score", ProtokitePlaytestFormFieldTypes.Rating));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetRating("score", 0);
            Assert.AreEqual("This one is needed.", answers.FindProblems(form)[0].Message);
            answers.SetRating("score", 6);
            StringAssert.Contains("from 1 to 5", answers.FindProblems(form)[0].Message);
            answers.SetRating("score", -1);
            Assert.AreEqual(1, answers.FindProblems(form).Count);
            answers.SetRating("score", 1);
            Assert.IsEmpty(answers.FindProblems(form));
            answers.SetRating("score", 5);
            Assert.IsEmpty(answers.FindProblems(form));
            Assert.AreEqual(JTokenType.Integer, answers.ToWire(form)["score"].Type, "A number, not text");
            Assert.AreEqual(5, (int)answers.ToWire(form)["score"]);
        }

        [Test]
        public void ASelectTakesOnlyItsOwnOptionsLetterForLetterAndTrimmed()
        {
            ProtokitePlaytestForm form = Form(Field("kind", ProtokitePlaytestFormFieldTypes.Select, true, "Bug", "Crash"));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetChosenOption("kind", "bug");
            Assert.AreEqual("Choose one of the options given.", answers.FindProblems(form)[0].Message, "Protokite refuses 'bug' for 'Bug' (measured)");
            answers.SetChosenOption("kind", "Other");
            Assert.AreEqual(1, answers.FindProblems(form).Count);
            answers.SetChosenOption("kind", "  Bug ");
            Assert.IsEmpty(answers.FindProblems(form), "Protokite trims before it compares (measured)");
            Assert.AreEqual("Bug", (string)answers.ToWire(form)["kind"]);
        }

        [Test]
        public void TextIsSentTrimmedAndLetterForLetter()
        {
            ProtokitePlaytestForm form = Form(Field("what", ProtokitePlaytestFormFieldTypes.TextArea));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("what", "  café — \"quoted\"\nsecond line  ");
            Assert.AreEqual("café — \"quoted\"\nsecond line", (string)answers.ToWire(form)["what"]);
        }

        [Test]
        public void AnAnswerToAQuestionTheFormDoesNotAskIsNeitherRefusedNorSent()
        {
            ProtokitePlaytestForm form = Form(Field("what", ProtokitePlaytestFormFieldTypes.Text));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("what", "a");
            answers.SetText("gone", "left over from a form that changed");
            Assert.IsEmpty(answers.FindProblems(form));
            Assert.IsNull(answers.ToWire(form)["gone"]);
        }

        [Test]
        public void QuestionsWhoseIdsDifferOnlyInLetterCaseKeepTheirOwnAnswers()
        {
            ProtokitePlaytestForm form = Form(Field("Q1", ProtokitePlaytestFormFieldTypes.Text), Field("q1", ProtokitePlaytestFormFieldTypes.Text));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("Q1", "upper");
            answers.SetText("q1", "lower");
            JObject wire = answers.ToWire(form);
            Assert.AreEqual("upper", (string)wire["Q1"]);
            Assert.AreEqual("lower", (string)wire["q1"]);
        }

        [Test]
        public void EveryProblemIsReportedInTheStudiosOrder()
        {
            ProtokitePlaytestForm form = Form(Field("second", ProtokitePlaytestFormFieldTypes.Text), Field("fine", ProtokitePlaytestFormFieldTypes.Text),
                Field("first", ProtokitePlaytestFormFieldTypes.Rating));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("fine", "yes");
            Assert.AreEqual(new[] { "second", "first" }, ProblemIds(answers.FindProblems(form)).ToArray(), "Protokite names only the first; the form names all");
        }

        [Test]
        public void TakingAnAnswerBackMakesItUnansweredAgain()
        {
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("what", "a");
            Assert.IsTrue(answers.IsAnswered("what"));
            answers.Remove("what");
            Assert.IsFalse(answers.IsAnswered("what"));
            answers.SetRating("score", 3);
            answers.Clear();
            Assert.AreEqual(0, answers.GetRating("score"));
        }

        // The submission

        [Test]
        public void ABodyWithNoSessionLeavesTheSessionOutAndClaimsNoSteamId()
        {
            ProtokitePlaytestFormSubmission submission = new ProtokitePlaytestFormSubmission { DeviceId = "device-1", Answers = new JObject { ["a"] = "b" } };
            JObject body = submission.ToBody();
            // Protokite stores an empty session_id as a session of its own (measured), so none is sent rather than an empty one.
            Assert.IsNull(body["session_id"]);
            Assert.IsNull(body["steam_id"]);
            Assert.AreEqual("device-1", (string)body["device_id"]);
            Assert.AreEqual("b", (string)body["answers"]["a"]);
            Assert.IsFalse(submission.CanBeSentAgainSafely, "With no session, every send adds a row");

            submission.PlaytestSessionId = "pk-1";
            Assert.AreEqual("pk-1", (string)submission.ToBody()["session_id"]);
            Assert.IsTrue(submission.CanBeSentAgainSafely, "Protokite replaces the answer it holds for the session");
        }

        [Test]
        public void AKeptFormComesBackLetterForLetterAndHoldsNoApiKey()
        {
            ProtokitePlaytestFormSubmission submission = new ProtokitePlaytestFormSubmission
            {
                PlaytestSessionId = "pk-1",
                SteamId = "76561190000000001",
                DeviceId = "",
                ProtokiteApiUrl = "http://protokite.test/",
                FlockGameVersionId = "gv-1",
                // Text that looks like a date must not come back as another spelling of the date.
                Answers = new JObject { ["when"] = "2026-09-29T10:00:00+03:00", ["score"] = 4, ["agree"] = false }
            };
            string saved = submission.ToSavedJson();
            StringAssert.DoesNotContain("test-key", saved);
            StringAssert.DoesNotContain("api_key", saved.Replace("protokite_api_url", ""));

            ProtokitePlaytestFormSubmission back = ProtokitePlaytestFormSubmission.FromSavedJson(saved, out string whyNot);
            Assert.IsNull(whyNot);
            Assert.AreEqual("pk-1", back.PlaytestSessionId);
            Assert.AreEqual("76561190000000001", back.SteamId);
            Assert.AreEqual("http://protokite.test/", back.ProtokiteApiUrl);
            Assert.AreEqual("gv-1", back.FlockGameVersionId);
            Assert.AreEqual(JTokenType.String, back.Answers["when"].Type);
            Assert.AreEqual("2026-09-29T10:00:00+03:00", (string)back.Answers["when"]);
            Assert.AreEqual(4, (int)back.Answers["score"]);
            Assert.IsFalse((bool)back.Answers["agree"]);
        }

        [TestCase("{not json", "not readable")]
        [TestCase("{\"protokite_api_url\":\"http://p.test\",\"answers\":{}}", "names nobody")]
        [TestCase("{\"device_id\":\"d\",\"answers\":{}}", "nowhere to send")]
        [TestCase("{\"device_id\":\"d\",\"protokite_api_url\":\"http://p.test\"}", "holds no answers")]
        [TestCase("{\"device_id\":\"d\",\"protokite_api_url\":\"http://p.test\",\"answers\":[]}", "holds no answers")]
        public void AKeptFormThatCouldNeverBeSentSaysWhy(string saved, string because)
        {
            Assert.IsNull(ProtokitePlaytestFormSubmission.FromSavedJson(saved, out string whyNot));
            StringAssert.Contains(because, whyNot);
        }

        [TestCase("Missing required answer 'steps'", "steps")]
        [TestCase("Rating 'score' must be 1-5", "score")]
        [TestCase("Invalid option for 'kind'", "kind")]
        [TestCase("No published feedback form", "")]
        [TestCase("", "")]
        [TestCase("An unclosed 'quote", "")]
        public void TheRefusedQuestionIsReadOutOfProtokitesMessage(string message, string question)
            => Assert.AreEqual(question, ProtokitePlaytestFormSubmission.FindQuestionInRefusal(message));

        // Kept on disk

        [Test]
        public void KeptFormsAreWholeFilesFoundOldestFirst()
        {
            ProtokitePlaytestKeptForms forms = new ProtokitePlaytestKeptForms(_folder);
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "20200101-000000-000-aaaaaaaa.json"), "{}");
            string kept = forms.Keep(new ProtokitePlaytestFormSubmission { DeviceId = "d", ProtokiteApiUrl = "http://p.test" }, out string error);
            Assert.IsNull(error);
            File.WriteAllText(Path.Combine(_folder, "20200101-000000-000-aaaaaaaa.json.0123.tmp"), "{\"half");

            IReadOnlyList<string> waiting = forms.FindWaiting();
            Assert.AreEqual(2, waiting.Count, "A temporary file is never a form");
            Assert.AreEqual("20200101-000000-000-aaaaaaaa.json", Path.GetFileName(waiting[0]), "Oldest first");
            Assert.AreEqual(kept, waiting[1]);
            Assert.AreEqual("d", ProtokitePlaytestFormSubmission.FromSavedJson(File.ReadAllText(kept), out _).DeviceId);
        }

        [Test]
        public void AKeepSweepsOnlyTemporaryFilesOldEnoughToBeLeftOver()
        {
            Directory.CreateDirectory(_folder);
            string old = Path.Combine(_folder, "a.json.1.tmp");
            string fresh = Path.Combine(_folder, "b.json.2.tmp");
            File.WriteAllText(old, "x");
            File.WriteAllText(fresh, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow - ProtokitePlaytestKeptForms.TemporaryFileAge - TimeSpan.FromSeconds(5));

            new ProtokitePlaytestKeptForms(_folder).Keep(new ProtokitePlaytestFormSubmission { DeviceId = "d", ProtokiteApiUrl = "http://p.test" }, out _);
            Assert.IsFalse(File.Exists(old), "Left by a write that never finished");
            Assert.IsTrue(File.Exists(fresh), "May be another launch's write in progress");
        }

        [Test]
        public void AKeepThatCannotWriteSaysWhereAndLeavesNothing()
        {
            // A file where the folder should be.
            File.WriteAllText(_folder, "in the way");
            try
            {
                string path = new ProtokitePlaytestKeptForms(_folder).Keep(new ProtokitePlaytestFormSubmission { DeviceId = "d" }, out string error);
                Assert.IsNull(path);
                StringAssert.Contains(_folder, error);
            }
            finally
            {
                File.Delete(_folder);
            }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.WindowsEditor)]
        public void AClaimedFormCannotBeClaimedAgainUntilLetGoAndCanBeDeletedWhileHeld()
        {
            // Windows only: elsewhere Mono keeps share modes within the process, which the test would not show.
            ProtokitePlaytestKeptForms forms = new ProtokitePlaytestKeptForms(_folder);
            string path = forms.Keep(new ProtokitePlaytestFormSubmission { DeviceId = "d", ProtokiteApiUrl = "http://p.test" }, out _);
            FileStream claim = ProtokitePlaytestKeptForms.Claim(path);
            Assert.IsNotNull(claim);
            using (FileStream second = ProtokitePlaytestKeptForms.Claim(path))
                Assert.IsNull(second, "Another launch cannot claim it while it is sent");
            StringAssert.Contains("\"device_id\":\"d\"", ProtokitePlaytestKeptForms.ReadClaimed(claim));
            Assert.IsTrue(ProtokitePlaytestKeptForms.ForgetClaimed(claim, path));
            Assert.IsFalse(File.Exists(path));
        }

        // The form as drawn

        private sealed class Calls
        {
            public int Sent;
            public int Closed;
            public bool CanSendRecording;
            public bool RecordingBegins = true;
            public int RecordingAskedFor;
            public string WhyNotSent;
            public ProtokitePlaytestFormAnswers LastSent;
        }

        private static ProtokitePlaytestFormView View(ProtokitePlaytestForm form, Calls calls, ProtokitePlaytestFormAnswers answers = null)
            => new ProtokitePlaytestFormView(form, answers ?? new ProtokitePlaytestFormAnswers(),
                filledIn =>
                {
                    calls.Sent++;
                    calls.LastSent = filledIn;
                    return calls.WhyNotSent;
                },
                () => calls.Closed++,
                () => calls.CanSendRecording,
                () =>
                {
                    calls.RecordingAskedFor++;
                    return calls.RecordingBegins;
                },
                () => false,
                () => { });

        [Test]
        public void EveryQuestionIsDrawnAsItsKindAndAnUnknownKindAsText()
        {
            ProtokitePlaytestForm form = Form(Field("title", ProtokitePlaytestFormFieldTypes.Text), Field("what", ProtokitePlaytestFormFieldTypes.TextArea, false),
                Field("score", ProtokitePlaytestFormFieldTypes.Rating), Field("kind", ProtokitePlaytestFormFieldTypes.Select, true, "Bug", "Crash"),
                Field("agree", ProtokitePlaytestFormFieldTypes.Checkbox), Field("count", "number"));
            ProtokitePlaytestFormView view = View(form, new Calls());

            Assert.IsFalse(view.Root.Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).multiline);
            Assert.IsTrue(view.Root.Q<TextField>(ProtokitePlaytestFormView.AnswerName("what")).multiline);
            Assert.IsNotNull(view.Root.Q<TextField>(ProtokitePlaytestFormView.AnswerName("count")), "A kind this package does not know is a text box");
            for (int score = 0; score < 5; score++)
                Assert.AreEqual((score + 1).ToString(), view.Root.Q<Button>(ProtokitePlaytestFormView.ChoiceName("score", score)).text);
            Assert.AreEqual("Crash", view.Root.Q<Button>(ProtokitePlaytestFormView.ChoiceName("kind", 1)).text);
            Assert.IsNotNull(view.Root.Q<Button>(ProtokitePlaytestFormView.AnswerName("agree")));
            // The labels, from the config, with the needed ones marked.
            List<string> labels = view.Root.Query<Label>().ToList().ConvertAll(label => label.text);
            CollectionAssert.Contains(labels, "Question title *");
            CollectionAssert.Contains(labels, "Question what");
            CollectionAssert.Contains(labels, "How was it?");
            CollectionAssert.Contains(labels, "Tell the studio.");
        }

        [Test]
        public void ARequiredCheckboxLeftAloneIsNoProblemBecauseItIsRecordedUnticked()
        {
            Calls calls = new Calls();
            ProtokitePlaytestFormView view = View(Form(Field("agree", ProtokitePlaytestFormFieldTypes.Checkbox)), calls);
            Assert.IsEmpty(view.TrySendForTesting());
            Assert.AreEqual(1, calls.Sent);
            Assert.IsFalse((bool)calls.LastSent.ToWire(Form(Field("agree", ProtokitePlaytestFormFieldTypes.Checkbox)))["agree"]);
        }

        [Test]
        public void ProblemsShowOnlyOnceSendingIsTriedAndNothingIsSentUntilThereAreNone()
        {
            Calls calls = new Calls();
            ProtokitePlaytestForm form = Form(Field("title", ProtokitePlaytestFormFieldTypes.Text), Field("score", ProtokitePlaytestFormFieldTypes.Rating));
            ProtokitePlaytestFormView view = View(form, calls);
            Label titleProblem = view.Root.Q<Label>(ProtokitePlaytestFormView.ProblemName("title"));
            Assert.AreEqual(DisplayStyle.None, titleProblem.style.display.value, "Nothing complains while the player is still filling it in");

            Assert.AreEqual(2, view.TrySendForTesting().Count);
            Assert.AreEqual(0, calls.Sent);
            Assert.AreEqual(DisplayStyle.Flex, titleProblem.style.display.value);
            Assert.AreEqual("This one is needed.", titleProblem.text);
            Assert.AreEqual(DisplayStyle.Flex, view.Root.Q<Label>(ProtokitePlaytestFormView.NotSentName).style.display.value);

            // Typing reaching the answers needs a panel to carry the change: a Play Mode test types into the drawn form.
            view.Answers.SetText("title", "It crashed");
            view.Answers.SetRating("score", 2);
            Assert.IsEmpty(view.TrySendForTesting());
            Assert.AreEqual(1, calls.Sent);
            Assert.AreEqual(DisplayStyle.None, titleProblem.style.display.value, "A fixed question stops complaining");
            Assert.AreEqual("It crashed", calls.LastSent.GetText("title"));
        }

        [Test]
        public void AFormThatCouldNotBeSentSaysWhyAndStaysOpen()
        {
            Calls calls = new Calls { WhyNotSent = "the disk is full" };
            ProtokitePlaytestFormView view = View(Form(Field("what", ProtokitePlaytestFormFieldTypes.Text, false)), calls);
            view.TrySendForTesting();
            Label notSent = view.Root.Q<Label>(ProtokitePlaytestFormView.NotSentName);
            StringAssert.Contains("the disk is full", notSent.text);
            Assert.AreEqual(0, calls.Closed);
        }

        [Test]
        public void PickingTheChosenRatingOrOptionAgainTakesItBack()
        {
            ProtokitePlaytestForm form = Form(Field("score", ProtokitePlaytestFormFieldTypes.Rating, false), Field("kind", ProtokitePlaytestFormFieldTypes.Select, false, "Bug", "Crash"));
            ProtokitePlaytestFormView view = View(form, new Calls());
            view.PressForTesting(ProtokitePlaytestFormView.ChoiceName("score", 3));
            Assert.AreEqual(4, view.Answers.GetRating("score"));
            view.PressForTesting(ProtokitePlaytestFormView.ChoiceName("score", 1));
            Assert.AreEqual(2, view.Answers.GetRating("score"), "Another score replaces it");
            view.PressForTesting(ProtokitePlaytestFormView.ChoiceName("score", 1));
            Assert.AreEqual(0, view.Answers.GetRating("score"));
            view.PressForTesting(ProtokitePlaytestFormView.ChoiceName("kind", 1));
            Assert.AreEqual("Crash", view.Answers.GetText("kind"));
            view.PressForTesting(ProtokitePlaytestFormView.ChoiceName("kind", 1));
            Assert.AreEqual(0, view.Answers.ToWire(form).Count);
        }

        [Test]
        public void TheCheckboxTicksAndUnticks()
        {
            ProtokitePlaytestFormView view = View(Form(Field("agree", ProtokitePlaytestFormFieldTypes.Checkbox)), new Calls());
            view.PressForTesting(ProtokitePlaytestFormView.AnswerName("agree"));
            Assert.IsTrue(view.Answers.IsChecked("agree"));
            view.PressForTesting(ProtokitePlaytestFormView.AnswerName("agree"));
            Assert.IsFalse(view.Answers.IsChecked("agree"));
        }

        [Test]
        public void SendAndCloseAreTheFormsOwnButtons()
        {
            Calls calls = new Calls();
            ProtokitePlaytestFormView view = View(Form(Field("what", ProtokitePlaytestFormFieldTypes.Text, false)), calls);
            view.PressForTesting(ProtokitePlaytestFormView.SendButtonName);
            Assert.AreEqual(1, calls.Sent);
            view.PressForTesting(ProtokitePlaytestFormView.CloseButtonName);
            Assert.AreEqual(1, calls.Closed);
        }

        [Test]
        public void AnswersGivenBeforeAreDrawnWhenTheFormOpensAgain()
        {
            ProtokitePlaytestForm form = Form(Field("title", ProtokitePlaytestFormFieldTypes.Text));
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            answers.SetText("title", "typed before");
            Assert.AreEqual("typed before", View(form, new Calls(), answers).Root.Q<TextField>(ProtokitePlaytestFormView.AnswerName("title")).value);
        }

        [Test]
        public void TheRecordingIsOfferedOnlyWhenItHasSomewhereToGoAndOnItsWayOnlyWhenSendingBegan()
        {
            Calls calls = new Calls { CanSendRecording = false };
            ProtokitePlaytestFormView view = View(Form(), calls);
            VisualElement offer = view.Root.Q<Button>(ProtokitePlaytestFormView.SendRecordingButtonName).parent;
            Label note = view.Root.Q<Label>(ProtokitePlaytestFormView.RecordingNoteName);
            Assert.AreEqual(DisplayStyle.None, offer.style.display.value, "A recording with no session cannot be sent: no button that would stop it and send nothing");
            Assert.AreEqual(DisplayStyle.None, note.style.display.value);

            calls.CanSendRecording = true;
            view = View(Form(), calls);
            offer = view.Root.Q<Button>(ProtokitePlaytestFormView.SendRecordingButtonName).parent;
            note = view.Root.Q<Label>(ProtokitePlaytestFormView.RecordingNoteName);
            Assert.AreEqual(DisplayStyle.Flex, offer.style.display.value);
            calls.RecordingBegins = false;
            SendTheRecording(view);
            Assert.AreEqual(1, calls.RecordingAskedFor);
            Assert.AreEqual(DisplayStyle.None, note.style.display.value, "Nothing claims a video is on its way when none is");
            Assert.AreEqual(DisplayStyle.None, offer.style.display.value, "Not offered twice");

            view = View(Form(), new Calls { CanSendRecording = true });
            SendTheRecording(view);
            Assert.AreEqual(DisplayStyle.Flex, view.Root.Q<Label>(ProtokitePlaytestFormView.RecordingNoteName).style.display.value);
            Assert.AreEqual(ProtokitePlaytestFormView.RecordingOnItsWay, view.Root.Q<Label>(ProtokitePlaytestFormView.RecordingNoteName).text);
        }

        private static void SendTheRecording(ProtokitePlaytestFormView view) => view.PressForTesting(ProtokitePlaytestFormView.SendRecordingButtonName);
    }
}
