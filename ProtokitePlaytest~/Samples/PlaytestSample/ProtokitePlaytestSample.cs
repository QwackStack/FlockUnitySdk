using System;
using System.Collections.Generic;
using Flock;
using Flock.Exceptions;
using UnityEngine;

namespace Protokite.Playtest.Samples
{
    /// <summary>Every call a game can make to the playtest, on one screen: add it to a GameObject and press Play.</summary>
    public class ProtokitePlaytestSample : MonoBehaviour
    {
        private const string SampleEventName = "sample_button_pressed";
        private const double TestVideoSeconds = 5.0;

        private string _said = "";
        private string _steamId = "";
        private bool _signingIn;
        private bool _playtestPanelOpen;
        private Vector2 _scroll;

        private void OnGUI()
        {
            // Read at Layout only, so an event's layout and what it draws agree.
            if (Event.current.type == EventType.Layout)
                _playtestPanelOpen = ProtokitePlaytest.IsFeedbackFormOpen || ProtokitePlaytest.IsConsentQuestionOpen;
            // The playtest's own form and question take the screen while open, so the sample steps aside.
            if (_playtestPanelOpen)
            {
                DrawWhileAPlaytestPanelIsOpen();
                return;
            }

            GUILayout.BeginArea(new Rect(16f, 16f, 480f, Screen.height - 32f), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("Protokite Playtest - Sample");
            DrawSignIn();
            DrawStatus();
            DrawConsent();
            DrawSteamId();
            DrawPlaytestEvent();
            DrawRecording();
            DrawFeedbackForm();
            DrawTestVideo();
            GUILayout.Space(8f);
            GUILayout.Label(_said);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawWhileAPlaytestPanelIsOpen()
        {
            GUILayout.BeginArea(new Rect(16f, 16f, 260f, 36f), GUI.skin.box);
            if (!ProtokitePlaytest.IsFeedbackFormOpen)
                GUILayout.Label("The consent question is on screen.");
            else if (GUILayout.Button("Close The Form From Code"))
                _said = ProtokitePlaytest.CloseFeedbackForm() ? "The form is closed; what was filled in comes back when it opens again." : "No form was open.";
            GUILayout.EndArea();
        }

        // The playtest never signs a player in: its session starts once the game's own sign-in has.
        private void DrawSignIn()
        {
            if (!FlockClient.IsInitialized)
            {
                GUILayout.Label("The Flock SDK is not running. Turn on Auto-Initialize On Load in Flock > Settings, or add a FlockBootstrap to the scene, then press Play.");
                return;
            }
            if (FlockClient.Instance.IsAuthenticated)
            {
                GUILayout.Label("Signed in as player " + FlockClient.Instance.CurrentPlayerId + ".");
                return;
            }
            GUI.enabled = !_signingIn;
            if (GUILayout.Button("Sign In With This Device"))
                SignInAsync();
            GUI.enabled = true;
        }

        private async void SignInAsync()
        {
            _signingIn = true;
            string deviceId = SystemInfo.deviceUniqueIdentifier;
            try
            {
                try
                {
                    await FlockClient.Instance.Authentication.LoginWithDeviceAsync(deviceId);
                }
                // A device Flock has never seen is not a player yet, so it is registered, with no name: Flock keeps names unique.
                catch (FlockException ex) when (ex.ErrorCode == FlockErrorCode.PlayerInvalidLoginCredentials)
                {
                    await FlockClient.Instance.Authentication.RegisterWithDeviceAsync(deviceId);
                }
                _said = "Signed in. The playtest's session starts once its config is loaded.";
            }
            catch (Exception ex)
            {
                _said = "Sign-in failed: " + ex.Message;
            }
            finally
            {
                _signingIn = false;
            }
        }

        private static void DrawStatus()
        {
            ProtokitePlaytestStatus status = ProtokitePlaytest.Status;
            GUILayout.Label("Status: " + status + ". " + ProtokitePlaytest.Describe(status));
            GUILayout.Label("Playtest: " + (ProtokitePlaytest.Config?.TestId ?? "not loaded") + ". Session: " + (ProtokitePlaytest.PlaytestSessionId ?? "not started") + ".");
            GUILayout.Label("Video: " + ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording)
                + ", play data: " + ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics)
                + ", exceptions: " + ProtokitePlaytest.IsFeatureEnabled(ProtokitePlaytestFeatures.ExceptionCapturing) + ".");
        }

