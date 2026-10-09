"""Reads back the two .unitypackage files a release carries, the way Unity stores them: a gzipped tar with a folder per asset GUID,
holding the asset's path ("pathname"), its .meta ("asset.meta") and, for a file, its bytes ("asset").

Checks, for FlockSDK-<version>.unitypackage and ProtokitePlaytest-<version>.unitypackage:
- both are there, at the version package.json carries, and nothing else is;
- every entry has its path and its .meta, sits under the package's own folder, and no path appears twice;
- the files a studio needs are there, and what must stay in the repository is not (tests, builders, working docs, native files,
  and in the Flock SDK's package anything of the playtest's);
- the version inside each package is the release's;
- every GUID a Unity text asset names is carried by the package, or is one of Unity's own.

Usage: python3 check_release_unitypackages.py <folder with the packages> <repository root>
Exits 1 with a "::error::" line per problem, so GitHub shows each on the run.
"""
import json
import re
import sys
import tarfile
from pathlib import Path

GUID_REFERENCE = re.compile(rb"guid:\s*([0-9a-f]{32})")
# Unity's own resources (built-in extras, default resources): never in a package.
UNITY_OWN_GUID = re.compile(r"^0{16}[0-9a-f]0{15}$")
# Native binaries only: the Flock SDK's WebGL .jslib is JavaScript and ships.
NATIVE_FILE = re.compile(r"\.(dll|so|dylib|a|bundle)$", re.IGNORECASE)

PACKAGES = {
    "FlockSDK": {
        "root": "Assets/FlockSDK/",
        "version_file": "Runtime/FlockSdkVersion.cs",
        "required": ["package.json", "README.md", "CHANGELOG.md", "Runtime/FlockClient.cs", "Runtime/FlockSdkVersion.cs",
                     "Editor/FlockPlaytestInstaller.cs", "Editor/FlockModelPreservation.cs",
                     "Samples/QuickStart/FlockQuickStartSample.cs", "Samples/Multiplayer/FlockMultiplayerSample.cs",
                     "Samples/Multiplayer/FlockPlayWithFriendsSample.cs", "Samples/Multiplayer/FlockQuickMatchSample.cs"],
        "forbidden": [("Protokite", "the playtest is a package of its own"),
                      ("Tests/", "tests stay in the repository"),
                      ("PackageBuilder/", "the SDK's tests stay in the repository"),
                      ("Documentation~", "the working docs are local only"),
                      ("Tooling~", "the maintainers' tooling stays in the repository"),
                      ("Editor/FlockPackageBuilder.cs", "the release builders are the maintainers' own"),
                      ("Editor/FlockPlaytestPackageBuilder.cs", "the release builders are the maintainers' own"),
                      ("Editor/FlockProviderManifest.cs", "the release builders are the maintainers' own")],
    },
    "ProtokitePlaytest": {
        "root": "Assets/ProtokitePlaytest/",
        "version_file": "Runtime/ProtokitePlaytestVersion.cs",
        "required": ["package.json", "README.md", "CHANGELOG.md", "Runtime/ProtokitePlaytest.cs", "Runtime/ProtokitePlaytestVersion.cs",
                     "Runtime/Resources/ProtokitePlaytestPanelSettings.asset", "Runtime/Resources/ProtokitePlaytestPanelTheme.tss",
                     "Editor/ProtokitePlaytestWindow.cs", "Samples/PlaytestSample/ProtokitePlaytestSample.cs"],
        "forbidden": [("Tests/", "tests stay in the repository"),
                      ("Documentation~", "the working docs are local only")],
    },
}


class Problems:
    def __init__(self):
        self.lines = []

    def add(self, text):
        self.lines.append(text)
        print("::error::" + text, flush=True)


def read_entries(package, problems):
    """Each GUID's parts: pathname (text), asset.meta (bytes), asset (bytes, files only); None when the file cannot be read."""
    entries = {}
    try:
        with tarfile.open(package, "r:gz") as archive:
            for member in archive.getmembers():
                parts = member.name.replace("\\", "/").strip("./").split("/")
                if len(parts) != 2 or not member.isfile():
                    continue
                guid, part = parts
                entries.setdefault(guid, {})[part] = archive.extractfile(member).read()
    except (tarfile.TarError, OSError, EOFError) as error:
        problems.add(f"{package.name} could not be read as a .unitypackage (a gzipped tar): {error}")
        return None
    return entries


