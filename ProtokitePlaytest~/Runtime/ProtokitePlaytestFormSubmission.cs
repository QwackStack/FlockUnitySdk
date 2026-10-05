using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Protokite.Playtest
{
    /// <summary>
    /// One filled-in feedback form, and everything a later launch needs to send it: the Protokite URL and the Game Version ID it
    /// was filled in under, since a later launch may run another version. Never an API key: whichever launch sends it uses its own.
    /// </summary>
    internal sealed class ProtokitePlaytestFormSubmission
    {
        /// <summary>The Protokite session the answers belong to; empty when none had started, which Protokite allows.</summary>
        public string PlaytestSessionId = "";
        public string SteamId = "";
        public string DeviceId = "";
        public string ProtokiteApiUrl = "";
        public string FlockGameVersionId = "";

        /// <summary>The answers, already as Protokite keeps them.</summary>
        public JObject Answers = new JObject();

        /// <summary>Whether sending it again replaces the first: Protokite keeps one answer per form and session, so only a form naming a session is safe to send twice.</summary>
        public bool CanBeSentAgainSafely => !string.IsNullOrEmpty(PlaytestSessionId);

        /// <summary>The request body. Empty members are left out: Protokite stores an empty session_id as one rather than reading it as none.</summary>
        public JObject ToBody()
        {
            JObject body = new JObject();
            if (!string.IsNullOrEmpty(PlaytestSessionId))
                body["session_id"] = PlaytestSessionId;
            if (!string.IsNullOrEmpty(SteamId))
                body["steam_id"] = SteamId;
            if (!string.IsNullOrEmpty(DeviceId))
                body["device_id"] = DeviceId;
            body["answers"] = Answers.DeepClone();
            return body;
        }

        /// <summary>The whole submission, as it is kept on disk until it is sent.</summary>
        public string ToSavedJson()
        {
            JObject saved = new JObject
            {
                ["playtest_session_id"] = PlaytestSessionId ?? "",
                ["steam_id"] = SteamId ?? "",
                ["device_id"] = DeviceId ?? "",
                ["protokite_api_url"] = ProtokiteApiUrl ?? "",
                ["flock_game_version_id"] = FlockGameVersionId ?? "",
                ["answers"] = Answers.DeepClone()
            };
            return saved.ToString(Formatting.None);
        }

        /// <summary>Reads a kept one back; null, with the reason, when it cannot be read or could never be sent.</summary>
        public static ProtokitePlaytestFormSubmission FromSavedJson(string json, out string whyNot)
        {
            JObject saved;
            try
            {
                // Dates left as written: a player's answer that looks like one must come back letter for letter.
                using (JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    saved = JObject.Load(reader);
            }
            catch (JsonException)
            {
                whyNot = "it is not readable.";
                return null;
            }
            ProtokitePlaytestFormSubmission submission = new ProtokitePlaytestFormSubmission
            {
                PlaytestSessionId = Text(saved, "playtest_session_id"),
                SteamId = Text(saved, "steam_id"),
                DeviceId = Text(saved, "device_id"),
                ProtokiteApiUrl = Text(saved, "protokite_api_url"),
                FlockGameVersionId = Text(saved, "flock_game_version_id")
            };
            // Protokite refuses one naming nobody, so sending it would only spend a request.
            if (submission.SteamId.Length == 0 && submission.DeviceId.Length == 0)
            {
                whyNot = "it names nobody who filled it in.";
                return null;
            }
            if (submission.ProtokiteApiUrl.Trim().Length == 0)
            {
                whyNot = "it names nowhere to send it.";
                return null;
            }
            if (!(saved["answers"] is JObject answers))
            {
                whyNot = "it holds no answers.";
                return null;
            }
            submission.Answers = answers;
            whyNot = null;
            return submission;
        }

        private static string Text(JObject json, string name) => json[name]?.Type == JTokenType.String ? (string)json[name] : "";

        /// <summary>The question a refusal is about, read out of Protokite's message ("Missing required answer 'steps'"), which names it only in quotes; empty when it names none.</summary>
        public static string FindQuestionInRefusal(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "";
            int start = message.IndexOf('\'');
            int end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
            return end < 0 ? "" : message.Substring(start + 1, end - start - 1);
        }
    }

    /// <summary>
    /// Filled-in feedback forms kept on disk until Protokite has taken them: a report the player wrote is the one thing here that
    /// cannot be collected again. One file per form, written through a temporary file of its own and moved into place, so a launch
    /// that ends mid-write leaves the whole form or none of it. A file goes once taken, or once Protokite refuses it for good.
    /// </summary>
    internal sealed class ProtokitePlaytestKeptForms
    {
        internal const string Extension = ".json";
        private const string TemporarySuffix = ".tmp";

        /// <summary>A temporary file older than this was left by a write that never finished; a fresh one may be another launch's write in progress.</summary>
        internal static readonly TimeSpan TemporaryFileAge = TimeSpan.FromMinutes(1);

        internal ProtokitePlaytestKeptForms(string folder)
        {
            Folder = folder;
        }

        internal string Folder { get; }

        /// <summary>Writes one down, named so the oldest sorts first; its path, or null with the reason when it could not be written.</summary>
        internal string Keep(ProtokitePlaytestFormSubmission submission, out string error)
        {
            // Named for when it was written, then made unique: forms from one launch keep their order, and none lands on another's name.
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Extension;
            string path = Path.Combine(Folder, name);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
            try
            {
                ProtokitePlaytestSavedFiles.CreateFolder(Folder);
                DeleteLeftOverTemporaryFiles();
                ProtokitePlaytestSavedFiles.WriteText(temporary, submission.ToSavedJson());
                ProtokitePlaytestSavedFiles.Move(temporary, path);
                error = null;
                return path;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(temporary))
                        ProtokitePlaytestSavedFiles.Delete(temporary);
                }
                catch (Exception)
                {
                    // Left for a later sweep.
                }
                error = $"it could not be written to {path}: {ex.Message}";
                return null;
            }
        }

        /// <summary>Every whole form waiting, oldest first; a temporary file is never one.</summary>
        internal IReadOnlyList<string> FindWaiting()
        {
            List<string> waiting = new List<string>();
            try
            {
                if (!Directory.Exists(Folder))
                    return waiting;
                waiting.AddRange(Directory.GetFiles(Folder, "*" + Extension));
            }
            catch (Exception)
            {
                return waiting;
            }
            waiting.Sort(StringComparer.Ordinal);
            return waiting;
        }

        /// <summary>
        /// Claims one form for sending, so no other launch sends it meanwhile (on Windows; elsewhere the claim holds within this
        /// launch only); null when another holds it or it is gone. The file may be deleted while claimed.
        /// </summary>
        internal static FileStream Claim(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads a claimed form.</summary>
        internal static string ReadClaimed(FileStream claim)
        {
            claim.Position = 0;
            using (StreamReader reader = new StreamReader(claim, Encoding.UTF8, true, 4096, true))
                return reader.ReadToEnd();
        }

        /// <summary>Deletes a claimed form while the claim still holds it, so no other launch can claim it in between, then lets go. True when it is gone.</summary>
        internal static bool ForgetClaimed(FileStream claim, string path)
        {
            try
            {
                ProtokitePlaytestSavedFiles.Delete(path);
            }
            catch (Exception)
            {
                // Reported by the check below.
            }
            claim.Dispose();
            return !File.Exists(path);
        }

        private void DeleteLeftOverTemporaryFiles()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(Folder, "*" + TemporarySuffix);
            }
            catch (Exception)
            {
                return;
            }
            foreach (string file in files)
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= TemporaryFileAge)
                        ProtokitePlaytestSavedFiles.Delete(file);
                }
                catch (Exception)
                {
                    // Left for a later sweep.
                }
            }
        }
    }
}