        // A game asking in its own menu answers with SetPlaytestConsent; the playtest's own question is asked for it otherwise.
        private void DrawConsent()
        {
            GUILayout.Label("Consent in force: " + ProtokitePlaytest.PlaytestConsent + ". The player's own answer: " + ProtokitePlaytest.PlayersConsentAnswer + ".");
            GUILayout.Label(ProtokitePlaytest.Describe(ProtokitePlaytest.PlaytestConsent));
            GUILayout.BeginHorizontal();
            GUI.enabled = !ProtokitePlaytest.IsConsentQuestionOpen;
            if (GUILayout.Button("Show The Consent Question"))
                _said = ProtokitePlaytest.AskForPlaytestConsent() ? "The consent question is on screen." : "The question could not be put: " + ProtokitePlaytest.Describe(ProtokitePlaytest.Status);
            GUI.enabled = true;
            if (GUILayout.Button("Answer: Play Data Only"))
                _said = ProtokitePlaytest.SetPlaytestConsent(ProtokitePlaytestConsentChoice.PlayDataOnly) ? "Answered in the game's own menu." : "The answer could not be saved; see the console.";
            GUILayout.EndHorizontal();
        }

        private void DrawSteamId()
        {
            GUILayout.BeginHorizontal();
            _steamId = GUILayout.TextField(_steamId, GUILayout.Width(220f));
            if (GUILayout.Button("Use This Steam Id"))
                _said = ProtokitePlaytest.SetSteamId(_steamId, "Playtest Sample Player")
                    ? "The session names this Steam id."
                    : "Refused: only before the session starts, and only an id of 1 to 64 characters with no spaces.";
            GUILayout.EndHorizontal();
        }

        private void DrawPlaytestEvent()
        {
            if (GUILayout.Button("Record A Playtest Event"))
            {
                bool queued = ProtokitePlaytest.RecordPlaytestEvent(SampleEventName, new Dictionary<string, object> { ["screen"] = "sample" });
                _said = queued ? "The event is queued with the playtest's own." : "Not recorded: the playtest is not measuring play data right now.";
            }
        }

        private void DrawRecording()
        {
            GUILayout.Label("Recording: " + ProtokitePlaytest.IsRecordingVideo + ". Can be sent now: " + ProtokitePlaytest.CanSendTheRecording + ".");
            GUILayout.BeginHorizontal();
            GUI.enabled = ProtokitePlaytest.CanSendTheRecording;
            if (GUILayout.Button("Stop And Send It"))
                _said = ProtokitePlaytest.StopRecordingAndSendIt() ? "Stopped; it uploads once its file is finished." : "Nothing to send.";
            GUI.enabled = ProtokitePlaytest.IsRecordingVideo;
            if (GUILayout.Button("Stop It"))
                _said = ProtokitePlaytest.StopVideoRecording() ? "Stopped for this launch." : "Nothing is recording.";
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void DrawFeedbackForm()
        {
            ProtokitePlaytestForm form = ProtokitePlaytest.FeedbackForm;
            GUILayout.Label(form != null ? "Feedback form: " + form.Title + ", " + form.Fields.Count + " questions." : "No feedback form to open.");
            GUILayout.BeginHorizontal();
            GUI.enabled = ProtokitePlaytest.CanOpenFeedbackForm;
            if (GUILayout.Button("Open It"))
                _said = ProtokitePlaytest.OpenFeedbackForm() ? "The form is open." : "The form did not open; see the console.";
            GUI.enabled = form != null;
            if (GUILayout.Button("Send Answers From Code"))
                _said = SendAnswersFromCode(form);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // A game drawing a form of its own fills in the same answers and hands them over.
        private static string SendAnswersFromCode(ProtokitePlaytestForm form)
        {
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            foreach (ProtokitePlaytestFormField field in form.Fields)
            {
                if (field.Type == ProtokitePlaytestFormFieldTypes.Rating)
                    answers.SetRating(field.Id, ProtokitePlaytestRatings.Highest);
                else if (field.Type == ProtokitePlaytestFormFieldTypes.Checkbox)
                    answers.SetChecked(field.Id, true);
                else if (field.Type == ProtokitePlaytestFormFieldTypes.Select)
                    answers.SetChosenOption(field.Id, field.Options.Count > 0 ? field.Options[0] : "");
                else
                    answers.SetText(field.Id, "Sent from the Protokite Playtest sample.");
            }
            IReadOnlyList<ProtokitePlaytestFormProblem> problems = answers.FindProblems(form);
            if (problems.Count > 0)
                return "Not sent: '" + problems[0].FieldId + "' " + problems[0].Message;
            return ProtokitePlaytest.SendFeedbackForm(answers) ? "Sent: it is kept on this device until Protokite takes it." : "Not sent; see the console.";
        }

        private void DrawTestVideo()
        {
            if (GUILayout.Button("Record A " + TestVideoSeconds + " Second Test Video"))
                _said = ProtokitePlaytest.RecordTestVideo(TestVideoSeconds, out string whyNot) ? "The test video starts at the end of this frame." : "No test video: " + whyNot;
            string where = ProtokitePlaytest.FinishedTestVideoPath ?? ProtokitePlaytest.TestVideoProblem ?? "";
            GUILayout.Label("Test video: " + ProtokitePlaytest.TestVideoState + ". " + where);
        }
    }
}
