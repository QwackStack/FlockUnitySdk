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
# The store purchase tests: the rules (EditMode) and Unity IAP 5's own store (PlayMode), compiled only with Unity IAP 5 installed.
store_rules_class="Flock.Tests.Editor.FlockStoreTests"
store_unity_iap_class="Flock.Tests.PlayMode.FlockStoreUnityIapTests"

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

# The test step says only that Unity exited 2, and its log needs a sign-in: everything that makes Unity exit 2 is named here
# and on the run's page. That is a failed test, an inconclusive one (an Assume that did not hold: Unity counts it as failed,
# so a test that cannot run somewhere must Assert.Ignore instead), and a fixture or assembly that failed outside any one test
# (its one-time set-up or tear-down). GitHub shows ten errors a step, so past ten the tenth annotation names the rest.
# $readable is split on purpose: the report paths hold no spaces.
if [ -n "$readable" ] && ! python3 - "$leg" $readable <<'PY'
import os
import sys
import xml.etree.ElementTree as ET

def annotate(report, title, text):
    text = text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    print(f"::error file={report},title={title} ({leg})::{text}")

def message_of(node):
    return (node.findtext("failure/message") or node.findtext("reason/message") or "").strip()

leg, reports = sys.argv[1], sys.argv[2:]
failed = []
for report in reports:
    mode = os.path.basename(report).split("-")[0]
    root = ET.parse(report).getroot()
    for case in root.iter("test-case"):
        if case.get("result") in ("Failed", "Inconclusive"):
            failed.append((report, mode, case.get("result").lower(), case.get("fullname"), message_of(case)))
    # A suite that failed with none of its tests failing failed on its own: its one-time set-up or tear-down. The suites above
    # it fail as well, marked site="Child", and are left out so one failure is named once.
    for suite in root.iter("test-suite"):
        if suite.get("result") == "Failed" and suite.get("site") != "Child" \
                and not any(case.get("result") in ("Failed", "Inconclusive") for case in suite.iter("test-case")):
            failed.append((report, mode, "failed outside its tests", suite.get("fullname"), message_of(suite)))
for report, mode, how, name, message in failed:
    print(f"[{leg}] {mode} test {how}: {name}\n    " + message.replace("\n", "\n    "))
shown = failed if len(failed) <= 10 else failed[:9]
for report, mode, how, name, message in shown:
    annotate(report, f"{mode} test {how}", f"{name}: {message[:2000]}")
if len(failed) > len(shown):
    rest = failed[len(shown):]
    annotate(rest[0][0], f"{len(rest)} more tests failed or were inconclusive", "\n".join(f"{mode} ({how}): {name}" for _, mode, how, name, _ in rest))
print(f"[{leg}] {len(failed)} tests failed or were inconclusive.")
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

# The store purchase tests compile only with Unity IAP 5 installed, so they too can pass by absence.
for check in "editmode:$store_rules_class" "playmode:$store_unity_iap_class"; do
  mode="${check%%:*}"
  class="${check#*:}"
  report="$results/${mode}-results.xml"
  [ -f "$report" ] || continue
  ran=$(grep -c "classname=\"$class\"" "$report" || true)
  if [ "$ran" -eq 0 ]; then
    echo "::error file=$report::[$leg] No store purchase test ran ($class). Unity IAP 5 is not installed in this leg, or its version define did not match, so the store purchase files compiled out and passed by absence."
    bad=$((bad + 1))
  else
    echo "[$leg] $ran store purchase tests ran ($class)."
  fi
done

[ "$bad" -eq 0 ]
