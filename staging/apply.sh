#!/usr/bin/env bash
# PR #596 round 1: move staging/'s five .claude/ fixes into place, one commit per finding, test, push.
# Run from the root of a worktree on the PR branch. NO_PUSH=1 stops before the push.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
[ -d staging/.claude ] || { echo "no staging/ here" >&2; exit 2; }
[ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
BRANCH=claude/improve-token-usage-4fjpcs
git fetch -q origin main

fix() {  # fix <message> <path>...
  local msg="$1"; shift
  for p in "$@"; do cp "staging/$p" "$p"; done
  git add -- "$@"
  git commit -q -m "$msg"
}
fix "fix: branch-reviewer greps migrations, plans and the closed records even when touched, and names a skipped check in its JSON (review: L3, L4)" \
  .claude/agents/branch-reviewer.md
fix "fix: ship.md says one clean round ends the review because deep or large fixes already earned a full pass (review: L5)" \
  .claude/commands/ship.md
fix "fix: test_harness_denies' docstring says the commands grant running the helpers, not the helpers holding grants (review: N7)" \
  .claude/scripts/test_harness_denies.py
fix "fix: test_pr_helpers.py ends in one newline after two blank lines (review: N8)" \
  .claude/scripts/test_pr_helpers.py
git rm -r -q staging
git commit -q -m "chore: staging/ goes once its fixes are in place"

"${PY[@]}" -m unittest discover -s .claude/scripts -q
"${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
[ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
git log --oneline -5
[ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before the push"; exit 0; }
git push origin HEAD:"$BRANCH"
echo DONE
