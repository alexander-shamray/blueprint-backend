#!/usr/bin/env bash
# Install hint-bound.sh, its suite and the settings wiring under .claude/, rerun the suites, commit.
# Run on fix/hook-timeout-marker with docs/harness-boundaries.md already committed; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_harness.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = fix/hook-timeout-marker ] || { echo "not on fix/hook-timeout-marker" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C6ngFTLvF9HrcSUdqw7mGT"
  cp staging/test_hint_bound.py .claude/scripts/test_hint_bound.py
  (cd .claude/scripts && ! "${PY[@]}" -m unittest test_hint_bound.HintHooksAreWired >/dev/null 2>&1) ||
    { echo "the wiring case passes before the fix, so it proves nothing" >&2; exit 1; }
  cp staging/hint-bound.sh .claude/hooks/hint-bound.sh
  chmod +x .claude/hooks/hint-bound.sh
  "${PY[@]}" staging/patch_harness.py .
  git rm -r -q staging
  git add .claude/hooks/hint-bound.sh .claude/scripts/test_hint_bound.py .claude/scripts/test_hook_timeouts.py .claude/scripts/test_index_query_hint.py .claude/scripts/test_index_search_hint.py .claude/settings.json
  git commit -q -m "fix(harness): a hint hook that stalls says so, under a bound below the harness's" -m "The harness discards the output of a hook it times out, so a stalled py launcher made index-query-hint and index-search-hint indistinguishable from a hint that did not apply. hint-bound.sh runs each under a 10 s bound, below the 15 s settings timeout, and prints a line naming the hook when it stalls or fails; it exits 0 either way. test_hint_bound failed on the wiring before this edit and its mutation, a wrapper that never prints the line, fails the stall and failure cases. test_hook_timeouts counts the wrapper as a py launcher so its floor still covers both hints, and the two hint suites run the registered wrapper command, where a missing hook file now leaves the line and not silence." -m "$TRAILER"
  (cd .claude/scripts && "${PY[@]}" -m unittest test_hint_bound test_hook_timeouts test_harness_denies test_index_query_hint test_index_search_hint test_index_mcp_root test_refresh_index test_command_grants)
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
