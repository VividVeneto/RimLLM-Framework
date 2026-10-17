#!/bin/bash
set -euo pipefail
dotnet /probe/out/Probe.dll unavailable
dbus-run-session -- bash -euo pipefail -c '
  printf "%s" "isolated-test-only-password" | gnome-keyring-daemon --unlock --components=secrets
  dotnet /probe/out/Probe.dll
  shared="/tmp/rimllm-native-shared-$$"
  dotnet /probe/out/Probe.dll create "$shared" > /tmp/rimllm-a & a=$!
  dotnet /probe/out/Probe.dll create "$shared" > /tmp/rimllm-b & b=$!
  wait "$a"
  wait "$b"
  cmp /tmp/rimllm-a /tmp/rimllm-b
  dotnet /probe/out/Probe.dll delete "$shared"
  echo "PASS: two processes resolve the same keyring entry and storage key."
'
