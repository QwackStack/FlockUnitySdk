using System;
using System.Collections.Generic;
using System.Linq;
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
        OpenBuildProfiles,
        ResolveThePlaytestId
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

        /// <summary>The Playtest ID as the playtest settings hold it; empty leaves the playtest to the Game Version.</summary>
        public string PlaytestId = "";

        /// <summary>The playtest's version ID resolved for that Playtest ID, or null.</summary>
        public string PlaytestVersionId;

        /// <summary>Whether that version was resolved with these Flock settings, rather than another environment's or game's.</summary>
        public bool PlaytestResolvedWithTheseFlockSettings;

        /// <summary>Whether a Playtest ID chooses the playtest, as the settings decide it.</summary>
        public bool ChoosesAPlaytest;
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

        /// <summary>The encoder Windows offers first on this PC, the editor's, or null when it offers none a recording may use.</summary>
        public string ThisPcEncoder;

        /// <summary>Why this PC records no video, or null when it records.</summary>
        public string WhyThisPcRecordsNoVideo;

        /// <summary>Whether Android players record video; on where the project has no playtest settings, as a new asset starts.</summary>
        public bool RecordVideoOnAndroid = true;

        /// <summary>The checks' input from these settings, either of which may be null (the project has none).</summary>
        public static ProtokitePlaytestSetupInput From(ProtokitePlaytestSettings playtest, FlockConfigAsset flock, BuildTarget buildTarget, string windowsArchitecture)
            => new ProtokitePlaytestSetupInput
            {
                PlaytestSettingsFound = playtest != null,
                PlaytestingEnabled = playtest != null && playtest.PlaytestingEnabled,
                ProtokiteApiUrl = playtest != null ? playtest.ProtokiteApiUrl : null,
                PlaytestId = playtest != null ? playtest.PlaytestId ?? "" : "",
                PlaytestVersionId = playtest != null ? playtest.PlaytestVersionId : null,
                PlaytestResolvedWithTheseFlockSettings = ProtokitePlaytestIdLookup.IsResolvedWith(playtest, flock),
                ChoosesAPlaytest = playtest != null && playtest.ChoosesAPlaytest,
                FlockSettingsFound = flock != null,
                FlockApiUrl = flock != null ? flock.apiUrl : null,
                FlockApiKey = flock != null ? flock.apiKey : null,
                GameVersion = flock != null ? flock.gameVersion : null,
                GameVersionId = flock != null ? flock.gameVersionId : null,
                BuildTarget = buildTarget,
                WindowsArchitecture = windowsArchitecture ?? "",
                RecordVideoOnAndroid = playtest == null || playtest.RecordVideoOnAndroid
            };

        /// <summary>The settings a build of this project carries, the platform it is built for now, and what records on this PC.</summary>
        public static ProtokitePlaytestSetupInput FromProject()
        {
            ProtokitePlaytestSettings playtest = ProtokitePlaytestSettings.Load();
            ProtokitePlaytestSetupInput input = From(playtest, LoadFlockSettings(), EditorUserBuildSettings.activeBuildTarget,
                EditorUserBuildSettings.GetPlatformSettings(BuildPipeline.GetBuildTargetName(BuildTarget.StandaloneWindows64), "Architecture"));
            bool allowSoftware = playtest != null && playtest.AllowSoftwareEncoder;
            // Asked of Windows once an editor session; the game's graphics card maker's encoder is the one tried first.
            input.WhyThisPcRecordsNoVideo = ProtokitePlaytestVideoEncoders.WhyThisPcRecordsNoVideo(allowSoftware);
            if (input.WhyThisPcRecordsNoVideo == null)
                input.ThisPcEncoder = ProtokitePlaytestVideoEncoders.EncodersOnThisPc(out _)?
                    .Where(encoder => encoder.InHardware || allowSoftware)
                    .OrderBy(encoder => encoder.InHardware ? 0 : 1)
                    .ThenBy(encoder => encoder.VendorId == SystemInfo.graphicsDeviceVendorID ? 0 : 1)
                    .FirstOrDefault()?.ToString();
            return input;
        }

        /// <summary>The Flock settings a build carries, or null.</summary>
        public static FlockConfigAsset LoadFlockSettings() => Resources.Load<FlockConfigAsset>(FlockSettingsResourceName);
    }

    /// <summary>Decides what in a project's settings would stop or change a playtest, before anyone presses Play. No editor, no network.</summary>
    internal static class ProtokitePlaytestSetupChecks
    {
        public const string PlaytestingCheck = "playtesting";
        public const string ProtokiteApiUrlCheck = "protokite_api_url";
        public const string WhichPlaytestCheck = "which_playtest";
        public const string VideoCheck = "video_on_build_target";

        /// <summary>How Protokite names a playtest's Game Version: this, then the test's id (the runtime's rule).</summary>
        public const string PlaytestVersionPrefix = ProtokitePlaytest.PlaytestVersionPrefix;

        // Flock's IDs are ULIDs: 26 letters and digits of Crockford's base 32, which has no I, L, O or U.
        private static readonly Regex IdShape = new Regex("^[0-9A-HJKMNP-TV-Z]{26}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The four checks, in the order they are fixed. An answer from Flock counts only when it was given for these same settings.</summary>
        public static List<ProtokitePlaytestSetupCheck> Evaluate(ProtokitePlaytestSetupInput input, ProtokitePlaytestGameVersionAnswer flockAnswer,
            ProtokitePlaytestIdAnswer playtestIdAnswer = null)
        {
            ProtokitePlaytestGameVersionAnswer answer = flockAnswer != null && flockAnswer.AskedFor == ProtokitePlaytestGameVersionLookup.KeyFor(input) ? flockAnswer : null;
            ProtokitePlaytestIdAnswer playtestAnswer = playtestIdAnswer != null
                && playtestIdAnswer.AskedFor == ProtokitePlaytestIdLookup.KeyFor(input.FlockApiUrl, input.FlockApiKey, input.PlaytestId) ? playtestIdAnswer : null;
            return new List<ProtokitePlaytestSetupCheck>
            {
                CheckPlaytesting(input),
                CheckProtokiteApiUrl(input),
                // With a Playtest ID, the Game Version is the game's own and is not judged here.
                input.ChoosesAPlaytest ? CheckPlaytestId(input, playtestAnswer) : CheckGameVersion(input, answer),
                CheckVideo(input)
            };
        }

        /// <summary>Whether a value has the shape of a Flock ID rather than a name.</summary>
        public static bool LooksLikeAnId(string value) => value != null && IdShape.IsMatch(value.Trim());

        /// <summary>Whether a Game Version name is one Protokite gives a playtest: "pt-", letter for letter, then the test's id.</summary>
        public static bool IsAPlaytestVersionName(string name) => ProtokitePlaytest.IsAPlaytestVersionName(name);

        /// <summary>Whether players built for this target record video: Android, and 64-bit Windows on x64, as the runtime decides.</summary>
        public static bool RecordsVideo(BuildTarget target, string windowsArchitecture)
        {
            if (target == BuildTarget.Android)
                return true;
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

        private static ProtokitePlaytestSetupCheck CheckPlaytestId(ProtokitePlaytestSetupInput input, ProtokitePlaytestIdAnswer answer)
        {
            string typed = input.PlaytestId.Trim();
            string resolved = input.PlaytestVersionId;
            string gameVersion = string.IsNullOrWhiteSpace(input.GameVersion) ? "its own Game Version" : $"Game Version '{input.GameVersion}'";
            // An answer naming another version than the one held is kept the moment it lands, so the two differ only in between.
            if (resolved != null && input.PlaytestResolvedWithTheseFlockSettings
                && (answer == null || answer.Problem != null || string.Equals(answer.VersionId, resolved, StringComparison.Ordinal)))
            {
                string checkedWithFlock = answer == null ? " It has not been checked with Flock again in this editor session."
                    : answer.Problem != null ? " It could not be checked with Flock again: " + answer.Problem
                    : $" Flock names it {answer.VersionName}.";
                return Passed(WhichPlaytestCheck, "Playtest ID chooses this build's playtest",
                    $"Playtest ID {typed} is resolved to the playtest's version {resolved}, which only the playtest's requests to Protokite carry; the Flock SDK " +
                    $"keeps {gameVersion} and everything set up under it.{checkedWithFlock}");
            }

            const string staysOff = " Until it is resolved, playtesting stays off, and a build with Playtesting Enabled on is refused.";
            if (resolved != null && !input.PlaytestResolvedWithTheseFlockSettings && (answer == null || answer.Problem != null))
                return Failed(WhichPlaytestCheck, "Playtest ID was resolved with other Flock settings",
                    $"Playtest ID {typed} was resolved to {resolved} with another Flock API URL or key, whose versions a build with these Flock settings " +
                    "does not have, so a build with Playtesting Enabled on is refused until it is resolved with these." +
                    (answer?.Problem != null ? " It could not be checked with Flock: " + answer.Problem : ""),
                    ProtokitePlaytestSetupFix.ResolveThePlaytestId);
            if (!input.FlockSettingsFound || string.IsNullOrWhiteSpace(input.FlockApiUrl) || string.IsNullOrWhiteSpace(input.FlockApiKey))
                return Failed(WhichPlaytestCheck, "Playtest ID cannot be resolved",
                    "The Playtest ID is resolved through Flock, and Flock > Settings has no API URL or API key to ask with." + staysOff,
                    ProtokitePlaytestSetupFix.OpenFlockSettings);
            if (answer == null)
                return Failed(WhichPlaytestCheck, "Playtest ID is not resolved yet", $"Playtest ID is {typed}. Checking it with Flock." + staysOff,
                    ProtokitePlaytestSetupFix.ResolveThePlaytestId);
            if (answer.Problem != null)
                return Failed(WhichPlaytestCheck, "Playtest ID is not resolved", $"Playtest ID is {typed}, and it could not be checked with Flock: {answer.Problem}" + staysOff,
                    ProtokitePlaytestSetupFix.ResolveThePlaytestId);
            if (answer.VersionId == null)
                return Failed(WhichPlaytestCheck, "No playtest has this ID", answer.WhyNoPlaytest + staysOff, ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            return Failed(WhichPlaytestCheck, "Playtest ID is not resolved yet",
                $"Flock names {typed} the playtest {answer.VersionName} ({answer.VersionId}), and the playtest settings do not hold it yet." + staysOff,
                ProtokitePlaytestSetupFix.ResolveThePlaytestId);
        }

        private static ProtokitePlaytestSetupCheck CheckGameVersion(ProtokitePlaytestSetupInput input, ProtokitePlaytestGameVersionAnswer answer)
        {
            if (!input.FlockSettingsFound)
                return Failed(WhichPlaytestCheck, "No Flock settings",
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
                return Failed(WhichPlaytestCheck, "Game Version is empty",
                    "Set Game Version in Flock > Settings to the game's own version, then paste the ID from the playtest's page in Protokite into " +
                    "Playtest ID in Protokite > Playtest > Settings.",
                    ProtokitePlaytestSetupFix.OpenFlockSettings);

            // A name that happens to look like an ID, and that Flock does resolve, is a name.
            if (LooksLikeAnId(name) && !(answer != null && answer.Problem == null && answer.NameFound))
                return WithSuggestion(Failed(WhichPlaytestCheck, "Game Version holds an ID, not a name",
                    $"Game Version is '{name}', which looks like a Game Version ID pasted from Protokite's test page. Flock resolves Game Version by name, so no version " +
                    $"is found for it and the build keeps sending the ID resolved before ({(baked.Length > 0 ? baked : "none")})." +
                    (suggested != null ? useSuggestion : answer == null ? " Checking with Flock for the name that resolves to it." : NoNameFoundFor(answer)) +
                    " Or paste it into Playtest ID in Protokite > Playtest > Settings, and set Game Version back to the game's own version.",
                    fixWithSuggestion), suggested, suggestedId);

            if (!IsAPlaytestVersionName(name))
            {
                // Meant as a playtest's name and not one letter for letter: the build would join the newest playtest instead of that one.
                if (name.Trim().StartsWith(PlaytestVersionPrefix, StringComparison.OrdinalIgnoreCase))
                    return Failed(WhichPlaytestCheck, "Game Version is not a playtest's name, letter for letter",
                        $"Game Version is '{name}'. Protokite names a playtest's version {PlaytestVersionPrefix} followed by the test's id, letter for letter, so " +
                        "with Playtest ID empty a build joins the game's newest playtest instead. Paste the playtest's ID into Playtest ID in Protokite > " +
                        "Playtest > Settings, and set Game Version to the game's own version.",
                        ProtokitePlaytestSetupFix.OpenPlaytestSettings);
                // The name is the game's own, but the ID held is a playtest's: Flock's own requests would go to that playtest's empty version.
                if (suggested != null)
                    return WithSuggestion(Failed(WhichPlaytestCheck, "The build sends a playtest's version ID",
                        $"Game Version is '{name}', but the Flock settings hold {answer.PastedId}, a playtest's version, which every Flock request of a build " +
                        "would carry. Resolve Game Version in Flock > Settings, and paste the playtest's ID into Playtest ID to join it." + useSuggestion,
                        ProtokitePlaytestSetupFix.UseTheSuggestedGameVersion), suggested, suggestedId);
                return Passed(WhichPlaytestCheck, "The game's newest playtest",
                    $"Playtest ID in Protokite > Playtest > Settings is empty and Game Version '{name}' is the game's own, so a build joins the game's newest " +
                    "playtest in Protokite, whichever is newest when it starts (one created later is joined from the next launch). Paste a playtest's ID into " +
                    "Playtest ID to choose it.");
            }

            if (baked.Length == 0)
                return Failed(WhichPlaytestCheck, "Game Version is not resolved",
                    $"Game Version is {name}, but no ID has been resolved for it, so a build of this project sends none. Resolve Game Version in Flock > Settings.",
                    ProtokitePlaytestSetupFix.OpenFlockSettings);

            if (answer != null && answer.Problem == null)
            {
                if (!answer.NameFound)
                    return Failed(WhichPlaytestCheck, "No version has this name",
                        $"Flock has no version named {name} in this game. Check the test id against Protokite's test page, letter for letter.",
                        ProtokitePlaytestSetupFix.OpenFlockSettings);
                // The name is what the developer set, so resolving it is the fix; the held ID's playtest is only named.
                if (!string.Equals(answer.NameResolvesTo, baked, StringComparison.Ordinal))
                    return Failed(WhichPlaytestCheck, "The build sends another version's ID",
                        $"Flock resolves {name} to {answer.NameResolvesTo}, but the Flock settings hold {baked}, which a build sends, and which the next resolve " +
                        "replaces. Resolve Game Version in Flock > Settings." +
                        (suggested != null ? $" {baked} is the ID of {suggested}: if that is the playtest meant, set Game Version to {suggested} instead." : ""),
                        ProtokitePlaytestSetupFix.OpenFlockSettings);
            }

            string checkedWithFlock = answer == null ? " It has not been checked with Flock yet."
                : answer.Problem != null ? " It could not be checked with Flock: " + answer.Problem
                : " Flock resolves it to that ID.";
            return Passed(WhichPlaytestCheck, "Game Version is a playtest's version", $"Game Version is {name}, and a build sends its ID {baked}.{checkedWithFlock} " +
                "Pasting the playtest's ID into Playtest ID in Protokite > Playtest > Settings instead lets the game keep its own Game Version.");
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
            if (!RecordsVideo(input.BuildTarget, input.WindowsArchitecture))
            {
                string platform = input.BuildTarget == BuildTarget.StandaloneWindows64 ? $"64-bit Windows on {input.WindowsArchitecture}" : input.BuildTarget.ToString();
                return Failed(VideoCheck, $"Players built for {platform} record no video",
                    "Only 64-bit Windows (x64) and Android builds record the screen. This build still runs the playtest, without video. " +
                    "If you want video, switch the build target to Windows, x64, or to Android.",
                    ProtokitePlaytestSetupFix.OpenBuildProfiles);
            }
            if (input.BuildTarget != BuildTarget.Android)
                return Passed(VideoCheck, "Players built for this platform record video",
                    "The build target is 64-bit Windows, x64. Players record with their graphics card's own H.264 encoder, and a PC without one records " +
                    "no video unless Allow Software Encoder is on." + ThisPc(input));
            if (!input.RecordVideoOnAndroid)
                return Failed(VideoCheck, "Players built for Android record no video",
                    "Record Video On Android is off in Protokite > Playtest > Settings, so Android players record nothing and never ask the phone for " +
                    "its encoders. Everything else in the playtest still runs. Turn it on if you want video.",
                    ProtokitePlaytestSetupFix.OpenPlaytestSettings);
            // This PC's encoder is not said here: a test video in the editor records with it and the Windows settings, which proves nothing of a phone.
            return Passed(VideoCheck, "Players built for this platform record video",
                "The build target is Android. Players record with the phone's own hardware H.264 encoder (measured on one 64-bit ARM phone, in " +
                "64-bit and 32-bit players; x86 Android devices are not measured), and a phone without one records no video unless Android Allow " +
                "Software Encoder is on. A test video in the editor records with this PC and the Windows settings, so only a test video in an " +
                "Android player (ProtokitePlaytest.RecordTestVideo) shows whether a phone records.");
        }

        // This PC is not a player's, so what it can record is said beside the check, never as its answer.
        private static string ThisPc(ProtokitePlaytestSetupInput input)
            // Offered is not proven: an encoder Windows lists may still fail to start, which only a test video shows.
            => input.ThisPcEncoder != null ? $" On this PC, Windows offers {input.ThisPcEncoder} first; a test video shows whether it records."
                : input.WhyThisPcRecordsNoVideo != null ? $" This PC records no video, so it records no test video: {input.WhyThisPcRecordsNoVideo}"
                : "";

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
