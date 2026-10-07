#!/usr/bin/env bash
# Step 4's second PR: move staging/'s two auditor prompts into .claude/agents/, test, push. NO_PUSH=1 stops before the push.
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
fix "feat(agents): bug-auditor keeps its rules, gains the read block and reports JSON, 13.4 KB to 5.5 KB" \
  .claude/agents/bug-auditor.md
fix "feat(agents): security-auditor keeps its rules, gains the read block and reports JSON, 6.4 KB to 3.4 KB" \
  .claude/agents/security-auditor.md
git rm -r -q staging
git commit -q -m "chore: staging/ goes once the prompts are in place"
"${PY[@]}" -m unittest discover -s .claude/scripts -q
"${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
[ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
git log --oneline -4
[ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before the push"; exit 0; }
git push origin HEAD:"$BRANCH"
echo DONE
