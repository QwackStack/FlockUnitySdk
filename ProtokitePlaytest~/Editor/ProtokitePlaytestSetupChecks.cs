using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Flock.Config;
using UnityEditor;
using UnityEngine;

namespace Protokite.Playtest.Editor
{
    /// <summary>Where a setup check is put right.</summary>
    internal enum ProtokitePlaytestSetupFix
    {
        None,
        OpenPlaytestSettings,
        OpenFlockSettings,
        UseTheSuggestedGameVersion,
        OpenBuildProfiles
    }

    /// <summary>One setup check, as it stands for the project.</summary>
    internal sealed class ProtokitePlaytestSetupCheck
    {
        /// <summary>Stable, so tests and the window can name a check.</summary>
        public string Id;
        public bool Passed;
        public string Title;
        public string Detail;
        public ProtokitePlaytestSetupFix Fix;

        /// <summary>The Game Version name to set, found for an ID that was pasted, or null.</summary>
        public string SuggestedGameVersion;

        /// <summary>The ID Flock resolves <see cref="SuggestedGameVersion"/> to, or null.</summary>
        public string SuggestedGameVersionId;
    }

    /// <summary>The project's settings the checks are decided from, read once so the checks themselves need no editor.</summary>
    internal sealed class ProtokitePlaytestSetupInput
    {
        /// <summary>The name the Flock SDK loads its settings by, from a Resources folder.</summary>
        public const string FlockSettingsResourceName = "FlockConfig";

        public bool PlaytestSettingsFound;
        public bool PlaytestingEnabled;
        public string ProtokiteApiUrl;
        public bool FlockSettingsFound;
        public string FlockApiUrl;
        public string FlockApiKey;

        /// <summary>The Flock SDK's Game Version: a name, which Flock > Settings resolves to the ID a build sends.</summary>
        public string GameVersion;

        /// <summary>The ID a build sends, resolved from <see cref="GameVersion"/> and kept in the Flock settings.</summary>
        public string GameVersionId;

        public BuildTarget BuildTarget;

        /// <summary>What the build settings name a 64-bit Windows player's processor ("x64", "ARM64"); empty where the editor has no such setting, which builds x64.</summary>
        public string WindowsArchitecture;

        /// <summary>The checks' input from these settings, either of which may be null (the project has none).</summary>
        public static ProtokitePlaytestSetupInput From(ProtokitePlaytestSettings playtest, FlockConfigAsset flock, BuildTarget buildTarget, string windowsArchitecture)
            => new ProtokitePlaytestSetupInput
            {
                PlaytestSettingsFound = playtest != null,
                PlaytestingEnabled = playtest != null && playtest.PlaytestingEnabled,
                ProtokiteApiUrl = playtest != null ? playtest.ProtokiteApiUrl : null,
                FlockSettingsFound = flock != null,
                FlockApiUrl = flock != null ? flock.apiUrl : null,
                FlockApiKey = flock != null ? flock.apiKey : null,
                GameVersion = flock != null ? flock.gameVersion : null,
                GameVersionId = flock != null ? flock.gameVersionId : null,
                BuildTarget = buildTarget,
                WindowsArchitecture = windowsArchitecture ?? ""
            };

        /// <summary>The settings a build of this project carries, and the platform it is built for now.</summary>
        public static ProtokitePlaytestSetupInput FromProject()
            => From(ProtokitePlaytestSettings.Load(), LoadFlockSettings(), EditorUserBuildSettings.activeBuildTarget,
                EditorUserBuildSettings.GetPlatformSettings(BuildPipeline.GetBuildTargetName(BuildTarget.StandaloneWindows64), "Architecture"));

        /// <summary>The Flock settings a build carries, or null.</summary>
        public static FlockConfigAsset LoadFlockSettings() => Resources.Load<FlockConfigAsset>(FlockSettingsResourceName);
    }

