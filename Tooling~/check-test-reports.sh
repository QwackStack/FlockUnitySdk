#!/usr/bin/env bash
# Checks one CI leg's Unity test reports: every mode wrote a report, every test assembly the leg installs ran at least one
# test, and the netcode tests ran. A green run that executed nothing looks like a green run that executed everything unless
# these are checked, and a whole assembly that never compiled (the playtest's, the netcode files') hides behind a healthy
# total.
#
#   check-test-reports.sh <results folder> <leg name>
#
# The reports are the NUnit 3 files game-ci's test runner writes, one per mode. An assembly's suite carries its dll name
# (type="Assembly", or type="SetUpFixture" when the assembly has a one-time setup), and its testcasecount.
set -euo pipefail

results="${1:?results folder}"
leg="${2:-unnamed leg}"

# Mode -> the test assemblies that must have run in it. Edit these with the assemblies, never loosen the check.
editmode_assemblies="Flock.Tests.Editor Protokite.Playtest.Tests.Editor"
playmode_assemblies="Flock.Tests.PlayMode Protokite.Playtest.Tests.PlayMode"
# The netcode tests: one partial class across the netcode PlayMode files, compiled only with Netcode for GameObjects installed.
netcode_class="Flock.Tests.PlayMode.FlockNetcodeTests"

bad=0
readable=""

for mode in editmode playmode; do
  report="$results/${mode}-results.xml"
  if [ ! -f "$report" ]; then
    echo "::error::[$leg] No $mode report at $report, so that mode did not run. Read the test step for the real failure."
    bad=$((bad + 1))
    continue
  fi

  total=$(grep -oE '<test-run [^>]*total="[0-9]+"' "$report" | head -1 | grep -oE 'total="[0-9]+"' | grep -oE '[0-9]+' || true)
  if [ -z "$total" ]; then
    echo "::error file=$report::[$leg] Could not read a total from this report. If the format changed, update this script with it."
    bad=$((bad + 1))
    continue
  fi
  if [ "$total" -eq 0 ]; then
    echo "::error file=$report::[$leg] The $mode run reported 0 tests, which passes without testing anything. Usually a package stopped being listed under 'testables' in the generated manifest, so UNITY_INCLUDE_TESTS was never defined and its test assemblies were skipped."
    bad=$((bad + 1))
    continue
  fi
  echo "[$leg] $total $mode tests ran."
  readable="$readable $report"

  assemblies="$editmode_assemblies"
  [ "$mode" = "playmode" ] && assemblies="$playmode_assemblies"
  for assembly in $assemblies; do
    suite=$(grep -oE "<test-suite type=\"(Assembly|SetUpFixture)\"[^>]*name=\"$assembly\.dll\"[^>]*>" "$report" | head -1 || true)
    count=$(echo "$suite" | grep -oE 'testcasecount="[0-9]+"' | grep -oE '[0-9]+' || true)
    if [ -z "$count" ] || [ "$count" -eq 0 ]; then
      echo "::error file=$report::[$leg] The $assembly assembly ran no test in $mode. Either it did not compile (a package it needs is not installed in this leg, or not listed under 'testables') or its tests were filtered out."
      bad=$((bad + 1))
    else
      echo "[$leg] $assembly: $count tests in $mode."
    fi
  done
done

# The test step says only that Unity exited 2, and its log needs a sign-in: every failed test is named here and on the run's
# page. GitHub shows ten errors a step, so past ten the tenth annotation names the rest.
# $readable is split on purpose: the report paths hold no spaces.
if [ -n "$readable" ] && ! python3 - "$leg" $readable <<'PY'
import os
import sys
import xml.etree.ElementTree as ET

def annotate(report, title, text):
    text = text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    print(f"::error file={report},title={title} ({leg})::{text}")

leg, reports = sys.argv[1], sys.argv[2:]
failed = []
for report in reports:
    mode = os.path.basename(report).split("-")[0]
    for case in ET.parse(report).getroot().iter("test-case"):
        if case.get("result") == "Failed":
            failed.append((report, mode, case.get("fullname"), (case.findtext("failure/message") or "").strip()))
for report, mode, name, message in failed:
    print(f"[{leg}] {mode} test failed: {name}\n    " + message.replace("\n", "\n    "))
shown = failed if len(failed) <= 10 else failed[:9]
for report, mode, name, message in shown:
    annotate(report, f"{mode} test failed", f"{name}: {message[:2000]}")
if len(failed) > len(shown):
    rest = failed[len(shown):]
    annotate(rest[0][0], f"{len(rest)} more tests failed", "\n".join(f"{mode}: {name}" for _, mode, name, _ in rest))
print(f"[{leg}] {len(failed)} tests failed.")
sys.exit(1 if failed else 0)
PY
then
  bad=$((bad + 1))
fi

playmode="$results/playmode-results.xml"
if [ -f "$playmode" ]; then
  netcode=$(grep -c "classname=\"$netcode_class\"" "$playmode" || true)
  if [ "$netcode" -eq 0 ]; then
    echo "::error file=$playmode::[$leg] No netcode test ran ($netcode_class). Netcode for GameObjects is not installed in this leg, or its version define did not match, so the netcode PlayMode files compiled out and passed by absence."
    bad=$((bad + 1))
  else
    echo "[$leg] $netcode netcode tests ran."
  fi
fi

[ "$bad" -eq 0 ]
