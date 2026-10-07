#!/usr/bin/env bash
# PR #597 cold review: move staging/'s auditor fixes into .claude/agents/, test, push. NO_PUSH=1 stops before the push.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
[ -d staging/.claude ] || { echo "no staging/ here" >&2; exit 2; }
[ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
BRANCH=claude/improve-token-usage-4fjpcs
git fetch -q origin main
cp staging/.claude/agents/bug-auditor.md .claude/agents/bug-auditor.md
cp staging/.claude/agents/security-auditor.md .claude/agents/security-auditor.md
git add .claude/agents/bug-auditor.md .claude/agents/security-auditor.md
git commit -q -m "fix: both auditors name the root verbatim in their JSON and say why a verdict quotes the file, not the candidate (review: R1, R2)"
git rm -r -q staging
git commit -q -m "chore: staging/ goes once its fix is in place"
"${PY[@]}" -m unittest discover -s .claude/scripts -q
"${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
[ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
git log --oneline -3
[ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before the push"; exit 0; }
git push origin HEAD:"$BRANCH"
echo DONE
