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
    }

    public static partial class ProtokitePlaytest
    {
        /// <summary>One try and one more with a fresh link: a link that expired before the upload began is the failure a second try mends.</summary>
        private const int MostUploadTries = 2;

        private static CancellationTokenSource _uploadsCancel = new CancellationTokenSource();
        private static bool _thisLaunchsUploadStarted;
        private static bool _earlierUploadsStarted;
        private static string _recordingContentType;

        /// <summary>This launch's recording's upload, for tests; null until one starts.</summary>
        internal static Task<ProtokitePlaytestRecordingUploadOutcome> ThisLaunchsUploadForTesting { get; private set; }

        /// <summary>The upload of what earlier launches left, for tests; null until it starts. Answers how many went and how many are kept.</summary>
        internal static Task<(int Uploaded, int Kept)> EarlierUploadsForTesting { get; private set; }

        // This launch's recording goes once its file is finished and its session has started, whichever comes second. At quit
        // the session is no longer Started, so a recording finished then is kept for the next launch.
        private static void UploadThisLaunchsRecordingWhenReady()
        {
            if (_thisLaunchsUploadStarted || _recordingRun == null || FinishedVideo == null)
                return;
            // Before the session check: a recording kept through quitting is one a later launch sends, so only deleting it honours the answer.
            if (!ProtokitePlaytestConsent.AllowsVideoRecording(EffectiveConsent()))
            {
                DeleteWithdrawnRecording();
                return;
            }
            if (FinishedVideo.FilePath == null || _sessionState != ProtokitePlaytestSessionState.Started)
                return;
            _thisLaunchsUploadStarted = true;

            // The address, key and version the session started with, as its end uses: a Flock restart since changes none of them.
            Dictionary<string, string> headers = new Dictionary<string, string>(_sessionHeaders);
            Debug.Log(LogPrefix + $"Uploading this launch's playtest recording to Protokite session {_playtestSessionId}.");
            ThisLaunchsUploadForTesting = UploadThisLaunchsRecordingAsync(_recordingRun, FinishedVideo.FilePath, _recordingContentType, _sessionApiUrl,
                headers, _playtestSessionId, _sessionRetryPolicy, _uploadsCancel.Token);
        }

        private static async Task<ProtokitePlaytestRecordingUploadOutcome> UploadThisLaunchsRecordingAsync(ProtokitePlaytestRecordingRun run,
            string videoPath, string contentType, string apiUrl, Dictionary<string, string> headers, string sessionId, RetryPolicy retryPolicy,
            CancellationToken cancellationToken)
        {
            ProtokitePlaytestRecordingUploadOutcome outcome = await UploadRecordingOrKeepItAsync(run, videoPath, contentType, apiUrl, headers, sessionId,
                retryPolicy, cancellationToken);
            if (outcome.Uploaded && outcome.Deleted && ReferenceEquals(_recordingRun, run))
                _recordingRun = null;
            if (outcome.Uploaded)
                Debug.Log(LogPrefix + $"Playtest recording uploaded to Protokite session {sessionId} ({outcome.BytesSent / BytesPerMegabyte:0.#} MB); it is no longer kept on disk.");
            else
                Debug.LogWarning(LogPrefix + "The playtest recording was not uploaded, so it is kept for a later launch to upload: " + outcome.WhyNot);
            return outcome;
        }

        // What earlier launches left goes once their recordings are gone through and Flock runs, for its API key; one at a
        // time, oldest first, each held while it is sent so no other launch sends it too.
        private static void UploadEarlierRecordingsWhenReady(FlockClient running)
        {
            if (_earlierUploadsStarted || running == null || _uploadsCancel.IsCancellationRequested || _earlierRecordings == null || !_earlierRecordings.IsCompleted)
                return;
            // Waits rather than giving up for the launch, so a change of mind sends them in the same launch.
            if (HoldingBackEarlierRecordings())
                return;
            _earlierUploadsStarted = true;
            EarlierUploadsForTesting = UploadEarlierRecordingsAsync(RecordingsFolder, running.GetGameHeaders(), running.RetryPolicy, _uploadsCancel.Token);
        }

        private static async Task<(int Uploaded, int Kept)> UploadEarlierRecordingsAsync(string folder, Dictionary<string, string> launchHeaders,
            RetryPolicy retryPolicy, CancellationToken cancellationToken)
        {
            int uploaded = 0;
            int kept = 0;
            try
            {
                foreach (string runFolder in ProtokitePlaytestRecordingsFolder.FindRuns(folder, ProtokitePlaytestRecordingKind.Playtest))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
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
                            retryPolicy, cancellationToken);
                        if (outcome.Uploaded)
                        {
                            uploaded++;
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
            return (uploaded, kept);
        }

        private static Task<ProtokitePlaytestRecordingUploadOutcome> UploadWaitingRecordingAsync(ProtokitePlaytestRecordingRun run, string videoPath,
            ProtokitePlaytestSavedSession session, Dictionary<string, string> launchHeaders, RetryPolicy retryPolicy, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(session.ProtokiteApiUrl))
                return Task.FromResult(new ProtokitePlaytestRecordingUploadOutcome { WhyNot = "its saved session names no Protokite API URL." });
            // By the file's ending, the one thing an earlier launch's recording still says about what kind it is.
            return UploadRecordingOrKeepItAsync(run, videoPath, ProtokitePlaytestRecordingFiles.ContentTypeFor(videoPath), session.ProtokiteApiUrl,
                HeadersForTheSession(launchHeaders, session.FlockGameVersionId), session.PlaytestSessionId, retryPolicy, cancellationToken);
        }

        /// <summary>This launch's API key with the Game Version ID the session started with, which is how Protokite finds its playtest; none when it had none.</summary>
        internal static Dictionary<string, string> HeadersForTheSession(Dictionary<string, string> launchHeaders, string sessionGameVersionId)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>(launchHeaders);
            if (string.IsNullOrEmpty(sessionGameVersionId))
                headers.Remove(GameVersionHeader);
            else
                headers[GameVersionHeader] = sessionGameVersionId;
            return headers;
        }

        // Asks for a link only now that the file is finished, counts the recording uploaded only on the storage's own 2xx, and
        // tries once more with a fresh link. A cancelled upload (the launch ended) keeps everything.
        private static async Task<ProtokitePlaytestRecordingUploadOutcome> UploadRecordingOrKeepItAsync(ProtokitePlaytestRecordingRun run, string videoPath,
            string contentType, string apiUrl, Dictionary<string, string> headers, string sessionId, RetryPolicy retryPolicy, CancellationToken cancellationToken)
        {
            ProtokitePlaytestRecordingUploadOutcome outcome = new ProtokitePlaytestRecordingUploadOutcome();
            ProtokiteClient client = new ProtokiteClient(retryPolicy);
            try
            {
                for (int attempt = 1; ; attempt++)
                {
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
            catch (OperationCanceledException)
            {
                outcome.WhyNot = "the launch ended before the upload finished.";
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.WhyNot = "the upload failed: " + ex.Message;
                return outcome;
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
            _thisLaunchsUploadStarted = false;
            _earlierUploadsStarted = false;
            _recordingContentType = null;
            ThisLaunchsUploadForTesting = null;
            EarlierUploadsForTesting = null;
        }
    }
}
