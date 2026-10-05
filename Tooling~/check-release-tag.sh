#!/usr/bin/env bash
# Checks a release tag: v<version>, where <version> is what package.json carries (1.69.0, or a prerelease such as 1.69.0-beta.1).
# The Playtesting tab in Flock > Settings downloads ProtokitePlaytest-<version>.unitypackage from the release tagged v<version>, so a
# release under a tag of any other shape (v.1.68.0 shipped once) cannot be installed from it.
# Usage: bash "Tooling~/check-release-tag.sh" <tag> [repository root]   (the root defaults to the current folder)
set -uo pipefail

TAG="${1:-}"
ROOT="${2:-.}"

if [ -z "$TAG" ]; then
  echo "::error::No tag given. Usage: check-release-tag.sh <tag> [repository root]"
  exit 1
fi

if ! printf '%s' "$TAG" | grep -qE '^v[0-9]+[.][0-9]+[.][0-9]+(-[0-9A-Za-z.-]+)?$'; then
  echo "::error::Tag '$TAG' is not v<version>, such as v1.69.0. The Playtesting tab in Flock > Settings downloads the playtest from the release tagged v<the Flock SDK's version>, so it cannot install from a release under this tag. Delete the tag and push v<version>, or edit the release to use it."
  exit 1
fi

# The path goes in as an argument, never inside the script text, so every shell hands node a path it can open.
if ! VERSION=$(node -e '
  const value = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).version;
  if (typeof value !== "string" || value === "") process.exit(2);
  process.stdout.write(value);
' "$ROOT/package.json" 2>/dev/null); then
  echo "::error::Could not read the version in $ROOT/package.json."
  exit 1
fi

if [ "${TAG#v}" != "$VERSION" ]; then
  echo "::error::Tag $TAG does not match package.json, which is at $VERSION. Tag the commit that carries the version: v$VERSION."
  exit 1
fi

echo "Tag $TAG is v<the package version>, $VERSION."
