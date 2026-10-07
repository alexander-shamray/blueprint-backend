#!/usr/bin/env bash
# Step 5 of docs/token-plan.md: move staging/ into .claude/, one commit per part, test, push. NO_PUSH=1 stops before the push.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
[ -d staging/.claude ] || { echo "no staging/ here" >&2; exit 2; }
[ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
BRANCH=claude/improve-token-usage-4fjpcs
git fetch -q origin main
for p in scripts/sweep-slices.sh scripts/sweep-mark.sh scripts/git-worktree-drop.sh scripts/test_sweep_slices.py; do
  cp "staging/.claude/$p" ".claude/$p"
done
git add .claude/scripts
git commit -q -m "feat(sweeps): sweep-slices.sh owns both partitions and cuts them into 60k-token lists; sweep-mark.sh records a clean sweep" -m "A tracked path no row owns refuses the run, and test_sweep_slices.py runs the helper over this tree, so a new top-level directory fails a suite before a sweep passes over it. git-worktree-drop.sh removes the slice lists with the worktree."
for p in commands/bug-sweep.md commands/security-sweep.md agents/bug-auditor.md agents/security-auditor.md; do
  cp "staging/.claude/$p" ".claude/$p"
done
git add .claude/commands .claude/agents
git commit -q -m "feat(sweeps): round 1 reads one slice per auditor, later rounds follow leads, a clean sweep marks its commit, and verification runs on Sonnet" -m "Where it stops says what a clean later round now means: the leads ran out, not a fresh read of the tree. The security sweep gains the explicit partition the bug sweep had."
git rm -r -q staging
git commit -q -m "chore: staging/ goes once its files are in place"
"${PY[@]}" -m unittest discover -s .claude/scripts -q
"${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
[ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
git log --oneline -4
[ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before the push"; exit 0; }
git push origin HEAD:"$BRANCH"
echo DONE