    /// <summary>Decides what in a project's settings would stop or change a playtest, before anyone presses Play. No editor, no network.</summary>
    internal static class ProtokitePlaytestSetupChecks
    {
        public const string PlaytestingCheck = "playtesting";
        public const string ProtokiteApiUrlCheck = "protokite_api_url";
        public const string GameVersionCheck = "game_version";
        public const string VideoCheck = "video_on_build_target";

        /// <summary>How Protokite names a playtest's Game Version: this, then the test's id.</summary>
        public const string PlaytestVersionPrefix = "pt-";

        // Flock's IDs are ULIDs: 26 letters and digits of Crockford's base 32, which has no I, L, O or U.
        private static readonly Regex IdShape = new Regex("^[0-9A-HJKMNP-TV-Z]{26}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The four checks, in the order they are fixed. An answer from Flock counts only when it was given for these same settings.</summary>
        public static List<ProtokitePlaytestSetupCheck> Evaluate(ProtokitePlaytestSetupInput input, ProtokitePlaytestGameVersionAnswer flockAnswer)
        {
            ProtokitePlaytestGameVersionAnswer answer = flockAnswer != null && flockAnswer.AskedFor == ProtokitePlaytestGameVersionLookup.KeyFor(input) ? flockAnswer : null;
            return new List<ProtokitePlaytestSetupCheck>
            {
                CheckPlaytesting(input),
                CheckProtokiteApiUrl(input),
                CheckGameVersion(input, answer),
                CheckVideo(input)
            };
        }

        /// <summary>Whether a value has the shape of a Flock ID rather than a name.</summary>
        public static bool LooksLikeAnId(string value) => value != null && IdShape.IsMatch(value.Trim());

        /// <summary>Whether a Game Version name is one Protokite gives a playtest: "pt-", letter for letter, then the test's id.</summary>
        public static bool IsAPlaytestVersionName(string name)
            => name != null && name.Length > PlaytestVersionPrefix.Length && name.StartsWith(PlaytestVersionPrefix, StringComparison.Ordinal);

        /// <summary>Whether players built for this target record video: 64-bit Windows on x64 only, as the runtime decides.</summary>
        public static bool RecordsVideo(BuildTarget target, string windowsArchitecture)
        {
            string architecture = windowsArchitecture ?? "";
            return target == BuildTarget.StandaloneWindows64 && (architecture.Length == 0 || architecture == "x64");
        }

        private static ProtokitePlaytestSetupCheck CheckPlaytesting(ProtokitePlaytestSetupInput input)
        {
            if (!input.PlaytestSettingsFound)
                return Failed(PlaytestingCheck, "No playtest settings",
                    "This project has no playtest settings, so a build of it collects nothing. Create them in Protokite > Playtest > Settings, then turn on Playtesting Enabled.",
                    ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            if (!input.PlaytestingEnabled)
                return Failed(PlaytestingCheck, "Playtesting is turned off", ProtokitePlaytest.Describe(ProtokitePlaytestStatus.TurnedOff),
                    ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            return Passed(PlaytestingCheck, "Playtesting is turned on", "Playtesting Enabled is on in the playtest settings.");
        }

        // The runtime's own rule and words, so this window and the game's log never disagree.
        private static ProtokitePlaytestSetupCheck CheckProtokiteApiUrl(ProtokitePlaytestSetupInput input)
        {
            if (!input.PlaytestSettingsFound)
                return Failed(ProtokiteApiUrlCheck, "No playtest settings", "The Protokite API URL is set in the playtest settings, which this project does not have yet.",
                    ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            string url = input.ProtokiteApiUrl?.Trim();
            if (string.IsNullOrEmpty(url))
                return Failed(ProtokiteApiUrlCheck, "Protokite API URL is empty", ProtokitePlaytest.Describe(ProtokitePlaytestStatus.ProtokiteApiUrlMissing),
                    ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            if (!ProtokitePlaytest.IsUsableApiUrl(url))
                return Failed(ProtokiteApiUrlCheck, "Protokite API URL cannot be used",
                    ProtokitePlaytest.Describe(ProtokitePlaytestStatus.ProtokiteApiUrlUnusable) + $" Current value: '{input.ProtokiteApiUrl}'.",
                    ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            return Passed(ProtokiteApiUrlCheck, "Protokite API URL is usable", $"The playtest reports to {url}.");
        }

        private static ProtokitePlaytestSetupCheck CheckGameVersion(ProtokitePlaytestSetupInput input, ProtokitePlaytestGameVersionAnswer answer)
        {
            if (!input.FlockSettingsFound)
                return Failed(GameVersionCheck, "No Flock settings",
                    $"No {ProtokitePlaytestSetupInput.FlockSettingsResourceName} asset is in a Resources folder, so there is no Game Version to check. Set up the Flock SDK in Flock > Settings.",
                    ProtokitePlaytestSetupFix.OpenFlockSettings);

            string name = input.GameVersion ?? "";
            string baked = input.GameVersionId ?? "";
            string suggested = answer?.SuggestedName;
            string suggestedId = suggested != null ? answer.PastedId : null;
            string useSuggestion = suggested != null
                ? $" {answer.PastedId} is the ID of the version named {suggested}: set Game Version to {suggested}, and every resolve keeps that ID."
                : "";
            ProtokitePlaytestSetupFix fixWithSuggestion = suggested != null ? ProtokitePlaytestSetupFix.UseTheSuggestedGameVersion : ProtokitePlaytestSetupFix.OpenFlockSettings;

            if (name.Trim().Length == 0)
                return Failed(GameVersionCheck, "Game Version is empty",
                    $"Set Game Version in Flock > Settings to the playtest's version, which Protokite names {PlaytestVersionPrefix}<test id>.",
                    ProtokitePlaytestSetupFix.OpenFlockSettings);

            // A name that happens to look like an ID, and that Flock does resolve, is a name.
            if (LooksLikeAnId(name) && !(answer != null && answer.Problem == null && answer.NameFound))
                return WithSuggestion(Failed(GameVersionCheck, "Game Version holds an ID, not a name",
                    $"Game Version is '{name}', which looks like a Game Version ID pasted from Protokite's test page. Flock resolves Game Version by name, so no version " +
                    $"is found for it and the build keeps sending the ID resolved before ({(baked.Length > 0 ? baked : "none")})." +
                    (suggested != null ? useSuggestion : answer == null ? " Checking with Flock for the name that resolves to it." : NoNameFoundFor(answer)),
                    fixWithSuggestion), suggested, suggestedId);

            if (!IsAPlaytestVersionName(name))
                return WithSuggestion(Failed(GameVersionCheck, "Game Version is not a playtest's version",
                    $"Game Version is '{name}'. A playtest runs only in builds whose Game Version matches the version it was created for on Protokite. " +
                    $"Set Game Version in Flock > Settings to that version's name, {PlaytestVersionPrefix}<test id>, not the version ID Protokite's test page shows." + useSuggestion,
                    fixWithSuggestion), suggested, suggestedId);

            if (baked.Length == 0)
                return Failed(GameVersionCheck, "Game Version is not resolved",
                    $"Game Version is {name}, but no ID has been resolved for it, so a build of this project sends none. Resolve Game Version in Flock > Settings.",
                    ProtokitePlaytestSetupFix.OpenFlockSettings);

            if (answer != null && answer.Problem == null)
            {
                if (!answer.NameFound)
                    return Failed(GameVersionCheck, "No version has this name",
                        $"Flock has no version named {name} in this game. Check the test id against Protokite's test page, letter for letter.",
                        ProtokitePlaytestSetupFix.OpenFlockSettings);
                // The name is what the developer set, so resolving it is the fix; the held ID's playtest is only named.
                if (!string.Equals(answer.NameResolvesTo, baked, StringComparison.Ordinal))
                    return Failed(GameVersionCheck, "The build sends another version's ID",
                        $"Flock resolves {name} to {answer.NameResolvesTo}, but the Flock settings hold {baked}, which a build sends, and which the next resolve " +
                        "replaces. Resolve Game Version in Flock > Settings." +
                        (suggested != null ? $" {baked} is the ID of {suggested}: if that is the playtest meant, set Game Version to {suggested} instead." : ""),
                        ProtokitePlaytestSetupFix.OpenFlockSettings);
            }

            string checkedWithFlock = answer == null ? " It has not been checked with Flock yet."
                : answer.Problem != null ? " It could not be checked with Flock: " + answer.Problem
                : " Flock resolves it to that ID.";
            return Passed(GameVersionCheck, "Game Version is a playtest's version", $"Game Version is {name}, and a build sends its ID {baked}.{checkedWithFlock}");
        }

        private static string NoNameFoundFor(ProtokitePlaytestGameVersionAnswer answer)
        {
            if (answer.Problem != null)
                return " It could not be checked with Flock: " + answer.Problem;
            if (!answer.PastedIdFound)
                return " Flock has no version with that ID.";
            if (answer.NameOfPastedIdResolvesBack)
                return $" It is the ID of the version named {answer.NameOfPastedId}, which is not a playtest's version.";
            return $" The version with that ID is named {answer.NameOfPastedId}, but that name does not resolve to it in this game, so it belongs to another game.";
        }

        private static ProtokitePlaytestSetupCheck CheckVideo(ProtokitePlaytestSetupInput input)
        {
            if (RecordsVideo(input.BuildTarget, input.WindowsArchitecture))
                return Passed(VideoCheck, "Players built for this platform record video", "The build target is 64-bit Windows, x64.");
            string platform = input.BuildTarget == BuildTarget.StandaloneWindows64 ? $"64-bit Windows on {input.WindowsArchitecture}" : input.BuildTarget.ToString();
            return Failed(VideoCheck, $"Players built for {platform} record no video",
                "Only 64-bit Windows (x64) builds record the screen. This build still runs the playtest, without video. " +
                "If you want video, switch the build target to Windows, x64.",
                ProtokitePlaytestSetupFix.OpenBuildProfiles);
        }

        private static ProtokitePlaytestSetupCheck Passed(string id, string title, string detail)
            => new ProtokitePlaytestSetupCheck { Id = id, Passed = true, Title = title, Detail = detail, Fix = ProtokitePlaytestSetupFix.None };

        private static ProtokitePlaytestSetupCheck Failed(string id, string title, string detail, ProtokitePlaytestSetupFix fix)
            => new ProtokitePlaytestSetupCheck { Id = id, Passed = false, Title = title, Detail = detail, Fix = fix };

        private static ProtokitePlaytestSetupCheck WithSuggestion(ProtokitePlaytestSetupCheck check, string name, string id)
        {
            check.SuggestedGameVersion = name;
            check.SuggestedGameVersionId = id;
            return check;
        }

        /// <summary>Sets Game Version to the check's suggestion, with the ID Flock resolved it to, as Flock > Settings' resolve would; false when the check has none.</summary>
        public static bool UseTheSuggestedGameVersion(FlockConfigAsset flock, ProtokitePlaytestSetupCheck check)
        {
            if (flock == null || check == null || string.IsNullOrEmpty(check.SuggestedGameVersion) || string.IsNullOrEmpty(check.SuggestedGameVersionId))
                return false;
            Undo.RecordObject(flock, "Use The Playtest's Game Version");
            flock.gameVersion = check.SuggestedGameVersion;
            flock.gameVersionId = check.SuggestedGameVersionId;
            EditorUtility.SetDirty(flock);
            // This asset only: saving every asset would write the developer's other unsaved edits too.
            AssetDatabase.SaveAssetIfDirty(flock);
            return true;
        }
    }
}
