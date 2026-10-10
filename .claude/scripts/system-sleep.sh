#!/usr/bin/env bash
# Suspend this computer: /ship's optional last step, run only when the caller asked for it by name. A prefix grant on
# `rundll32` would run any DLL export, so the command is fixed here and the only argument is the flag that proves
# nothing was suspended. Windows hibernates instead of sleeping where hibernation is on (`powercfg /a` says which).
set -euo pipefail
dry=false
case "$#:${1-}" in
  0:) ;;
  1:--dry-run) dry=true ;;
  *) echo "usage: system-sleep.sh [--dry-run]" >&2; exit 2 ;;
esac
command -v rundll32.exe >/dev/null 2>&1 ||
  { echo "rundll32.exe not found: this helper suspends Windows only" >&2; exit 3; }
if $dry; then
  echo "would run: rundll32.exe powrprof.dll,SetSuspendState 0,1,0"
  exit 0
fi
exec rundll32.exe powrprof.dll,SetSuspendState 0,1,0
