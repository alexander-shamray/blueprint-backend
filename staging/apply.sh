#!/usr/bin/env bash
# Give the two hint suites a `py` shim for hint-bound.sh, rerun the suites and the comment gate, commit.
# Run on fix/hook-timeout-marker with the tree clean; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_ci.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = fix/hook-timeout-marker ] || { echo "not on fix/hook-timeout-marker" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C6ngFTLvF9HrcSUdqw7mGT"
  "${PY[@]}" staging/patch_ci.py .
  git rm -r -q staging
  git add .claude/scripts/review_helpers.py .claude/scripts/test_index_query_hint.py .claude/scripts/test_index_search_hint.py
  git commit -q -m "fix(harness): the hint suites run hint-bound.sh with a py on PATH" -m "hint-bound.sh starts its hook as py -3.12, and a Linux runner has no py, so the wrapper exited 127 and left its did-not-run line where the two hint suites expected the hook's answer: four failures in CI's review-helpers job on the previous commit. The suites now put a py on PATH that runs their own interpreter. With py removed from PATH the four cases failed on the previous commit's suites exactly as in CI, and pass with the shim." -m "$TRAILER"
  (cd .claude/scripts && "${PY[@]}" -m unittest test_hint_bound test_hook_timeouts test_harness_denies test_index_query_hint test_index_search_hint test_index_mcp_root test_refresh_index test_command_grants)
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
