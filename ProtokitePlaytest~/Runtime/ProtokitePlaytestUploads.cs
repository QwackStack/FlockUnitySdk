using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>Where Protokite said a recording can be uploaded to: a presigned link, short-lived, never kept.</summary>
    internal sealed class ProtokitePlaytestRecordingLink
    {
        public string UploadUrl;

        /// <summary>The link in the route's result, or null when the result names no http or https upload_url. Its bucket and key are not needed: nothing confirms an upload.</summary>
        public static ProtokitePlaytestRecordingLink FromJson(JObject result)
        {
            string url = result?["upload_url"]?.Type == JTokenType.String ? (string)result["upload_url"] : null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                return null;
            return new ProtokitePlaytestRecordingLink { UploadUrl = url };
        }
    }

    /// <summary>What became of one recording's upload.</summary>
    internal sealed class ProtokitePlaytestRecordingUploadOutcome
    {
        /// <summary>True only when the storage answered the file's upload with a 2xx.</summary>
        public bool Uploaded;
        /// <summary>Why it was not uploaded, or null.</summary>
        public string WhyNot;
        public long BytesSent;
        public int LinksAskedFor;
        public int FilesSent;
        /// <summary>Whether the uploaded recording's files were all deleted; a later launch deletes what is left, but never uploads it again.</summary>
        public bool Deleted;
        /// <summary>Stopped because the device left Wi-Fi under a Wi-Fi only answer: kept, and sent again once it is back on Wi-Fi.</summary>
        public bool StoppedWhenTheDeviceLeftWiFi;
    }

    public static partial class ProtokitePlaytest
    {
        /// <summary>One try and one more with a fresh link: a link that expired before the upload began is the failure a second try mends.</summary>
        private const int MostUploadTries = 2;

        private static CancellationTokenSource _uploadsCancel = new CancellationTokenSource();
        private static bool _earlierUploadsStarted;
        private static string _recordingContentType;

        /// <summary>This launch's recording's upload, which the self-test waits for; null until one starts.</summary>
        internal static Task<ProtokitePlaytestRecordingUploadOutcome> ThisLaunchsUpload { get; private set; }

        /// <summary>The latest pass over what earlier launches left; null until one starts. Answers how many went, how many are kept, and whether the device leaving Wi-Fi stopped it.</summary>
        internal static Task<(int Uploaded, int Kept, bool StoppedWhenTheDeviceLeftWiFi)> EarlierUploads { get; private set; }

        // Once a frame: this launch's recording goes once its file is finished, its session has started and the player's answer allows the
        // network. At quit the session is no longer Started, so a recording finished then is kept for the next launch.
        private static void UploadThisLaunchsRecordingWhenReady()
        {
            if (_recordingRun == null || FinishedVideo == null || ThisLaunchsUploadIsUnderWayOrDone())
                return;
            // Before the other checks: a recording kept through quitting, or after an upload that did not go, is one a later launch
            // sends, so only deleting it honours the answer.
            if (!ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent()))
            {
                DeleteWithdrawnRecording();
                return;
            }
            // One upload a launch, but one the device leaving Wi-Fi stopped starts again once it is back on Wi-Fi. Its result is read only
            // once it has one: the main thread never waits for an upload.
            if (ThisLaunchsUpload != null
                && (ThisLaunchsUpload.Status != TaskStatus.RanToCompletion || !ThisLaunchsUpload.Result.StoppedWhenTheDeviceLeftWiFi))
                return;
            if (FinishedVideo.FilePath == null || _sessionState != ProtokitePlaytestSessionState.Started)
                return;
            if (UploadsWaitForWiFi())
            {
                SayUploadsWaitForWiFi();
                return;
            }
            // The address, key and version the session started with, as its end uses: a Flock restart since changes none of them.
            Dictionary<string, string> headers = new Dictionary<string, string>(_sessionHeaders);
            Debug.Log(LogPrefix + $"Uploading this launch's playtest recording to Protokite session {_playtestSessionId}.");
            ThisLaunchsUpload = UploadThisLaunchsRecordingAsync(_recordingRun, FinishedVideo.FilePath, _recordingContentType, _sessionApiUrl,
                headers, _playtestSessionId, _sessionRetryPolicy, _uploadsCancel.Token, BeginUploadOnAnAllowedNetwork());
        }

        // An upload still being sent goes on whatever the player answers meanwhile, and an uploaded recording is sent; anything else of this
        // launch's (not begun, refused, or stopped on leaving Wi-Fi) is still the player's to take back.
        private static bool ThisLaunchsUploadIsUnderWayOrDone()
            => ThisLaunchsUpload != null && (ThisLaunchsUpload.Status != TaskStatus.RanToCompletion || ThisLaunchsUpload.Result.Uploaded);

        private static async Task<ProtokitePlaytestRecordingUploadOutcome> UploadThisLaunchsRecordingAsync(ProtokitePlaytestRecordingRun run,
            string videoPath, string contentType, string apiUrl, Dictionary<string, string> headers, string sessionId, RetryPolicy retryPolicy,
            CancellationToken launchEnds, CancellationToken deviceLeavesWiFi)
        {
            ProtokitePlaytestRecordingUploadOutcome outcome = await UploadRecordingOrKeepItAsync(run, videoPath, contentType, apiUrl, headers, sessionId,
                retryPolicy, launchEnds, deviceLeavesWiFi);
            // Said when it stopped; a later frame starts it again on Wi-Fi.
            if (outcome.StoppedWhenTheDeviceLeftWiFi)
                return outcome;
            if (outcome.Uploaded && outcome.Deleted && ReferenceEquals(_recordingRun, run))
                _recordingRun = null;
            if (outcome.Uploaded)
                Debug.Log(LogPrefix + $"Playtest recording uploaded to Protokite session {sessionId} ({outcome.BytesSent / BytesPerMegabyte:0.#} MB); " +
                          (outcome.Deleted ? "it is no longer kept on disk." : "its files could not all be deleted now."));
            else
                Debug.LogWarning(LogPrefix + "The playtest recording was not uploaded, so it is kept for a later launch to upload: " + outcome.WhyNot);
            return outcome;
        }

        // What earlier launches left goes once their recordings are gone through, Flock runs (for its API key) and the player's answer
        // allows the network; one at a time, oldest first, each held while it is sent so no other launch sends it too.
        private static void UploadEarlierRecordingsWhenReady(FlockClient running)
        {
            // One pass a launch, but one the device leaving Wi-Fi stopped goes through again once it is back on Wi-Fi.
            bool stoppedOffWiFi = EarlierUploads != null && EarlierUploads.Status == TaskStatus.RanToCompletion && EarlierUploads.Result.StoppedWhenTheDeviceLeftWiFi;
            if ((_earlierUploadsStarted && !stoppedOffWiFi) || running == null || _uploadsCancel.IsCancellationRequested || _earlierRecordings == null
                || !_earlierRecordings.IsCompleted)
                return;
            // Waits rather than giving up for the launch, so a change of mind sends them in the same launch.
            if (HoldingBackEarlierRecordings())
                return;
            if (UploadsWaitForWiFi())
            {
                SayUploadsWaitForWiFi();
                return;
            }
            _earlierUploadsStarted = true;
            EarlierUploads = UploadEarlierRecordingsAsync(RecordingsFolder, running.GetGameHeaders(), running.RetryPolicy, _uploadsCancel.Token,
                BeginUploadOnAnAllowedNetwork());
        }

        private static async Task<(int Uploaded, int Kept, bool StoppedWhenTheDeviceLeftWiFi)> UploadEarlierRecordingsAsync(string folder, Dictionary<string, string> launchHeaders,
            RetryPolicy retryPolicy, CancellationToken launchEnds, CancellationToken deviceLeavesWiFi)
        {
            int uploaded = 0;
            int kept = 0;
            bool stoppedWhenTheDeviceLeftWiFi = false;
            try
            {
                foreach (string runFolder in ProtokitePlaytestRecordingsFolder.FindRuns(folder, ProtokitePlaytestRecordingKind.Playtest))
                {
                    if (launchEnds.IsCancellationRequested)
                        break;
                    if (deviceLeavesWiFi.IsCancellationRequested)
                    {
                        stoppedWhenTheDeviceLeftWiFi = true;
                        break;
                    }
                    using (ProtokitePlaytestRecordingRun run = ProtokitePlaytestRecordingRun.ClaimEnded(runFolder, ProtokitePlaytestRecordingKind.Playtest))
                    {
                        // Still recording, or another launch is sending it.
                        if (run == null)
                            continue;
                        ProtokitePlaytestSavedSession session = ProtokitePlaytestRecordingRun.ReadSession(run.FolderPath, out _);
                        string videoPath = ProtokitePlaytestRecordingRun.FinishedVideoPath(run.FolderPath);
                        // A run with no session is the finishing pass's to delete; one with no finished video has nothing to send yet.
                        if (session == null || videoPath == null)
                            continue;

                        ProtokitePlaytestRecordingUploadOutcome outcome = await UploadWaitingRecordingAsync(run, videoPath, session, launchHeaders,
                            retryPolicy, launchEnds, deviceLeavesWiFi);
                        if (outcome.Uploaded)
                        {
                            uploaded++;
                        }
                        else if (outcome.StoppedWhenTheDeviceLeftWiFi)
                        {
                            stoppedWhenTheDeviceLeftWiFi = true;
                            break;
                        }
                        else
                        {
                            kept++;
                            // Not a warning: a build that never records never makes room either, so one Protokite refuses is asked for every launch.
                            Debug.Log(LogPrefix + $"A recording an earlier launch left for Protokite session {session.PlaytestSessionId} was not uploaded, so it is kept: {outcome.WhyNot}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(LogPrefix + $"The recordings earlier launches left in {folder} could not all be gone through for upload; the next launch tries again. {ex.Message}");
            }
            if (uploaded + kept > 0)
                Debug.Log(LogPrefix + $"Recordings earlier launches left: {uploaded} uploaded, {kept} kept for a later launch.");
            return (uploaded, kept, stoppedWhenTheDeviceLeftWiFi);
        }

        private static Task<ProtokitePlaytestRecordingUploadOutcome> UploadWaitingRecordingAsync(ProtokitePlaytestRecordingRun run, string videoPath,
            ProtokitePlaytestSavedSession session, Dictionary<string, string> launchHeaders, RetryPolicy retryPolicy, CancellationToken launchEnds,
            CancellationToken deviceLeavesWiFi)
        {
            if (string.IsNullOrWhiteSpace(session.ProtokiteApiUrl))
                return Task.FromResult(new ProtokitePlaytestRecordingUploadOutcome { WhyNot = "its saved session names no Protokite API URL." });
            // By the file's ending, the one thing an earlier launch's recording still says about what kind it is.
            return UploadRecordingOrKeepItAsync(run, videoPath, ProtokitePlaytestRecordingFiles.ContentTypeFor(videoPath), session.ProtokiteApiUrl,
                HeadersNamingTheVersion(launchHeaders, session.FlockGameVersionId), session.PlaytestSessionId, retryPolicy, launchEnds, deviceLeavesWiFi);
        }

        /// <summary>This launch's API key with the Game Version ID of a playtest, which is how Protokite finds the playtest; none when it is empty.</summary>
        internal static Dictionary<string, string> HeadersNamingTheVersion(Dictionary<string, string> launchHeaders, string playtestGameVersionId)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>(launchHeaders);
            if (string.IsNullOrEmpty(playtestGameVersionId))
                headers.Remove(GameVersionHeader);
            else
                headers[GameVersionHeader] = playtestGameVersionId;
            return headers;
        }

        // Asks for a link only now that the file is finished, counts the recording uploaded only on the storage's own 2xx, and
        // tries once more with a fresh link. A cancelled upload (the launch ended, or the device left Wi-Fi) keeps everything.
        private static async Task<ProtokitePlaytestRecordingUploadOutcome> UploadRecordingOrKeepItAsync(ProtokitePlaytestRecordingRun run, string videoPath,
            string contentType, string apiUrl, Dictionary<string, string> headers, string sessionId, RetryPolicy retryPolicy, CancellationToken launchEnds,
            CancellationToken deviceLeavesWiFi)
        {
            ProtokitePlaytestRecordingUploadOutcome outcome = new ProtokitePlaytestRecordingUploadOutcome();
            ProtokiteClient client = new ProtokiteClient(retryPolicy);
            using (CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(launchEnds, deviceLeavesWiFi))
            {
                CancellationToken cancellationToken = either.Token;
                try
                {
                    return await SendRecordingAsync(client, run, videoPath, contentType, apiUrl, headers, sessionId, outcome, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The launch ending wins: what it was sending is the next launch's to send.
                    outcome.StoppedWhenTheDeviceLeftWiFi = !launchEnds.IsCancellationRequested && deviceLeavesWiFi.IsCancellationRequested;
                    outcome.WhyNot = outcome.StoppedWhenTheDeviceLeftWiFi
                        ? "the device left Wi-Fi, and the player chose Wi-Fi only."
                        : "the launch ended before the upload finished.";
                    return outcome;
                }
                catch (Exception ex)
                {
                    outcome.WhyNot = "the upload failed: " + ex.Message;
                    return outcome;
                }
            }
        }

        private static async Task<ProtokitePlaytestRecordingUploadOutcome> SendRecordingAsync(ProtokiteClient client, ProtokitePlaytestRecordingRun run,
            string videoPath, string contentType, string apiUrl, Dictionary<string, string> headers, string sessionId,
            ProtokitePlaytestRecordingUploadOutcome outcome, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProtokitePlaytestRecordingLink link;
                try
                {
                    outcome.LinksAskedFor++;
                    link = await client.RequestRecordingUploadLinkAsync(apiUrl, headers, sessionId, contentType, cancellationToken);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    outcome.WhyNot = DescribeLinkFailure(ex);
                    return outcome;
                }

                outcome.FilesSent++;
                FlockFileUploadOutcome sent = await FlockHttpClient.UploadFileAsync(link.UploadUrl, videoPath, contentType, cancellationToken);
                outcome.BytesSent = sent.BytesSent;
                if (sent.IsUploaded)
                {
                    outcome.Uploaded = true;
                    // Every file is tried, so the recording is never sent again once either it or its session is gone.
                    outcome.Deleted = run.DeleteEverything();
                    return outcome;
                }

                outcome.WhyNot = DescribeUploadFailure(sent);
                if (attempt >= MostUploadTries || !AFreshLinkCouldHelp(sent))
                    return outcome;
                Debug.Log(LogPrefix + outcome.WhyNot + " Asking for a fresh link and trying once more.");
            }
        }

        // A link refused for its signature (the Content-Type sent is not the one it was signed for) would be refused again.
        private static bool AFreshLinkCouldHelp(FlockFileUploadOutcome sent)
            => !(sent.StatusCode == 403 && sent.Body != null && sent.Body.IndexOf("<Code>SignatureDoesNotMatch</Code>", StringComparison.Ordinal) >= 0);

        // From the HTTP status alone: the route's refusals carry no code, and a 403 here means the session is another game's or
        // playtest's, never a wrong key.
        private static string DescribeLinkFailure(Exception failure)
        {
            int? status = (failure as FlockException)?.StatusCode;
            switch (status)
            {
                case 401:
                case 422:
                    return $"Protokite refused the Flock API key when asked where to upload it (HTTP {status}).";
                case 403:
                    return "Protokite says its session belongs to another game or playtest than this build's API key and the session's Game Version ID name (HTTP 403).";
                case 404:
                    return "Protokite has no such session, or no playtest linked to the session's Game Version ID (HTTP 404).";
                default:
                    return "Protokite gave nowhere to upload it: " + failure.Message + (status.HasValue ? $" (HTTP {status})" : "");
            }
        }

        private static string DescribeUploadFailure(FlockFileUploadOutcome sent)
        {
            switch (sent.Result)
            {
                case FlockHttpResult.Success:
                    string storageCode = StorageErrorCode(sent.Body);
                    return $"the storage answered HTTP {sent.StatusCode}" + (storageCode != null ? $" ({storageCode})." : ".");
                case FlockHttpResult.Timeout:
                    return "the upload stalled: " + sent.Body;
                default:
                    return "the upload could not reach the storage: " + sent.Body;
            }
        }

        // An S3-style error names itself in <Code>...</Code>.
        private static string StorageErrorCode(string body)
        {
            if (string.IsNullOrEmpty(body))
                return null;
            int start = body.IndexOf("<Code>", StringComparison.Ordinal);
            int end = start < 0 ? -1 : body.IndexOf("</Code>", start, StringComparison.Ordinal);
            return start < 0 || end < 0 ? null : body.Substring(start + 6, end - start - 6);
        }

        // Anything still being sent belongs to the launch that is ending: it stops, and what it held is kept.
        private static void StopUploads()
        {
            _uploadsCancel.Cancel();
        }

        private static void ResetUploadsForNewLaunch()
        {
            StopUploads();
            _uploadsCancel = new CancellationTokenSource();
            _earlierUploadsStarted = false;
            _recordingContentType = null;
            ThisLaunchsUpload = null;
            EarlierUploads = null;
        }
    }
}
