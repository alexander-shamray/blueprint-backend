#!/usr/bin/env bash
# Runs one code-index hook that is not a hint, `index-hook.sh <event> <hook>.py`, under the first interpreter the
# host has: a raw `py -3.12` exits 127 where there is no Windows launcher, so the root check and the index refresh
# never ran on macOS or Linux. On any non-zero exit this prints the line below instead, and it exits 0 either way,
# since exit 2 under PreToolUse refuses the call. Why: docs/harness-boundaries.md (*Index hint hooks and hook wiring*).
set -u
event="${1:?event}"
export INDEX_HOOK="${2:?hook}"
# A closed set, as run-guard.sh's: the wiring names these two, and what each one's absence costs is said below.
case "$INDEX_HOOK" in
  index-mcp-root.py) lost="no code-index MCP call was checked against the checkout the server reads" ;;
  refresh-index.py) lost="the code index was not refreshed, so its answers may miss this checkout's latest edits" ;;
  *) echo "index-hook.sh: not a hook this launcher runs: $INDEX_HOOK" >&2; exit 0 ;;
esac
# A file, not a substitution: any orphaned child of an interpreter or launcher could hold a pipe open past the hook.
out="$(mktemp)"
trap 'rm -f "$out"' EXIT
# hint-bound.sh's choice, which is run-guard.sh's probe order: `py` is the Windows launcher, which macOS and Linux
# lack, and Windows' `python3` is the Store alias. An interpreter older than `-P` (3.11) fails into the line below.
if command -v py >/dev/null 2>&1; then python=(py -3.12)
elif command -v python3 >/dev/null 2>&1; then python=(python3)
else python=(python)
fi
rc=0
"${python[@]}" -P -c \
  "import os, runpy; runpy.run_path(os.environ['CLAUDE_PROJECT_DIR'] + '/.claude/hooks/' + os.environ['INDEX_HOOK'], run_name='__main__')" \
  >"$out" || rc=$?
if [ "$rc" -eq 0 ]; then
  cat "$out"
else
  printf '{"hookSpecificOutput":{"hookEventName":"%s","additionalContext":"The code-index hook %s did not run (exit %s under %s), so %s."}}\n' \
    "$event" "$INDEX_HOOK" "$rc" "${python[*]}" "$lost"
fi
exit 0
