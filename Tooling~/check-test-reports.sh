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
