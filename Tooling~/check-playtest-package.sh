#!/usr/bin/env bash
# Checks the Protokite Playtest package against the rules that keep it separate from the Flock SDK and installable.
# Usage: bash "Tooling~/check-playtest-package.sh" [repository root]   (defaults to the current folder)
set -uo pipefail

ROOT="${1:-.}"
PLAYTEST="$ROOT/ProtokitePlaytest~"
fail=0

error() {
  echo "::error::$1"
  fail=1
}

if [ ! -f "$PLAYTEST/package.json" ]; then
  echo "::error::$PLAYTEST/package.json is missing. The playtest package lives in ProtokitePlaytest~ at the repository root; if it moved, update this script with it."
  exit 1
fi

# Prints one top-level member of a package.json; fails when the file cannot be read or the member is missing.
# The paths go in as arguments, never inside the script text, so every shell hands node a path it can open.
json_member() {
  node -e '
    const [file, member] = process.argv.slice(1);
    const value = JSON.parse(require("fs").readFileSync(file, "utf8"))[member];
    if (value === undefined) process.exit(2);
    process.stdout.write(typeof value === "string" ? value : JSON.stringify(value));
  ' "$1" "$2"
}

# 1. Released together from one tag, so both carry the same version.
core_version=$(json_member "$ROOT/package.json" version) || { echo "::error::Could not read the version in $ROOT/package.json."; exit 1; }
playtest_version=$(json_member "$PLAYTEST/package.json" version) || { echo "::error::Could not read the version in $PLAYTEST/package.json."; exit 1; }
if [ -z "$core_version" ] || [ -z "$playtest_version" ]; then
  echo "::error::A package.json has an empty version (Flock SDK '$core_version', playtest '$playtest_version')."
  exit 1
fi
if [ "$core_version" != "$playtest_version" ]; then
  error "The playtest package is $playtest_version but the Flock SDK is $core_version. They ship from one tag at one version: bump ProtokitePlaytest~/package.json with package.json."
fi

# 1b. The version each session reports is a constant in the package's code, bumped by hand with package.json.
version_file="$PLAYTEST/Runtime/ProtokitePlaytestVersion.cs"
code_version=$(grep -oE 'Current = "[^"]*"' "$version_file" 2>/dev/null | sed -E 's/Current = "(.*)"/\1/')
if [ -z "$code_version" ]; then
  error "Could not read the version constant in $version_file. Sessions report it as sdk_version; if it moved, update this script with it."
elif [ "$code_version" != "$playtest_version" ]; then
  error "ProtokitePlaytestVersion.Current is $code_version but ProtokitePlaytest~/package.json is $playtest_version. Bump the constant with the package."
fi

# 2. No package dependency on the Flock SDK: a studio that imported Flock from the .unitypackage has no com.flock.sdk
#    package, and Package Manager would refuse the playtest. The playtest reaches Flock through its assembly instead.
dependencies=$(json_member "$PLAYTEST/package.json" dependencies)
case $? in
  0) if echo "$dependencies" | grep -q '"com.flock.sdk"'; then
       error "ProtokitePlaytest~/package.json depends on com.flock.sdk. Remove it: studios that imported Flock from the .unitypackage could not install the playtest. Reference the Flock.Runtime assembly instead."
     fi ;;
  2) ;;  # no dependencies at all
  *) echo "::error::Could not read $PLAYTEST/package.json."; exit 1 ;;
esac

# 3. The Flock SDK's runtime never names the playtest. Its editor may, for the Playtesting tab's install button.
named=$(grep -rniI "protokite" "$ROOT/Runtime" "$ROOT/package.json" 2>/dev/null || true)
if [ -n "$named" ]; then
  error "The Flock SDK's runtime names the playtest; it must never depend on it:"
  echo "$named"
fi

# 4. Package Manager treats a git package as read-only and ignores any file or folder without a .meta beside it. Folders
#    ending in ~ (the native source in Native~) are never imported, so they carry none.
missing=0
while IFS= read -r path; do
  if [ ! -f "$path.meta" ]; then
    echo "  no .meta: ${path#"$ROOT/"}"
    missing=1
  fi
done < <(find "$PLAYTEST" -mindepth 1 ! -name "*.meta" ! -name ".*" ! -path "*/.*" ! -path "$PLAYTEST/*~" ! -path "$PLAYTEST/*~/*")
if [ "$missing" -ne 0 ]; then
  error "Files in ProtokitePlaytest~ have no .meta, so a git-URL install would leave them out. Open the package in a Unity project (Libraries/Unity/packages/com.protokite.playtest links to it) so Unity writes them, then commit them."
fi

# 5. The Windows video encoder ships with its licences, and its .meta keeps it to the 64-bit Windows editor and players:
#    a .meta fallen back to Unity's defaults would hand every other platform's build a Windows DLL.
PLUGINS="$PLAYTEST/Runtime/Plugins/x86_64"
for file in protokite_vpx.dll libvpx-LICENSE.txt libvpx-PATENTS.txt; do
  [ -f "$PLUGINS/$file" ] || error "$file is missing from Runtime/Plugins/x86_64. Rebuild it with Native~/build-protokite-vpx.sh."
done
# Prints whether a platform is enabled in the DLL's .meta: 1, 0, or "absent".
platform_enabled() {
  awk -v platform="$1" '
    $0 ~ "^    " platform ":[[:space:]]*$" { inside = 1; next }
    inside && /^      enabled:/ { gsub(/[^0-9]/, "", $2); print $2; found = 1; exit }
    inside && /^    [A-Za-z0-9]+:/ { exit }
    END { if (!found) print "absent" }
  ' "$PLUGINS/protokite_vpx.dll.meta" | tr -d '\r'
}
if [ -f "$PLUGINS/protokite_vpx.dll.meta" ]; then
  for expected in "Any 0" "Editor 1" "Win64 1"; do
    set -- $expected
    [ "$(platform_enabled "$1")" = "$2" ] || error "protokite_vpx.dll.meta has $1 enabled '$(platform_enabled "$1")', expected $2. Open the package in Unity so ProtokitePlaytestNativePluginImport sets the platforms, then commit the .meta."
  done
  for other in Win OSXUniversal Linux64 Android iPhone WebGL; do
    case "$(platform_enabled "$other")" in
      0|absent) ;;
      *) error "protokite_vpx.dll.meta enables $other; the DLL is for the 64-bit Windows editor and players only." ;;
    esac
  done
  grep -q "OS: Windows" "$PLUGINS/protokite_vpx.dll.meta" || error "protokite_vpx.dll.meta does not keep the DLL to the Windows editor (OS: Windows)."
else
  error "protokite_vpx.dll.meta is missing, so a git-URL install would leave the DLL out."
fi

if [ "$fail" -eq 0 ]; then
  echo "Protokite Playtest $playtest_version: same version as the Flock SDK, no package dependency on it, not named by its runtime, every file has a .meta."
fi
exit "$fail"
