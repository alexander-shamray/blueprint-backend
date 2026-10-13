#!/usr/bin/env bash
# Raise the codebase-index example's hook timeouts to match the installed block, rerun the suites, commit.
# Run on fix/hook-launcher-timeout; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_example.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = fix/hook-launcher-timeout ] || { echo "not on fix/hook-launcher-timeout" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C6ngFTLvF9HrcSUdqw7mGT"
  (cd .claude/scripts && ! "${PY[@]}" -m unittest test_refresh_index.DocumentedConfiguration >/dev/null 2>&1) ||
    { echo "DocumentedConfiguration passes before the fix, so it proves nothing" >&2; exit 1; }
  "${PY[@]}" staging/patch_example.py .
  git rm -r -q staging
  git add .claude/skills/codebase-index/examples/hooks/settings.json
  git commit -q -m "fix(harness): the codebase-index example carries the installed hooks' 15 s timeout" -m "test_refresh_index.DocumentedConfiguration holds the example equal to the installed refresh-index block, and the previous commit moved only the installed one, so CI's review-helpers and edit-target-guard jobs failed on the pair. The test failed before this edit and passes after it." -m "$TRAILER"
  (cd .claude/scripts && "${PY[@]}" -m unittest test_refresh_index test_hook_timeouts test_harness_denies)
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
