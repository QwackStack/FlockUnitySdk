using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flock.Config;
using Flock.Models;
using UnityEditor;

namespace Protokite.Playtest.Editor
{
    /// <summary>What Flock said about a Playtest ID, for the settings it was asked with.</summary>
    internal sealed class ProtokitePlaytestIdAnswer
    {
        /// <summary>The settings asked with, as <see cref="ProtokitePlaytestIdLookup.KeyFor(string, string, string)"/> writes them.</summary>
        public string AskedFor;

        /// <summary>Why Flock could not be asked, or null; when set, nothing else here was learned.</summary>
        public string Problem;

        /// <summary>The playtest's version ID and its name (pt-&lt;test id&gt;), or null when no playtest of this game has the ID.</summary>
        public string VersionId;
        public string VersionName;

        /// <summary>What was found instead, when Flock answered and no playtest of this game has the ID.</summary>
        public string WhyNoPlaytest;
    }

    /// <summary>Finds the playtest's version for a Playtest ID pasted from Protokite: the test's own ID, or the version ID the playtest's SDK block shows.</summary>
    internal static class ProtokitePlaytestIdLookup
    {
        /// <summary>The settings an answer depends on, one line each.</summary>
        public static string KeyFor(string flockApiUrl, string flockApiKey, string playtestId)
            => string.Join("\n", flockApiUrl ?? "", flockApiKey ?? "", playtestId ?? "");

        public static string KeyFor(ProtokitePlaytestSettings settings, FlockConfigAsset flock)
            => KeyFor(flock != null ? flock.apiUrl : null, flock != null ? flock.apiKey : null, settings != null ? settings.PlaytestId : null);

        /// <summary>Whether a Playtest ID is set and the Flock settings have what asking needs.</summary>
        public static bool CanAsk(ProtokitePlaytestSettings settings, FlockConfigAsset flock)
            => settings != null && settings.ChoosesAPlaytest && flock != null && !string.IsNullOrWhiteSpace(flock.apiUrl) && !string.IsNullOrWhiteSpace(flock.apiKey);

        /// <summary>A fingerprint of the Flock URL and API key, kept beside a resolution in place of the key itself: another environment or game has other versions.</summary>
        public static string FlockSettingsFingerprint(string flockApiUrl, string flockApiKey)
        {
            string asked = (flockApiUrl ?? "").Trim().TrimEnd('/') + "\n" + (flockApiKey ?? "").Trim();
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(asked)), 0, 12).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Whether the settings hold a version resolved for their Playtest ID with these Flock settings.</summary>
        public static bool IsResolvedWith(ProtokitePlaytestSettings settings, FlockConfigAsset flock)
            => settings != null && settings.PlaytestVersionId != null && flock != null
               && string.Equals(settings.ResolvedWithFlockSettings, FlockSettingsFingerprint(flock.apiUrl, flock.apiKey), StringComparison.Ordinal);

        /// <summary>The ID as Flock keeps it, in upper case, with spaces around it and a leading pt- left out; null when what is left is not shaped like one.</summary>
        public static string AsAnId(string pasted)
        {
            string id = (pasted ?? "").Trim();
            if (id.StartsWith(ProtokitePlaytestSetupChecks.PlaytestVersionPrefix, StringComparison.OrdinalIgnoreCase))
                id = id.Substring(ProtokitePlaytestSetupChecks.PlaytestVersionPrefix.Length);
            id = id.ToUpperInvariant();
            return ProtokitePlaytestSetupChecks.LooksLikeAnId(id) ? id : null;
        }

