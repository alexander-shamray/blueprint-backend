#!/usr/bin/env bash
# Step 6 of docs/token-plan.md: move the six runbooks and their tests into .claude/, test, push. NO_PUSH=1 stops before the push.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -d staging/.claude ] || { echo "no staging/ here" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  BRANCH=claude/improve-token-usage-4fjpcs
  git fetch -q origin main
  git merge-base --is-ancestor 73a58dd HEAD || { echo "not the PR branch at or after 73a58dd" >&2; exit 2; }
  for c in ship branch bug-sweep security-sweep validate-blueprint review-branch; do
    cp "staging/.claude/commands/$c.md" ".claude/commands/$c.md"
  done
  git add .claude/commands
  git commit -q -m "feat(commands): the six long commands become runbooks, their argument moved to docs/commands/" -m "Each runbook keeps its frontmatter, every step and its numbering, every fenced command, table and stop condition, and each rule as one sentence citing the section of docs/commands/<name>.md that argues it."
  for p in test_gh_issue_suppresses.py test_command_runbooks.py; do
    cp "staging/.claude/scripts/$p" ".claude/scripts/$p"
  done
  git add .claude/scripts
  git commit -q -m "test(commands): the runbooks keep their ceilings and cite headings that exist, and the sweeps' gate wording is read from runbook and argument together"
  git rm -r -q staging
  git commit -q -m "chore: staging/ goes once its files are in place"
  started=$SECONDS
  "${PY[@]}" -m unittest discover -s .claude/scripts -q
  echo "the .claude/scripts suites took $((SECONDS - started)) s"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -4
  [ "${NO_PUSH:-}" = 1 ] && { echo "NO_PUSH: stopping before the push"; return 0; }
  git push origin HEAD:"$BRANCH"
  echo DONE
}
main "$@"
exit
