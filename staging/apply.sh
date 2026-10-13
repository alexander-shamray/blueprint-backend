#!/usr/bin/env bash
# Take the issue number out of test_hook_timeouts.py's comments, rerun the suites and the comment gate, commit.
# Run on fix/hook-launcher-timeout; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_comments.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = fix/hook-launcher-timeout ] || { echo "not on fix/hook-launcher-timeout" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01C6ngFTLvF9HrcSUdqw7mGT"
  "${PY[@]}" staging/patch_comments.py .
  git rm -r -q staging
  git add .claude/scripts/test_hook_timeouts.py
  git commit -q -m "fix(harness): test_hook_timeouts comments name no issue number" -m "The comment gate refuses a comment that carries an issue number, and the previous commit's two did. Wording only; the suite's cases are unchanged." -m "$TRAILER"
  (cd .claude/scripts && "${PY[@]}" -m unittest test_hook_timeouts test_harness_denies)
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -4
  echo DONE
}
main "$@"
exit