        /// <summary>Asks Flock with the Flock settings' own API key: never throws but for a cancellation; a failure is the answer's <see cref="ProtokitePlaytestIdAnswer.Problem"/>.</summary>
        public static async Task<ProtokitePlaytestIdAnswer> AskAsync(string flockApiUrl, string flockApiKey, string playtestId, CancellationToken cancellationToken)
        {
            ProtokitePlaytestIdAnswer answer = new ProtokitePlaytestIdAnswer { AskedFor = KeyFor(flockApiUrl, flockApiKey, playtestId) };
            string id = AsAnId(playtestId);
            if (id == null)
            {
                answer.WhyNoPlaytest = $"'{(playtestId ?? "").Trim()}' is not an ID from Protokite, which is 26 letters and digits. Copy it again from the playtest's page in Protokite.";
                return answer;
            }

            string apiUrl = (flockApiUrl ?? "").Trim().TrimEnd('/');
            try
            {
                // A test's own ID: Protokite names the playtest's version pt-<test id>.
                GameVersionSchema byTestId = await ProtokitePlaytestGameVersionLookup.FindByNameAsync(apiUrl, flockApiKey,
                    ProtokitePlaytestSetupChecks.PlaytestVersionPrefix + id, cancellationToken);
                if (byTestId != null)
                {
                    answer.VersionId = byTestId.Id;
                    answer.VersionName = byTestId.Name;
                    return answer;
                }

                // The version ID the playtest's SDK block shows. Flock names a version ID of any game, so it counts only once its name resolves back to it in this game.
                GameVersionSchema byVersionId = await ProtokitePlaytestGameVersionLookup.FindByIdAsync(apiUrl, flockApiKey, id, cancellationToken);
                if (byVersionId == null)
                {
                    answer.WhyNoPlaytest = $"No playtest of this game has the ID {id}, and no Flock version has it either. Copy it again from the playtest's page in Protokite.";
                    return answer;
                }
                if (!ProtokitePlaytestSetupChecks.IsAPlaytestVersionName(byVersionId.Name))
                {
                    answer.WhyNoPlaytest = $"{id} is the ID of the Flock version named '{byVersionId.Name}', which is not a playtest's version. Copy the ID from the playtest's page in Protokite.";
                    return answer;
                }
                GameVersionSchema back = await ProtokitePlaytestGameVersionLookup.FindByNameAsync(apiUrl, flockApiKey, byVersionId.Name, cancellationToken);
                if (back == null || !string.Equals(back.Id, id, StringComparison.Ordinal))
                {
                    answer.WhyNoPlaytest = $"{id} is the ID of {byVersionId.Name}, a playtest's version of another game: the API key in Flock > Settings belongs to a different game.";
                    return answer;
                }
                answer.VersionId = id;
                answer.VersionName = byVersionId.Name;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                answer.Problem = ProtokitePlaytestGameVersionLookup.DescribeProblem(ex);
            }
            return answer;
        }
    }

    /// <summary>Resolves the game's Playtest ID through Flock once per change of what it depends on, for the inspector and the setup window alike.</summary>
    // A question on its way at a script reload is dropped, and asked again on the next draw.
    internal static class ProtokitePlaytestIdResolver
    {
        private static string _askingFor;
        private static CancellationTokenSource _cancel;

        /// <summary>While set, the inspector and the setup window ask nothing, so a test owns the resolver.</summary>
        internal static bool ViewsWaitForTesting { get; set; }

        /// <summary>What a view calls: only the settings the game loads are resolved, and nothing while a test owns the resolver.</summary>
        public static void ResolveForAView(ProtokitePlaytestSettings settings, FlockConfigAsset flock, bool askAgain = false)
        {
            if (!ViewsWaitForTesting && IsTheGamesSettings(settings))
                ResolveIfChanged(settings, flock, askAgain);
        }

        /// <summary>Whether these are the settings a build loads, the one asset worth resolving.</summary>
        public static bool IsTheGamesSettings(ProtokitePlaytestSettings settings) => settings != null && settings == ProtokitePlaytestSettings.Load();

        /// <summary>The answer to the latest question, or null until one comes back.</summary>
        public static ProtokitePlaytestIdAnswer Answer { get; private set; }

        /// <summary>Whether a question is on its way.</summary>
        public static bool IsAsking => _askingFor != null;

        /// <summary>Raised on the thread the answer lands on, once an answer is taken.</summary>
        public static event Action Answered;

        /// <summary>The latest question, answered or not, so a test can wait for one it held.</summary>
        internal static Task LastQuestionForTesting { get; private set; }

        /// <summary>Asks when the Playtest ID or the Flock settings have changed since the last question, or when told to ask again.</summary>
        public static void ResolveIfChanged(ProtokitePlaytestSettings settings, FlockConfigAsset flock, bool askAgain = false)
        {
            string key = ProtokitePlaytestIdLookup.KeyFor(settings, flock);
            if (!askAgain && (key == _askingFor || (_askingFor == null && Answer != null && Answer.AskedFor == key)))
                return;
            Stop();
            if (!ProtokitePlaytestIdLookup.CanAsk(settings, flock))
                return;
            _askingFor = key;
            _cancel = new CancellationTokenSource();
            LastQuestionForTesting = AskAsync(settings, flock, _cancel);
        }

        /// <summary>Cancels the question on its way, so nothing it answers is taken.</summary>
        public static void Stop()
        {
            _cancel?.Cancel();
            _cancel = null;
            _askingFor = null;
        }

        /// <summary>Forgets the last answer as well, so the next draw asks again.</summary>
        internal static void ForgetForTesting()
        {
            Stop();
            Answer = null;
        }

        private static async Task AskAsync(ProtokitePlaytestSettings settings, FlockConfigAsset flock, CancellationTokenSource cancel)
        {
            ProtokitePlaytestIdAnswer answer;
            try
            {
                answer = await ProtokitePlaytestIdLookup.AskAsync(flock.apiUrl, flock.apiKey, settings.PlaytestId, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // Cancelled while it was on its way: newer settings were asked about.
            if (cancel.IsCancellationRequested)
                return;
            _askingFor = null;
            _cancel = null;
            Answer = answer;
            Keep(settings, flock, answer);
            Answered?.Invoke();
        }

        /// <summary>Keeps an answer for the settings still asked about (saving that asset alone); no answer from Flock keeps what is there, and "no playtest" keeps none.</summary>
        internal static bool Keep(ProtokitePlaytestSettings settings, FlockConfigAsset flock, ProtokitePlaytestIdAnswer answer)
        {
            if (settings == null || flock == null || answer == null || answer.Problem != null || ProtokitePlaytestIdLookup.KeyFor(settings, flock) != answer.AskedFor)
                return false;
            string versionId = answer.VersionId ?? "";
            string fingerprint = ProtokitePlaytestIdLookup.FlockSettingsFingerprint(flock.apiUrl, flock.apiKey);
            if (string.Equals(settings.ResolvedFromPlaytestId, settings.PlaytestId, StringComparison.Ordinal)
                && string.Equals(settings.ResolvedPlaytestVersionId, versionId, StringComparison.Ordinal)
                && string.Equals(settings.ResolvedWithFlockSettings, fingerprint, StringComparison.Ordinal))
                return false;
            settings.KeepResolvedPlaytestVersion(settings.PlaytestId, versionId, fingerprint);
            EditorUtility.SetDirty(settings);
            // This asset only: saving every asset would write the developer's other unsaved edits too.
            if (EditorUtility.IsPersistent(settings))
                AssetDatabase.SaveAssetIfDirty(settings);
            return true;
        }
    }
}
