using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using Flock.Providers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Raised twice on purpose by the self-test, to check that a game's exceptions reach Flock once, with their repeat counted.</summary>
    internal sealed class ProtokitePlaytestSelfTestException : Exception
    {
        internal ProtokitePlaytestSelfTestException(string runId)
            : base($"Raised on purpose by the Protokite Playtest self-test (run {runId}) to check that exceptions reach Flock. Nothing is wrong.")
        {
        }
    }

    public static partial class ProtokitePlaytest
    {
        /// <summary>The name of the event the self-test records with the playtest's.</summary>
        internal const string SelfTestEventName = "playtest_self_test";

        /// <summary>The property carrying the run's id on every event and in the extra data of every session start it sends.</summary>
        internal const string SelfTestRunProperty = "playtest_self_test_run";

        // A well-formed id no row has, for the probes that name something that does not exist.
        internal const string SelfTestIdNothingHas = "01ZZZZZZZZZZZZZZZZZZZZZZZZ";
        internal const string SelfTestWrongApiKey = "protokite-playtest-self-test-wrong-key";
        /// <summary>The start of the device id the closed-playtest probe names, followed by the run's id.</summary>
        internal const string SelfTestDeviceIdPrefix = "playtest-self-test-";
        private const string ApiKeyHeader = "X-Flock-API-Key";
        private const double SelfTestWaitSeconds = 60.0;
        private const double SelfTestUploadWaitSeconds = 300.0;

        private static SelfTestRun _selfTest;

        /// <summary>Whether this build runs the self-test, when a test sets it; the editor and development builds otherwise.</summary>
        internal static bool? SelfTestRunsInThisBuildForTesting;

        /// <summary>How long a step waits for the game, when a test sets it; a minute otherwise (five for the upload).</summary>
        internal static double? SelfTestWaitSecondsForTesting;

        /// <summary>Whether a self-test is running now.</summary>
        internal static bool SelfTestIsRunning => _selfTest != null;

        /// <summary>Runs the self-test, or answers why it cannot run, never throwing.</summary>
        internal static Task<ProtokitePlaytestSelfTestReport> RunSelfTestAsync(string closedPlaytestGameVersionId)
        {
            string notRun = WhySelfTestCannotRun();
            if (notRun != null)
            {
                Debug.LogWarning(LogPrefix + "The self-test did not run: " + notRun);
                return Task.FromResult(new ProtokitePlaytestSelfTestReport("", notRun));
            }
            SelfTestRun run = new SelfTestRun(RunningFlock(), _launch, closedPlaytestGameVersionId);
            _selfTest = run;
            return RunSelfTestStepsAsync(run);
        }

        private static string WhySelfTestCannotRun()
        {
            if (!(SelfTestRunsInThisBuildForTesting ?? Debug.isDebugBuild))
                return "it runs in the editor and in development builds only, as it sends Protokite requests meant to be refused.";
            if (_selfTest != null)
                return "a self-test is already running.";
            if (RunningFlock() == null)
                return "the Flock SDK is not running. Enter Play Mode (or start the Flock SDK) and sign a player in first.";
            return null;
        }

        private static async Task<ProtokitePlaytestSelfTestReport> RunSelfTestStepsAsync(SelfTestRun run)
        {
            Debug.Log(LogPrefix + $"Self-test {run.Report.RunId}: starting, against {ProtokitePlaytestSettings.Load()?.ProtokiteApiUrl?.Trim()}.");
            try
            {
                await run.Step("The playtest config loads", SelfTestNeeds.Nothing, CheckTheConfigLoadsAsync);
                await run.Step("A wrong API key is refused (401)", SelfTestNeeds.Config,
                    r => ExpectRefusalAsync(r, 401, () => r.Client.FetchPlaytestConfigAsync(r.ApiUrl, WithHeader(r.GameHeaders, ApiKeyHeader, SelfTestWrongApiKey), CancellationToken.None)));
                await run.Step("A missing API key is refused (422)", SelfTestNeeds.Config,
                    r => ExpectRefusalAsync(r, 422, () => r.Client.FetchPlaytestConfigAsync(r.ApiUrl, WithHeader(r.GameHeaders, ApiKeyHeader, null), CancellationToken.None)));
                await run.Step("A version no playtest is linked to is refused (404)", SelfTestNeeds.Config,
                    r => ExpectRefusalAsync(r, 404, () => r.Client.FetchPlaytestConfigAsync(r.ApiUrl, WithHeader(r.GameHeaders, GameVersionHeader, SelfTestIdNothingHas), CancellationToken.None)));
                await run.Step("This launch's Protokite session starts", SelfTestNeeds.Config, CheckTheSessionStartsAsync);
                await run.Step("A session start naming no player is refused (422)", SelfTestNeeds.Config, CheckAStartNamingNoPlayerIsRefusedAsync);
                await run.Step("A session start for a closed playtest is refused (400)", SelfTestNeeds.Config, CheckAClosedPlaytestTakesNoSessionAsync);
                await run.Step("An exception is raised twice, for Flock to report once with its repeat counted", SelfTestNeeds.Config, CheckAnExceptionIsHandedToFlockAsync);
                await run.Step("A playtest event is recorded, and the playtest's own event name is refused", SelfTestNeeds.Session, CheckAPlaytestEventIsRecordedAsync);
                await run.Step("A form missing a needed answer is refused (422, naming it)", SelfTestNeeds.Session, CheckAFormMissingAnAnswerIsRefusedAsync);
                await run.Step("A form choosing an option not on the list is refused (422, naming it)", SelfTestNeeds.Session, CheckAFormWithAnOptionNotOnTheListIsRefusedAsync);
                await run.Step("A form naming a session that does not exist is refused (404)", SelfTestNeeds.Session, CheckAFormForNoSuchSessionIsRefusedAsync);
                await run.Step("A filled-in form is taken", SelfTestNeeds.Session, CheckAFilledInFormIsTakenAsync);
                await run.Step("An upload link for a session that does not exist is refused (404)", SelfTestNeeds.Config,
                    r => ExpectRefusalAsync(r, 404, () => r.Client.RequestRecordingUploadLinkAsync(r.ApiUrl, r.GameHeaders, SelfTestIdNothingHas,
                        ProtokitePlaytestRecordingFiles.ContentTypeWritten, CancellationToken.None)));
                await run.Step("This launch's recording is uploaded", SelfTestNeeds.Session, CheckTheRecordingIsUploadedAsync);
                await run.Step("An end for a session that does not exist is refused (404)", SelfTestNeeds.Config,
                    r => ExpectRefusalAsync(r, 404, async () =>
                    {
                        await r.Client.EndPlaytestSessionAsync(r.ApiUrl, r.GameHeaders, SelfTestIdNothingHas, CancellationToken.None);
                        return true;
                    }));
            }
            finally
            {
                if (ReferenceEquals(_selfTest, run))
                    _selfTest = null;
            }
            run.LogTheEnd("finished");
            return run.Report;
        }

        /// <summary>The game is closing: a self-test under way says how far it got, as nothing after this is sure to run.</summary>
        private static void StopSelfTestForQuitting()
        {
            SelfTestRun run = _selfTest;
            if (run == null)
                return;
            run.GameIsClosing = true;
            run.LogTheEnd("stopped, as the game is closing, after");
        }

        // Step 1: once Flock runs, the config arrives or is refused; a playtest waiting for the player's answer has still loaded.
        private static async Task<ProtokitePlaytestSelfTestStep> CheckTheConfigLoadsAsync(SelfTestRun run)
        {
            await run.WaitUntilAsync(() => Status != ProtokitePlaytestStatus.FetchingPlaytestConfig && Status != ProtokitePlaytestStatus.WaitingForFlock,
                SelfTestWaitSecondsForTesting ?? SelfTestWaitSeconds);
            ProtokitePlaytestStatus status = Status;
            if (status != ProtokitePlaytestStatus.Ready && status != ProtokitePlaytestStatus.WaitingForPlayerConsent && status != ProtokitePlaytestStatus.PlayerRefusedPlaytest)
                return run.Fail(Describe(status) + (string.IsNullOrEmpty(_configProblem) ? "" : " " + _configProblem));

            run.TakeTheLoadedConfig(_config);
            string loaded = $"playtest {_config.TestId} for Game Version ID {_configGameVersionId}, with {DescribeFeatures(_config)}";
            if (status == ProtokitePlaytestStatus.WaitingForPlayerConsent)
                run.NoSessionBecause = "the player has not said what the playtest may collect, so no session starts. Answer the question on screen, call " +
                                       "ProtokitePlaytest.SetPlaytestConsent, or turn off Ask The Player For Playtest Consent in Protokite > Playtest > Settings.";
            else if (status == ProtokitePlaytestStatus.PlayerRefusedPlaytest)
                run.NoSessionBecause = "the player asked this playtest to collect nothing, so no session starts. Ask again with ProtokitePlaytest.AskForPlaytestConsent.";
            else if (UploadNetworkAnswerIsDue())
                run.NoSessionBecause = "the player has not said which networks recordings may upload on, and the session waits for the answer. Answer the " +
                                       "question on screen, or call ProtokitePlaytest.SetPlaytestUploadNetwork.";
            return run.Pass(run.NoSessionBecause == null ? loaded + "." : loaded + "; " + run.NoSessionBecause);
        }

        // Step 5: the game's own session, started once a Flock session reaches the server; the self-test starts nothing itself.
        private static async Task<ProtokitePlaytestSelfTestStep> CheckTheSessionStartsAsync(SelfTestRun run)
        {
            if (run.NoSessionBecause != null)
                return run.Skip(run.NoSessionBecause);
            await run.WaitUntilAsync(() => _sessionState != ProtokitePlaytestSessionState.NotStarted && _sessionState != ProtokitePlaytestSessionState.Starting,
                SelfTestWaitSecondsForTesting ?? SelfTestWaitSeconds);
            switch (_sessionState)
            {
                case ProtokitePlaytestSessionState.Started:
                    run.TakeTheStartedSession(_playtestSessionId, _sessionApiUrl, _sessionHeaders);
                    return run.Pass($"Protokite session {_playtestSessionId}, as the player's {(_steamId != null ? "Steam id" : "device id")}.");
                case ProtokitePlaytestSessionState.NotStarted:
                    run.NoSessionBecause = "this launch's Protokite session did not start.";
                    return run.Fail("no session started within the wait. It starts once a Flock session reaches the server: sign a player in first, " +
                                    "with Analytics Enabled and Analytics Auto Start Session on in Flock > Settings.");
                case ProtokitePlaytestSessionState.Starting:
                    run.NoSessionBecause = "this launch's Protokite session did not start.";
                    return run.Fail("the session start sent to Protokite had no answer within the wait.");
                case ProtokitePlaytestSessionState.Ended:
                    run.NoSessionBecause = "this launch's Protokite session has ended.";
                    return run.Fail("this launch's session has already ended, and a launch starts one.");
                case ProtokitePlaytestSessionState.NoPlayerIdentity:
                    run.NoSessionBecause = "this launch's Protokite session did not start.";
                    return run.Fail("no Steam id was set and no device id could be kept on this device, so nobody could be named.");
                default:
                    run.NoSessionBecause = "this launch's Protokite session did not start.";
                    return run.Fail(_playtestNoLongerCollecting ? Describe(ProtokitePlaytestStatus.PlaytestNoLongerCollecting) : "Protokite did not take the session start; the log above says why.");
            }
        }

        // Step 6: a start with no Steam id and no device id; one Protokite wrongly takes is ended at once.
        private static Task<ProtokitePlaytestSelfTestStep> CheckAStartNamingNoPlayerIsRefusedAsync(SelfTestRun run)
        {
            JObject body = new JObject { ["extra_debug"] = new JObject { [SelfTestRunProperty] = run.Report.RunId } };
            return ExpectSessionStartRefusalAsync(run, 422, run.GameHeaders, body);
        }

        // Step 7: a start naming a device of the run's own, under a closed playtest's version. Never the player's: a session
        // wrongly made would be theirs, and reading their device id before any session creates the game's file for it.
        private static Task<ProtokitePlaytestSelfTestStep> CheckAClosedPlaytestTakesNoSessionAsync(SelfTestRun run)
        {
            string closed = run.ClosedPlaytestGameVersionId;
            if (closed == null)
                return Task.FromResult(run.Skip("no closed playtest was named. Pass the Game Version ID of a closed playtest of this game to " +
                                                "ProtokitePlaytestSelfTest.RunAsync to check that one takes no session."));
            // A platform issued it: refused rather than trimmed, as a trimmed id hides the mistake that made it.
            if (!ProtokitePlaytestIds.IsUsable(closed, int.MaxValue))
                return Task.FromResult(run.Fail($"'{closed}' is not a Game Version ID: it is empty or holds whitespace."));
            JObject body = new JObject
            {
                ["device_id"] = SelfTestDeviceIdPrefix + run.Report.RunId,
                ["extra_debug"] = new JObject { [SelfTestRunProperty] = run.Report.RunId }
            };
            return ExpectSessionStartRefusalAsync(run, 400, WithHeader(run.GameHeaders, GameVersionHeader, closed), body);
        }

        private static async Task<ProtokitePlaytestSelfTestStep> ExpectSessionStartRefusalAsync(SelfTestRun run, int expectedStatus,
            Dictionary<string, string> headers, JObject body)
        {
            string acceptedSession = null;
            Exception failure = null;
            try
            {
                acceptedSession = await run.Client.StartPlaytestSessionAsync(run.ApiUrl, headers, body, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            string whyNot = WhyNotTheRefusal(failure, expectedStatus);
            if (whyNot == null)
                return run.Pass(RefusedAs(failure));
            if (acceptedSession != null)
            {
                // Ended with the headers it was started with, so a session this run made wrongly is not left in progress.
                try
                {
                    await run.Client.EndPlaytestSessionAsync(run.ApiUrl, headers, acceptedSession, CancellationToken.None);
                    whyNot += $" The session it made, {acceptedSession}, was ended.";
                }
                catch (Exception ex)
                {
                    whyNot += $" The session it made, {acceptedSession}, could not be ended: {ex.Message}";
                }
            }
            return run.Fail(whyNot);
        }

        // Step 8: the same exception twice from one line, which Flock counts as one fault and one repeat.
        private static async Task<ProtokitePlaytestSelfTestStep> CheckAnExceptionIsHandedToFlockAsync(SelfTestRun run)
        {
            if (run.Flock.Analytics is NullAnalyticsProvider)
                return run.Skip("the Flock SDK's analytics is off, so exceptions are not reported. Turn on Analytics Enabled in Flock > Settings.");
            if (!run.Flock.Analytics.HasConsent)
                return run.Skip("the game has not given the Flock SDK's analytics consent, so exceptions are not reported.");
            Debug.Log(LogPrefix + $"Self-test {run.Report.RunId}: the next two exceptions are raised on purpose.");
            for (int raised = 0; raised < 2; raised++)
                Debug.LogException(new ProtokitePlaytestSelfTestException(run.Report.RunId));
            await run.Flock.Analytics.FlushAsync();
            return run.Pass($"raised twice from one line with run id {run.Report.RunId}, and handed to the Flock SDK. It reports the first at once and one " +
                            "summary with repeat_count 1 once a minute has passed, with Capture Exceptions on in Flock > Settings. This game cannot see " +
                            "them arrive: read them in Flock's log events.");
        }

        // Step 9: one of the game's own events goes with the playtest's; the playtest's own name is the game's to leave alone.
        private static async Task<ProtokitePlaytestSelfTestStep> CheckAPlaytestEventIsRecordedAsync(SelfTestRun run)
        {
            if (!IsFeatureEnabled(ProtokitePlaytestFeatures.HeavyAnalytics))
                return run.Skip("heavy analytics is off: the playtest does not turn it on, or the player's answer does not allow play data.");
            // Measuring starts on the frame after the feature is on; a game's event before then is not taken.
            await run.WaitUntilAsync(() => _measuringPerformance, SelfTestWaitSecondsForTesting ?? SelfTestWaitSeconds);
            if (!_measuringPerformance)
                return run.Fail("heavy analytics did not start measuring: the Flock SDK's analytics is off. Turn on Analytics Enabled in Flock > Settings.");
            Dictionary<string, object> properties = new Dictionary<string, object> { [SelfTestRunProperty] = run.Report.RunId };
            if (!RecordPlaytestEvent(SelfTestEventName, properties))
                return run.Fail($"'{SelfTestEventName}' was not queued: the Flock SDK has no analytics consent, or refused the event (a refusal is logged above).");
            Debug.Log(LogPrefix + $"Self-test {run.Report.RunId}: the next warning, a refused event, is expected.");
            if (RecordPlaytestEvent(ProtokitePlaytestEvents.PerformanceWindow, properties))
                return run.Fail($"an event named '{ProtokitePlaytestEvents.PerformanceWindow}' was queued, though the playtest sends events with that name itself.");
            await run.Flock.Analytics.FlushAsync();
            return run.Pass($"'{SelfTestEventName}' was queued and handed to the Flock SDK with run id {run.Report.RunId}, and " +
                            $"'{ProtokitePlaytestEvents.PerformanceWindow}' was refused. Read the event in Flock's analytics events.");
        }

        // Steps 10 to 13 send through the self-test's own client, as the game's sender keeps a form on disk and never says what Protokite answered.
        private static Task<ProtokitePlaytestSelfTestStep> CheckAFormMissingAnAnswerIsRefusedAsync(SelfTestRun run)
        {
            if (run.Form == null)
                return Task.FromResult(run.Skip("the playtest publishes no feedback form."));
            ProtokitePlaytestFormField needed = FirstField(run.Form, field => field.Required);
            if (needed == null)
                return Task.FromResult(run.Skip("no question of the feedback form needs an answer."));
            ProtokitePlaytestFormAnswers answers = SelfTestAnswers(run);
            answers.Remove(needed.Id);
            return ExpectFormRefusalAsync(run, 422, run.SessionId, answers, needed.Id);
        }

        private static Task<ProtokitePlaytestSelfTestStep> CheckAFormWithAnOptionNotOnTheListIsRefusedAsync(SelfTestRun run)
        {
            if (run.Form == null)
                return Task.FromResult(run.Skip("the playtest publishes no feedback form."));
            ProtokitePlaytestFormField select = FirstField(run.Form,
                field => ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Select) && field.Options.Count > 0);
            if (select == null)
                return Task.FromResult(run.Skip("the feedback form has no choice question with options."));
            ProtokitePlaytestFormAnswers answers = SelfTestAnswers(run);
            answers.SetChosenOption(select.Id, AnOptionNotOnTheList(select.Options));
            return ExpectFormRefusalAsync(run, 422, run.SessionId, answers, select.Id);
        }

        private static Task<ProtokitePlaytestSelfTestStep> CheckAFormForNoSuchSessionIsRefusedAsync(SelfTestRun run)
        {
            if (run.Form == null)
                return Task.FromResult(run.Skip("the playtest publishes no feedback form."));
            return ExpectFormRefusalAsync(run, 404, SelfTestIdNothingHas, SelfTestAnswers(run), null);
        }

        private static async Task<ProtokitePlaytestSelfTestStep> CheckAFilledInFormIsTakenAsync(SelfTestRun run)
        {
            if (run.Form == null)
                return run.Skip("the playtest publishes no feedback form.");
            ProtokitePlaytestFormAnswers answers = SelfTestAnswers(run);
            IReadOnlyList<ProtokitePlaytestFormProblem> problems = answers.FindProblems(run.Form);
            if (problems.Count > 0)
                return run.Fail($"the self-test's own answers would be refused: '{problems[0].FieldId}': {problems[0].Message}");
            ProtokitePlaytestFormSubmission submission = SelfTestSubmission(run, run.SessionId, answers);
            if (submission == null)
                return run.Fail("nobody could be named as the sender: no Steam id was set and no device id could be kept on this device.");
            string storedAs = await run.Client.SubmitFeedbackFormAsync(run.SessionApiUrl, run.SessionHeaders, submission, CancellationToken.None);
            return run.Pass($"Protokite stored it as {storedAs}, for session {run.SessionId}.");
        }

        private static async Task<ProtokitePlaytestSelfTestStep> ExpectFormRefusalAsync(SelfTestRun run, int expectedStatus, string sessionId,
            ProtokitePlaytestFormAnswers answers, string questionId)
        {
            ProtokitePlaytestFormSubmission submission = SelfTestSubmission(run, sessionId, answers);
            if (submission == null)
                return run.Fail("nobody could be named as the sender: no Steam id was set and no device id could be kept on this device.");
            Exception failure = null;
            try
            {
                await run.Client.SubmitFeedbackFormAsync(run.SessionApiUrl, run.SessionHeaders, submission, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            string whyNot = WhyNotTheRefusal(failure, expectedStatus, questionId);
            return whyNot == null ? run.Pass(RefusedAs(failure)) : run.Fail(whyNot);
        }

        // Step 15: the launch's own recording, stopped and sent the way the form's button sends it, judged on the storage's own 2xx.
        private static async Task<ProtokitePlaytestSelfTestStep> CheckTheRecordingIsUploadedAsync(SelfTestRun run)
        {
            if (!IsFeatureEnabled(ProtokitePlaytestFeatures.VideoRecording))
                return run.Skip("the playtest records no video: it does not turn video on, or the player's answer does not allow the screen.");
            // The launch's recording starts on a frame once earlier launches' recordings are gone through.
            await run.WaitUntilAsync(() => _videoStartedThisLaunch, SelfTestWaitSecondsForTesting ?? SelfTestWaitSeconds);
            // A recording that already ended (its length limit, or an encoder that could not start) has started; its file is judged below.
            if (_videoRecording == null && FinishedVideo == null)
            {
                string why = _videoNotStartedBecause ?? "no recording started within the wait.";
                return _videoNotStartedIsExpected ? run.Skip("this build records no video: " + why) : run.Fail("the recording did not start: " + why);
            }
            // Frames are captured once the graphics card's encoder has started (0.1 to 2.2 s, measured): a recording stopped before its
            // first frame holds nothing to send.
            await run.WaitUntilAsync(() => _videoRecording == null || !_videoRecording.IsCapturing || _videoRecording.FramesAskedFor > 0,
                SelfTestWaitSecondsForTesting ?? SelfTestWaitSeconds);
            if (IsRecordingVideo && !StopRecordingAndSendIt())
                return run.Fail("the recording could not be stopped and sent.");
            await run.WaitUntilAsync(() => (ThisLaunchsUpload != null && ThisLaunchsUpload.IsCompleted) || (FinishedVideo != null && FinishedVideo.FilePath == null)
                                           || ThisLaunchsRecordingWaitsForWiFi(), SelfTestWaitSecondsForTesting ?? SelfTestUploadWaitSeconds);
            // A recording that kept no file has nothing to upload: its own reason, not the upload's wait running out.
            if (FinishedVideo != null && FinishedVideo.FilePath == null)
                return run.Fail("the recording kept no file to upload: " + (FinishedVideo.Error ?? "no frame was captured before it stopped."));
            if (ThisLaunchsRecordingWaitsForWiFi())
                return run.Skip("the player chose Wi-Fi only and the device is not on Wi-Fi, so the recording waits on it and uploads on Wi-Fi.");
            if (ThisLaunchsUpload == null || !ThisLaunchsUpload.IsCompleted)
                return run.Fail("the recording was not uploaded within the wait: its file was not finished, or the upload is still going.");
            ProtokitePlaytestRecordingUploadOutcome outcome = await ThisLaunchsUpload;
            if (!outcome.Uploaded)
                return run.Fail("it was not uploaded: " + outcome.WhyNot);
            if (!outcome.Deleted)
                return run.Fail($"it was uploaded ({outcome.BytesSent} bytes), but its files could not all be deleted, so a later launch may find them.");
            return run.Pass($"the storage took all {outcome.BytesSent} bytes for session {run.SessionId}, and the recording is no longer kept on disk.");
        }

        // Finished, and not being sent because the player's answer holds it back on this network (never begun, or stopped on leaving Wi-Fi).
        private static bool ThisLaunchsRecordingWaitsForWiFi()
            => FinishedVideo?.FilePath != null && UploadsWaitForWiFi()
               && (ThisLaunchsUpload == null || (ThisLaunchsUpload.Status == TaskStatus.RanToCompletion && ThisLaunchsUpload.Result.StoppedWhenTheDeviceLeftWiFi));

        private static async Task<ProtokitePlaytestSelfTestStep> ExpectRefusalAsync<T>(SelfTestRun run, int expectedStatus, Func<Task<T>> send)
        {
            Exception failure = null;
            try
            {
                await send();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            string whyNot = WhyNotTheRefusal(failure, expectedStatus);
            return whyNot == null ? run.Pass(RefusedAs(failure)) : run.Fail(whyNot);
        }

        /// <summary>Why a request's outcome is not the refusal expected, or null when it is: this status, and for a form, this question named.</summary>
        internal static string WhyNotTheRefusal(Exception failure, int expectedStatus, string questionId = null)
        {
            if (failure == null)
                return $"it was accepted, where Protokite should have answered HTTP {expectedStatus}.";
            // The client reads a success it could not make sense of as this: the request was taken all the same.
            if (failure is FlockSerializationException)
                return $"it was accepted, with an answer that could not be read, where Protokite should have answered HTTP {expectedStatus}.";
            int status = (failure as FlockException)?.StatusCode ?? 0;
            if (status == 0)
                return "no answer came from Protokite: " + failure.Message;
            string said = (failure as FlockException)?.ServerMessage;
            if (status != expectedStatus)
                return $"Protokite answered HTTP {status}, where {expectedStatus} was expected." + (string.IsNullOrEmpty(said) ? "" : " It said: " + said);
            if (questionId == null)
                return null;
            string named = QuestionNamedIn(said);
            if (named == null)
                return $"Protokite refused it with HTTP {status} but named no question, where '{questionId}' was expected. It said: {said}";
            // Letter for letter, as Protokite names the question it checked.
            return string.Equals(named, questionId, StringComparison.Ordinal)
                ? null
                : $"Protokite refused it for question '{named}', where '{questionId}' was expected.";
        }

        /// <summary>The question a form refusal names: the text inside its first pair of single quotes, or null when it has none.</summary>
        internal static string QuestionNamedIn(string refusal)
        {
            if (string.IsNullOrEmpty(refusal))
                return null;
            int start = refusal.IndexOf('\'');
            int end = start < 0 ? -1 : refusal.IndexOf('\'', start + 1);
            return end < 0 ? null : refusal.Substring(start + 1, end - start - 1);
        }

        /// <summary>A choice that is not one of these options: the first one in upper case, else in lower case, else with words added.</summary>
        internal static string AnOptionNotOnTheList(IReadOnlyList<string> options)
        {
            string first = options[0];
            foreach (string candidate in new[] { first.ToUpperInvariant(), first.ToLowerInvariant(), first + " (not on the list)" })
            {
                bool listed = false;
                foreach (string option in options)
                    listed |= string.Equals(option, candidate, StringComparison.Ordinal);
                if (!listed)
                    return candidate;
            }
            return first + " (not on the list " + Guid.NewGuid().ToString("N") + ")";
        }

        private static string RefusedAs(Exception failure)
        {
            FlockException refusal = failure as FlockException;
            return $"Protokite answered HTTP {refusal?.StatusCode}" + (string.IsNullOrEmpty(refusal?.ServerMessage) ? "." : ": " + refusal.ServerMessage);
        }

        /// <summary>A copy of these headers with one set to this value, or left out when the value is null, whatever the letter case it had.</summary>
        internal static Dictionary<string, string> WithHeader(Dictionary<string, string> headers, string name, string value)
        {
            Dictionary<string, string> copy = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> header in headers)
            {
                if (!string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    copy[header.Key] = header.Value;
            }
            if (value != null)
                copy[name] = value;
            return copy;
        }

        private static ProtokitePlaytestFormField FirstField(ProtokitePlaytestForm form, Func<ProtokitePlaytestFormField, bool> wanted)
        {
            foreach (ProtokitePlaytestFormField field in form.Fields)
            {
                if (wanted(field))
                    return field;
            }
            return null;
        }

        // Every question answered as a player might: the best rating, the box ticked, the first option, and text naming the run.
        private static ProtokitePlaytestFormAnswers SelfTestAnswers(SelfTestRun run)
        {
            ProtokitePlaytestFormAnswers answers = new ProtokitePlaytestFormAnswers();
            foreach (ProtokitePlaytestFormField field in run.Form.Fields)
            {
                if (ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Rating))
                    answers.SetRating(field.Id, ProtokitePlaytestRatings.Highest);
                else if (ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Checkbox))
                    answers.SetChecked(field.Id, true);
                else if (ProtokitePlaytestFormAnswers.IsKind(field, ProtokitePlaytestFormFieldTypes.Select))
                    answers.SetChosenOption(field.Id, field.Options.Count > 0 ? field.Options[0] : "");
                else
                    answers.SetText(field.Id, $"Sent by the Protokite Playtest self-test, run {run.Report.RunId}.");
            }
            return answers;
        }

        private static ProtokitePlaytestFormSubmission SelfTestSubmission(SelfTestRun run, string sessionId, ProtokitePlaytestFormAnswers answers)
        {
            JObject identity = SelfTestIdentity(run);
            if (identity == null)
                return null;
            return new ProtokitePlaytestFormSubmission
            {
                PlaytestSessionId = sessionId ?? "",
                SteamId = (string)identity["steam_id"] ?? "",
                DeviceId = (string)identity["device_id"] ?? "",
                ProtokiteApiUrl = run.SessionApiUrl,
                Answers = answers.ToWire(run.Form)
            };
        }

        // Who the launch's session names: the Steam id when the game set one, this install's device id otherwise; null when neither.
        private static JObject SelfTestIdentity(SelfTestRun run)
        {
            if (_steamId != null)
                return new JObject { ["steam_id"] = _steamId };
            string deviceId = ReadDeviceId(out _);
            return deviceId == null ? null : new JObject { ["device_id"] = deviceId };
        }

        private enum SelfTestNeeds
        {
            Nothing,
            Config,
            Session
        }

        /// <summary>One run of the self-test: what it has learned so far, its own client with no retries, and its report.</summary>
        private sealed class SelfTestRun
        {
            internal readonly FlockClient Flock;
            internal readonly int Launch;
            internal readonly string ClosedPlaytestGameVersionId;
            internal readonly ProtokitePlaytestSelfTestReport Report;
            // Each refusal is asked for once, and one Protokite wrongly takes is never sent a second time.
            internal readonly ProtokiteClient Client = new ProtokiteClient(new RetryPolicy { MaxRetries = 0 });
            internal readonly string ApiUrl;
            internal readonly Dictionary<string, string> GameHeadersAsStarted;

            internal ProtokitePlaytestForm Form;
            internal string NoConfigBecause = "the playtest config did not load.";
            internal string NoSessionBecause;
            internal string SessionId;
            internal string SessionApiUrl;
            internal Dictionary<string, string> SessionHeadersAsStarted;
            internal bool GameIsClosing;
            private bool _flockShutDownReported;
            private bool _endLogged;

            internal SelfTestRun(FlockClient flock, int launch, string closedPlaytestGameVersionId)
            {
                Flock = flock;
                Launch = launch;
                ClosedPlaytestGameVersionId = closedPlaytestGameVersionId;
                Report = new ProtokitePlaytestSelfTestReport(Guid.NewGuid().ToString("N").Substring(0, 12), null);
                ApiUrl = ProtokitePlaytestSettings.Load()?.ProtokiteApiUrl?.Trim() ?? "";
                GameHeadersAsStarted = flock.GetGameHeaders();
            }

            // A fresh copy for each request, so no probe's swap reaches another.
            internal Dictionary<string, string> GameHeaders => new Dictionary<string, string>(GameHeadersAsStarted);
            internal Dictionary<string, string> SessionHeaders => new Dictionary<string, string>(SessionHeadersAsStarted);

            internal void TakeTheLoadedConfig(ProtokitePlaytestConfig config)
            {
                Form = config.Form;
                NoConfigBecause = null;
            }

            internal void TakeTheStartedSession(string sessionId, string apiUrl, Dictionary<string, string> headers)
            {
                SessionId = sessionId;
                SessionApiUrl = apiUrl;
                SessionHeadersAsStarted = new Dictionary<string, string>(headers);
            }

            private bool LaunchEnded => GameIsClosing || Launch != _launch;
            private bool FlockGone => !ReferenceEquals(RunningFlock(), Flock);

            internal ProtokitePlaytestSelfTestStep Pass(string detail) => new ProtokitePlaytestSelfTestStep("", ProtokitePlaytestSelfTestOutcome.Passed, detail);
            internal ProtokitePlaytestSelfTestStep Fail(string detail) => new ProtokitePlaytestSelfTestStep("", ProtokitePlaytestSelfTestOutcome.Failed, detail);
            internal ProtokitePlaytestSelfTestStep Skip(string detail) => new ProtokitePlaytestSelfTestStep("", ProtokitePlaytestSelfTestOutcome.Skipped, detail);

            // Runs one step unless what it needs is missing, and records and logs how it came out; a step never throws out of the run.
            internal async Task Step(string name, SelfTestNeeds needs, Func<SelfTestRun, Task<ProtokitePlaytestSelfTestStep>> check)
            {
                ProtokitePlaytestSelfTestStep result;
                if (LaunchEnded)
                    result = Skip("the game closed during the self-test.");
                else if (FlockGone)
                    result = _flockShutDownReported ? Skip("the Flock SDK shut down during the self-test.") : Fail("the Flock SDK shut down during the self-test.");
                else if (needs != SelfTestNeeds.Nothing && NoConfigBecause != null)
                    result = Skip(NoConfigBecause);
                else if (needs == SelfTestNeeds.Session && SessionId == null)
                    result = Skip(NoSessionBecause ?? "this launch's Protokite session did not start.");
                else
                {
                    try
                    {
                        result = await check(this);
                    }
                    catch (Exception ex)
                    {
                        result = Fail(DescribeFailure(ex));
                    }
                    // A step that ended because the launch or Flock did is reported as that, not as what it was waiting for.
                    if (LaunchEnded && result.Outcome != ProtokitePlaytestSelfTestOutcome.Passed)
                        result = Skip("the game closed during the self-test.");
                    else if (FlockGone && result.Outcome != ProtokitePlaytestSelfTestOutcome.Passed && !_flockShutDownReported)
                        result = Fail("the Flock SDK shut down during the self-test.");
                }
                if (FlockGone && result.Outcome == ProtokitePlaytestSelfTestOutcome.Failed)
                    _flockShutDownReported = true;

                ProtokitePlaytestSelfTestStep step = new ProtokitePlaytestSelfTestStep(name, result.Outcome, result.Detail);
                if (_endLogged)
                    return;
                Report.Add(step);
                string line = LogPrefix + $"Self-test: {Label(step.Outcome),-7} {name} - {step.Detail}";
                // A warning, never an error: the Flock SDK reports error lines as exceptions.
                if (step.Outcome == ProtokitePlaytestSelfTestOutcome.Failed)
                    Debug.LogWarning(line);
                else
                    Debug.Log(line);
            }

            // Waits frame by frame, in real time, on the main thread: no timer or thread pool, so WebGL waits too.
            internal async Task WaitUntilAsync(Func<bool> done, double seconds)
            {
                double until = Time.realtimeSinceStartupAsDouble + seconds;
                while (!done() && !LaunchEnded && !FlockGone && Time.realtimeSinceStartupAsDouble < until)
                    await Task.Yield();
            }

            internal void LogTheEnd(string how)
            {
                if (_endLogged)
                    return;
                _endLogged = true;
                string line = LogPrefix + $"Self-test {Report.RunId} {how}: {Report.Passed} passed, {Report.Failed} failed, {Report.Skipped} skipped.";
                // Loud when anything failed or nothing passed: a run that checked nothing is not a green run.
                if (Report.Failed > 0 || Report.Passed == 0)
                    Debug.LogWarning(line);
                else
                    Debug.Log(line);
            }

            private static string Label(ProtokitePlaytestSelfTestOutcome outcome)
                => outcome == ProtokitePlaytestSelfTestOutcome.Passed ? "PASS" : outcome == ProtokitePlaytestSelfTestOutcome.Failed ? "FAIL" : "SKIPPED";

            private static string DescribeFailure(Exception ex)
            {
                FlockException flock = ex as FlockException;
                if (flock?.StatusCode > 0)
                    return $"Protokite answered HTTP {flock.StatusCode}" + (string.IsNullOrEmpty(flock.ServerMessage) ? "." : ": " + flock.ServerMessage);
                return ex.Message;
            }
        }
    }
}
