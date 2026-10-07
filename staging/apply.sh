#!/usr/bin/env bash
# Issue #600: move staging/'s faster suites into .claude/scripts, test, push. NO_PUSH=1 stops before the push.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
[ -d staging/.claude ] || { echo "no staging/ here" >&2; exit 2; }
[ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
BRANCH=claude/improve-token-usage-4fjpcs
git fetch -q origin main
for p in test_git_worktree_remove.py test_git_rebase_onto_main.py test_sweep_slices.py; do
  cp "staging/.claude/scripts/$p" ".claude/scripts/$p"
done
git add .claude/scripts
git commit -q -m "perf(scripts): the rebase fixture is built once and copied per test, the worktree-remove tests run a copy with a shorter bound and grace, and the sweep tests use a small repository" -m "Closes #600"
git rm -r -q staging
git commit -q -m "chore: staging/ goes once its files are in place"
started=$SECONDS
"${PY[@]}" -m unittest discover -s .claude/scripts -q
echo "the .claude/scripts suites took $((SECONDS - started)) s"
"${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
[ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
git log --oneline -3
[ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before the push"; exit 0; }
git push origin HEAD:"$BRANCH"
echo DONE