def entry_path(parts):
    """The entry's path as Unity reads it (its pathname file's first line), or "" when it has none."""
    lines = parts.get("pathname", b"").decode("utf-8", "replace").splitlines()
    return lines[0].strip() if lines else ""


def check_package(name, version, folder, problems):
    rules = PACKAGES[name]
    package = folder / f"{name}-{version}.unitypackage"
    if not package.is_file():
        problems.add(f"{package.name} is missing from {folder}. The release build makes it; read that step's log for why it did not.")
        return
    entries = read_entries(package, problems)
    if entries is None:
        return
    if not entries:
        problems.add(f"{package.name} holds no entries.")
        return

    root = rules["root"]
    paths = {}
    for guid, parts in entries.items():
        path = entry_path(parts)
        if not path:
            problems.add(f"{package.name}: entry {guid} has no pathname, so Unity cannot place it.")
            continue
        if path in paths:
            problems.add(f"{package.name}: {path} appears twice ({paths[path]} and {guid}).")
        paths[path] = guid
        if "asset.meta" not in parts:
            problems.add(f"{package.name}: {path} has no .meta, so Unity gives it a new GUID on every import and references to it break.")
        if not path.startswith(root):
            problems.add(f"{package.name}: {path} is outside {root}, so it would land somewhere else in a studio's project.")

    relative = {path[len(root):]: guid for path, guid in paths.items() if path.startswith(root)}
    for needed in rules["required"]:
        if needed not in relative or "asset" not in entries[relative[needed]]:
            problems.add(f"{package.name}: {root}{needed} is missing, and every release carries it.")
    for path in relative:
        for fragment, why in rules["forbidden"]:
            if fragment.lower() in path.lower():
                problems.add(f"{package.name}: {root}{path} is in the release, but {why}.")
        if NATIVE_FILE.search(path):
            problems.add(f"{package.name}: {root}{path} is a native file; the packages ship none, so nothing a player's system may refuse goes out.")

    manifest = relative.get("package.json")
    if manifest is not None and "asset" in entries[manifest]:
        try:
            inside = json.loads(entries[manifest]["asset"].decode("utf-8-sig")).get("version")
        except ValueError:
            inside = None
        if inside != version:
            problems.add(f"{package.name}: its package.json says version {inside!r}, but the release is {version}.")
    version_file = relative.get(rules["version_file"])
    if version_file is not None and "asset" in entries[version_file] and f'"{version}"'.encode() not in entries[version_file]["asset"]:
        problems.add(f"{package.name}: {rules['version_file']} does not hold \"{version}\", so the package would report another version.")

    for guid, parts in entries.items():
        asset = parts.get("asset", b"")
        if not asset.startswith(b"%YAML"):
            continue
        path = entry_path(parts) or guid
        for named in sorted({match.decode() for match in GUID_REFERENCE.findall(asset)}):
            if named not in entries and not UNITY_OWN_GUID.match(named):
                problems.add(f"{package.name}: {path} names GUID {named}, which the package does not carry, so it loses that reference on import.")

    print(f"{package.name}: {len(entries)} entries read.", flush=True)


def main():
    if len(sys.argv) != 3:
        print("Usage: python3 check_release_unitypackages.py <folder with the packages> <repository root>")
        return 2
    folder, repository = Path(sys.argv[1]), Path(sys.argv[2])
    problems = Problems()
    try:
        version = json.loads((repository / "package.json").read_text(encoding="utf-8-sig")).get("version")
    except (OSError, ValueError) as error:
        problems.add(f"Could not read the release's version from {repository / 'package.json'}: {error}")
        return 1
    if not version:
        problems.add(f"{repository / 'package.json'} has no version.")
        return 1
    if not folder.is_dir():
        problems.add(f"{folder} does not exist, so the release build wrote no packages there.")
        return 1

    for name in PACKAGES:
        check_package(name, version, folder, problems)
    expected = {f"{name}-{version}.unitypackage" for name in PACKAGES}
    for extra in sorted(path.name for path in folder.glob("*.unitypackage") if path.name not in expected):
        problems.add(f"{extra} is in {folder} but is not a {version} release package; only {', '.join(sorted(expected))} go out.")

    if problems.lines:
        print(f"{len(problems.lines)} problem(s) in the release packages.")
        return 1
    print(f"Both {version} release packages read back clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
