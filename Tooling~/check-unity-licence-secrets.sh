#!/usr/bin/env bash
# Cheap triage before a Unity image is pulled: the three licence secrets are present and shaped the way Unity takes them.
# Activation failure retries three times with backoff, so a secret that is simply absent or has a stray newline otherwise
# costs minutes and reports itself as a login failure. Values are never printed, only their shape.
# Reads UNITY_EMAIL, UNITY_PASSWORD and UNITY_LICENSE from the environment.
# Usage: bash "Tooling~/check-unity-licence-secrets.sh"
set -euo pipefail

fail=0

for name in UNITY_EMAIL UNITY_PASSWORD UNITY_LICENSE; do
  if [ -z "${!name:-}" ]; then
    echo "::error::$name is empty or unset. Add it under Settings > Secrets and variables > Actions."
    fail=1
  fi
done

# Only the single-line secrets: a .ulf legitimately ends with a newline.
for name in UNITY_EMAIL UNITY_PASSWORD; do
  value="${!name:-}"
  trimmed=$(printf '%s' "$value" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
  if [ -n "$value" ] && [ "$value" != "$trimmed" ]; then
    echo "::error::$name has leading or trailing whitespace, which Unity sends verbatim and rejects. Re-add the secret without the stray character."
    fail=1
  fi
done

case "${UNITY_LICENSE:-}" in
  *"<License"*) ;;
  "") ;;
  *) echo "::error::UNITY_LICENSE does not contain a <License> element, so it is not the contents of a .ulf file. Paste the whole of Unity_lic.ulf, opening tag to closing tag."; fail=1 ;;
esac

if [ "$fail" -ne 0 ]; then
  echo "::error::Fix the secrets above before this job can activate a licence."
  exit 1
fi

echo "All three licence secrets are present and shaped correctly."
