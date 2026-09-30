using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Protokite.Playtest
{
    /// <summary>What the player let this playtest collect: the playtest's own question, separate from the Flock SDK's analytics consent, which is the game's.</summary>
    public enum ProtokitePlaytestConsentChoice
    {
        /// <summary>The player has not answered yet; nothing is collected meanwhile.</summary>
        NotAnswered,
        /// <summary>The screen is recorded, and play data is collected.</summary>
        VideoAndPlayData,
        /// <summary>The screen is recorded; no play data is collected.</summary>
        VideoOnly,
        /// <summary>Play data is collected; the screen is not recorded.</summary>
        PlayDataOnly,
        /// <summary>Nothing is collected (no recording, no play data, no Protokite session), though a feedback form the player sends themselves still goes.</summary>
        Nothing
    }

    /// <summary>What each answer allows and how it is spelt, with no panel, file or network near it.</summary>
    internal static class ProtokitePlaytestConsent
    {
        private static readonly (ProtokitePlaytestConsentChoice Choice, string Wire)[] Spellings =
        {
            (ProtokitePlaytestConsentChoice.NotAnswered, "not_answered"),
            (ProtokitePlaytestConsentChoice.VideoAndPlayData, "video_and_play_data"),
            (ProtokitePlaytestConsentChoice.VideoOnly, "video_only"),
            (ProtokitePlaytestConsentChoice.PlayDataOnly, "play_data_only"),
            (ProtokitePlaytestConsentChoice.Nothing, "nothing")
        };

        internal static bool AllowsVideoRecording(ProtokitePlaytestConsentChoice choice)
            => choice == ProtokitePlaytestConsentChoice.VideoAndPlayData || choice == ProtokitePlaytestConsentChoice.VideoOnly;

        /// <summary>Play data is the performance windows, the scene loads and the game's own playtest events.</summary>
        internal static bool AllowsPlayData(ProtokitePlaytestConsentChoice choice)
            => choice == ProtokitePlaytestConsentChoice.VideoAndPlayData || choice == ProtokitePlaytestConsentChoice.PlayDataOnly;

        /// <summary>
        /// Whether the answer lets the playtest run this feature. A feature this build does not know runs only for the answer
        /// that allows everything: the server may add one, and nobody described it to the player.
        /// </summary>
        internal static bool AllowsFeature(ProtokitePlaytestConsentChoice choice, string featureName)
        {
            if (string.Equals(featureName, ProtokitePlaytestFeatures.VideoRecording, StringComparison.Ordinal))
                return AllowsVideoRecording(choice);
            if (string.Equals(featureName, ProtokitePlaytestFeatures.HeavyAnalytics, StringComparison.Ordinal)
                || string.Equals(featureName, ProtokitePlaytestFeatures.ExceptionCapturing, StringComparison.Ordinal))
                return AllowsPlayData(choice);
            return choice == ProtokitePlaytestConsentChoice.VideoAndPlayData;
        }

        /// <summary>Nothing counts as an answer; NotAnswered does not.</summary>
        internal static bool IsAnswered(ProtokitePlaytestConsentChoice choice) => choice != ProtokitePlaytestConsentChoice.NotAnswered;

        internal static bool CollectsAnything(ProtokitePlaytestConsentChoice choice) => AllowsVideoRecording(choice) || AllowsPlayData(choice);

        /// <summary>How the answer is spelt in the saved file and in what a session start sends.</summary>
        internal static string ToWire(ProtokitePlaytestConsentChoice choice)
        {
            foreach ((ProtokitePlaytestConsentChoice Choice, string Wire) spelling in Spellings)
            {
                if (spelling.Choice == choice)
                    return spelling.Wire;
            }
            return Spellings[0].Wire;
        }

        /// <summary>Letter for letter: any other spelling, another letter case included, is NotAnswered, and the player is asked again.</summary>
        internal static ProtokitePlaytestConsentChoice FromWire(string wire)
        {
            foreach ((ProtokitePlaytestConsentChoice Choice, string Wire) spelling in Spellings)
            {
                if (string.Equals(wire, spelling.Wire, StringComparison.Ordinal))
                    return spelling.Choice;
            }
            return ProtokitePlaytestConsentChoice.NotAnswered;
        }

        /// <summary>One sentence saying what the answer allows, for the log.</summary>
        internal static string Describe(ProtokitePlaytestConsentChoice choice)
        {
            switch (choice)
            {
                case ProtokitePlaytestConsentChoice.VideoAndPlayData: return "The player let this playtest record the screen and collect play data.";
                case ProtokitePlaytestConsentChoice.VideoOnly: return "The player let this playtest record the screen, and no play data is collected.";
                case ProtokitePlaytestConsentChoice.PlayDataOnly: return "The player let this playtest collect play data, and the screen is not recorded.";
                case ProtokitePlaytestConsentChoice.Nothing: return "The player asked this playtest to collect nothing, so it collects nothing; a feedback form they send themselves still goes.";
                default: return "The player has not yet said what this playtest may collect, so nothing is collected.";
            }
        }
    }

    /// <summary>
    /// The player's answer, kept in a small file so it holds from one launch of the game to the next. A file that is missing,
    /// unreadable or holds anything else reads as NotAnswered: losing an answer costs one question, guessing one would collect
    /// from somebody who never agreed.
    /// </summary>
    internal sealed class ProtokitePlaytestConsentFile
    {
        private const string AnswerKey = "playtest_consent";
        private const string AnsweredAtKey = "answered_at";
        private const string TemporarySuffix = ".tmp";
        private static readonly TimeSpan TemporaryFileAge = TimeSpan.FromMinutes(1);

        internal ProtokitePlaytestConsentFile(string path) => Path = path;

        internal string Path { get; }

        /// <summary>ProtokitePlaytest/playtest_consent.json in the game's persistent data folder. Main thread only.</summary>
        internal static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, "ProtokitePlaytest", "playtest_consent.json");

        /// <summary>The saved answer, or NotAnswered when the file holds none this build can read.</summary>
        internal ProtokitePlaytestConsentChoice Read()
        {
            try
            {
                if (!File.Exists(Path))
                    return ProtokitePlaytestConsentChoice.NotAnswered;
                JObject saved = JObject.Parse(File.ReadAllText(Path));
                JToken answer = saved[AnswerKey];
                return answer?.Type == JTokenType.String
                    ? ProtokitePlaytestConsent.FromWire((string)answer)
                    : ProtokitePlaytestConsentChoice.NotAnswered;
            }
            catch (Exception)
            {
                return ProtokitePlaytestConsentChoice.NotAnswered;
            }
        }

        /// <summary>
        /// Saves the answer, or removes the file for NotAnswered (asking to be asked again). True when the file now holds it.
        /// A save that fails forgets the answer saved before: the next launch would otherwise collect under one the player replaced.
        /// </summary>
        internal bool Save(ProtokitePlaytestConsentChoice choice)
        {
            if (!ProtokitePlaytestConsent.IsAnswered(choice))
                return Forget();

            DeleteTemporaryFiles(TemporaryFileAge);
            JObject saved = new JObject
            {
                [AnswerKey] = ProtokitePlaytestConsent.ToWire(choice),
                // For whoever opens the file; only the answer decides anything.
                [AnsweredAtKey] = DateTime.UtcNow.ToString("o")
            };
            // A temporary file of its own, moved into place, so a launch that ends mid-save never leaves half an answer.
            string temporary = Path + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
            try
            {
                ProtokitePlaytestSavedFiles.CreateFolder(System.IO.Path.GetDirectoryName(Path));
                ProtokitePlaytestSavedFiles.WriteText(temporary, saved.ToString(Formatting.None));
                if (File.Exists(Path))
                    ProtokitePlaytestSavedFiles.Replace(temporary, Path);
                else
                    ProtokitePlaytestSavedFiles.Move(temporary, Path);
                if (Read() == choice)
                    return true;
            }
            catch (Exception)
            {
                // Forgotten below.
            }
            Forget();
            return false;
        }

        /// <summary>Removes the answer and every temporary file of it, however fresh, so nothing on disk still holds it. True when the file is gone.</summary>
        internal bool Forget()
        {
            DeleteTemporaryFiles(null);
            try
            {
                if (File.Exists(Path))
                {
                    // Even a read-only file: nothing about the flag makes an answer the player took back worth keeping.
                    ProtokitePlaytestSavedFiles.SetAttributes(Path, FileAttributes.Normal);
                    ProtokitePlaytestSavedFiles.Delete(Path);
                }
            }
            catch (Exception)
            {
                // Reported by the check below.
            }
            return !File.Exists(Path);
        }

        // Only those older than olderThan, as a fresh one may belong to another launch saving right now; every one when it is null,
        // whatever its time says (a clock set wrong can date one in the future).
        private void DeleteTemporaryFiles(TimeSpan? olderThan)
        {
            string folder = System.IO.Path.GetDirectoryName(Path);
            string[] files;
            try
            {
                if (!Directory.Exists(folder))
                    return;
                files = Directory.GetFiles(folder, System.IO.Path.GetFileName(Path) + ".*" + TemporarySuffix);
            }
            catch (Exception)
            {
                return;
            }
            foreach (string file in files)
            {
                try
                {
                    if (!olderThan.HasValue || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= olderThan.Value)
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
