#!/usr/bin/env bash
# Runs one index hint hook, `hint-bound.sh <event> <hook>.py`, under a bound shorter than the harness's own: a hook
# the harness kills has its output discarded, so a stalled launcher left no sign the hint had not run. Past the
# bound, or on any non-zero exit, this prints the line below instead, and it exits 0 either way because exit 2
# erases the prompt. Why the bound is here: docs/harness-boundaries.md (*Index hint hooks and hook wiring*).
set -u
HINT_BOUND_SECONDS="${HINT_BOUND_SECONDS:-10}"
event="${1:?event}"
export HINT_HOOK="${2:?hook}"
# A file, not a substitution: a launcher's orphaned child would hold a pipe open and keep this waiting.
out="$(mktemp)"
trap 'rm -f "$out"' EXIT
rc=0
timeout "$HINT_BOUND_SECONDS" py -3.12 -P -c \
  "import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR'] + '/.claude/hooks/' + os.environ['HINT_HOOK'], run_name='__main__')" \
  >"$out" || rc=$?
if [ "$rc" -eq 0 ]; then
  cat "$out"
else
  printf '{"hookSpecificOutput":{"hookEventName":"%s","additionalContext":"The code-index hint hook %s did not run (exit %s after at most %s s), so no hint here says nothing about whether one applies."}}\n' \
    "$event" "$HINT_HOOK" "$rc" "$HINT_BOUND_SECONDS"
fi
exit 0
