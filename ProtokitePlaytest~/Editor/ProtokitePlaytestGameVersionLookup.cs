using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flock;
using Flock.Exceptions;
using Flock.Http;
using Flock.Models;

namespace Protokite.Playtest.Editor
{
    /// <summary>What Flock said about a project's Game Version, for the settings it was asked with.</summary>
    internal sealed class ProtokitePlaytestGameVersionAnswer
    {
        /// <summary>The settings asked with, as <see cref="ProtokitePlaytestGameVersionLookup.KeyFor"/> writes them.</summary>
        public string AskedFor;

        /// <summary>Why Flock could not be asked, or null; when set, nothing else here was learned.</summary>
        public string Problem;

        /// <summary>Whether this game has a version of the Game Version's name, and its ID.</summary>
        public bool NameFound;
        public string NameResolvesTo;

        /// <summary>The ID taken to be pasted: Game Version itself when it looks like one and is no name, else the ID the settings hold when the name does not resolve to it.</summary>
        public string PastedId;

        /// <summary>Whether Flock has a version with that ID, and its name.</summary>
        public bool PastedIdFound;
        public string NameOfPastedId;

        /// <summary>Whether that name resolves to the pasted ID in this game; another game's version of the same name does not.</summary>
        public bool NameOfPastedIdResolvesBack;

        /// <summary>The playtest version's name to set Game Version to, once it is proven to resolve to the pasted ID; else null.</summary>
        public string SuggestedName => Problem == null && NameOfPastedIdResolvesBack && ProtokitePlaytestSetupChecks.IsAPlaytestVersionName(NameOfPastedId) ? NameOfPastedId : null;
    }

    /// <summary>Asks Flock what the project's Game Version resolves to, and the name of an ID that was pasted where a name belongs.</summary>
    internal static class ProtokitePlaytestGameVersionLookup
    {
        private const string ApiKeyHeader = "X-Flock-API-Key";
        private const string GameVersionIdHeader = "X-Game-Version-ID";

        /// <summary>The settings an answer depends on, one line each.</summary>
        public static string KeyFor(ProtokitePlaytestSetupInput input)
            => string.Join("\n", input.FlockApiUrl ?? "", input.FlockApiKey ?? "", input.GameVersion ?? "", input.GameVersionId ?? "");

        /// <summary>Whether there is enough in the Flock settings to ask with.</summary>
        public static bool CanAsk(ProtokitePlaytestSetupInput input)
            => input.FlockSettingsFound && !string.IsNullOrWhiteSpace(input.FlockApiUrl) && !string.IsNullOrWhiteSpace(input.FlockApiKey)
               && !string.IsNullOrWhiteSpace(input.GameVersion);

