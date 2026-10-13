#!/usr/bin/env bash
# Add the hook-timeout suite, prove it red, raise the five 5 s hook timeouts in .claude/settings.json, commit.
# Run on fix/hook-launcher-timeout; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_hook_timeouts.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = fix/hook-launcher-timeout ] || { echo "not on fix/hook-launcher-timeout" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C6ngFTLvF9HrcSUdqw7mGT"
  cp staging/test_hook_timeouts.py .claude/scripts/test_hook_timeouts.py
  if (cd .claude/scripts && "${PY[@]}" -m unittest test_hook_timeouts >/dev/null 2>&1); then
    echo "the new suite passes before the fix, so it proves nothing" >&2; exit 1
  fi
  "${PY[@]}" staging/patch_hook_timeouts.py .
  (cd .claude/scripts && "${PY[@]}" -m unittest test_hook_timeouts)
  git add .claude/settings.json .claude/scripts/test_hook_timeouts.py
  git commit -q -m "fix(harness): py-launched hooks time out at 15 s, not 5 s (#989)" -m "A py launcher start measured 2.9 s on a busy machine, and a UserPromptSubmit hook that times out has its output discarded, so index-query-hint's hint was silently dropped. The five hooks that carried a 5 s timeout now carry 15 s; the three guards carry none. test_hook_timeouts failed on the 5 s settings and holds every py hook that sets a timeout at the floor." -m "$TRAILER"
  git rm -r -q staging
  git commit -q -m "chore: staging/ goes once its files are in place" -m "$TRAILER"
  (cd .claude/scripts && "${PY[@]}" -m unittest test_hook_timeouts test_harness_denies)
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
