#!/usr/bin/env bash
# Patch .claude/commands/{branch,ship}.md for the TODO.md row, test, commit. Run on chore/todo-row-at-branch; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_commands.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = chore/todo-row-at-branch ] || { echo "not on chore/todo-row-at-branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  "${PY[@]}" staging/patch_commands.py .
  git add .claude/commands
  git commit -q -m "chore(commands): /branch adds the TODO.md In-progress row and /ship step 6 deletes it" -m "The row is written before EnterWorktree because the worktree session is refused that copy (the contract's section 6). The ship runbook's teardown deletes it from the main checkout after the pull." -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E95hGmD5dbjihKDU5o4Ytc"
  git rm -r -q staging
  git commit -q -m "chore: staging/ goes once its files are in place" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E95hGmD5dbjihKDU5o4Ytc"
  "${PY[@]}" -m unittest .claude/scripts/test_command_runbooks.py
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