        /// <summary>Asks Flock, with the settings' own API key: never throws but for a cancellation; a failure is the answer's <see cref="ProtokitePlaytestGameVersionAnswer.Problem"/>.</summary>
        public static async Task<ProtokitePlaytestGameVersionAnswer> AskAsync(ProtokitePlaytestSetupInput input, CancellationToken cancellationToken)
        {
            ProtokitePlaytestGameVersionAnswer answer = new ProtokitePlaytestGameVersionAnswer { AskedFor = KeyFor(input) };
            string apiUrl = (input.FlockApiUrl ?? "").Trim().TrimEnd('/');
            string name = input.GameVersion ?? "";
            string baked = input.GameVersionId ?? "";
            try
            {
                // The name exactly as the Flock settings hold it, as their own resolve sends it.
                GameVersionSchema named = await FindByNameAsync(apiUrl, input.FlockApiKey, name, cancellationToken);
                answer.NameFound = named != null;
                answer.NameResolvesTo = named?.Id;

                // Flock keeps its IDs in upper case and matches them letter for letter.
                if (!answer.NameFound && ProtokitePlaytestSetupChecks.LooksLikeAnId(name))
                    answer.PastedId = name.Trim().ToUpperInvariant();
                else if (baked.Length > 0 && !string.Equals(baked, answer.NameResolvesTo, StringComparison.Ordinal))
                    answer.PastedId = baked;
                if (answer.PastedId == null)
                    return answer;

                GameVersionSchema pasted = await FindByIdAsync(apiUrl, input.FlockApiKey, answer.PastedId, cancellationToken);
                answer.PastedIdFound = pasted != null;
                answer.NameOfPastedId = pasted?.Name;
                if (string.IsNullOrEmpty(answer.NameOfPastedId))
                    return answer;

                // Flock names an ID of any game; only a name that resolves back to it in this game is one to set.
                GameVersionSchema back = string.Equals(answer.NameOfPastedId, name, StringComparison.Ordinal)
                    ? named
                    : await FindByNameAsync(apiUrl, input.FlockApiKey, answer.NameOfPastedId, cancellationToken);
                answer.NameOfPastedIdResolvesBack = back != null && string.Equals(back.Id, answer.PastedId, StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                answer.Problem = DescribeProblem(ex);
            }
            return answer;
        }

        // Null when the route answers 404: no such version.
        private static Task<GameVersionSchema> FindByNameAsync(string apiUrl, string apiKey, string name, CancellationToken cancellationToken)
            => FindAsync($"{apiUrl}/{FlockClient.ApiVersion}/game_version/by-name/{Uri.EscapeDataString(name)}",
                new Dictionary<string, string> { { ApiKeyHeader, apiKey } }, cancellationToken);

        private static Task<GameVersionSchema> FindByIdAsync(string apiUrl, string apiKey, string id, CancellationToken cancellationToken)
            => FindAsync($"{apiUrl}/{FlockClient.ApiVersion}/game_version",
                new Dictionary<string, string> { { ApiKeyHeader, apiKey }, { GameVersionIdHeader, id } }, cancellationToken);

        private static async Task<GameVersionSchema> FindAsync(string url, Dictionary<string, string> headers, CancellationToken cancellationToken)
        {
            GenericResponse<GameVersionSchema> response;
            try
            {
                response = await FlockHttpClient.GetAsync<GenericResponse<GameVersionSchema>>(url, headers, cancellationToken);
            }
            // Only the route's own answer that there is no such version; any other 404 came from a wrong address or something on the way.
            catch (FlockException ex) when (ex.StatusCode == 404 && (ex.ErrorCode == FlockErrorCode.GameVersionGameVersionByNameNotFound
                                                                     || ex.ErrorCode == FlockErrorCode.GameVersionGameVersionNotFound))
            {
                return null;
            }
            if (response?.Result == null || string.IsNullOrEmpty(response.Result.Id))
                throw new FlockSerializationException("Flock answered without a Game Version");
            return response.Result;
        }

        // Judged by the status alone: the Flock SDK raises a 403 as an authentication failure, but these routes refuse a key with 401 only.
        private static string DescribeProblem(Exception ex)
        {
            int? status = (ex as FlockException)?.StatusCode;
            if (status == 401)
                return "Flock refused the API key in Flock > Settings (HTTP 401).";
            if (status.HasValue && status.Value > 0)
                return $"Flock answered HTTP {status.Value}. {ex.Message}";
            if (ex is FlockSerializationException)
                return "Flock's answer could not be read. " + ex.Message;
            return "Flock could not be reached. " + ex.Message;
        }
    }

    /// <summary>Asks Flock once for each change of the settings and keeps only the latest question's answer.</summary>
    // A question for settings that have changed since is cancelled, and whatever it still answers is dropped.
    internal sealed class ProtokitePlaytestGameVersionQuestions
    {
        private string _askingFor;
        private CancellationTokenSource _cancel;

        /// <summary>The answer to the latest question, or null until one comes back.</summary>
        public ProtokitePlaytestGameVersionAnswer Answer { get; private set; }

        /// <summary>Whether a question is on its way.</summary>
        public bool IsAsking => _askingFor != null;

        /// <summary>Raised on the thread the answer lands on, once an answer is kept.</summary>
        public event Action Answered;

        /// <summary>The latest question, answered or not, so a test can wait for one it held.</summary>
        internal Task LastQuestionForTesting { get; private set; }

        /// <summary>Asks when the settings have changed since the last question, or when told to ask again.</summary>
        public void AskIfChanged(ProtokitePlaytestSetupInput input, bool askAgain = false)
        {
            string key = ProtokitePlaytestGameVersionLookup.KeyFor(input);
            if (!askAgain && (key == _askingFor || (_askingFor == null && Answer != null && Answer.AskedFor == key)))
                return;
            Stop();
            if (!ProtokitePlaytestGameVersionLookup.CanAsk(input))
                return;
            _askingFor = key;
            _cancel = new CancellationTokenSource();
            LastQuestionForTesting = AskAsync(input, key, _cancel);
        }

        /// <summary>Cancels the question on its way, so nothing it answers is kept.</summary>
        public void Stop()
        {
            _cancel?.Cancel();
            _cancel = null;
            _askingFor = null;
        }

        private async Task AskAsync(ProtokitePlaytestSetupInput input, string key, CancellationTokenSource cancel)
        {
            ProtokitePlaytestGameVersionAnswer answer;
            try
            {
                answer = await ProtokitePlaytestGameVersionLookup.AskAsync(input, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // Cancelled while it was on its way: newer settings were asked about, or the window closed.
            if (cancel.IsCancellationRequested)
                return;
            _askingFor = null;
            _cancel = null;
            Answer = answer;
            Answered?.Invoke();
        }
    }
}
