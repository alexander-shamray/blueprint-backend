#!/usr/bin/env bash
# Patch .claude/commands/validate-blueprint.md so its main session only dispatches, test, commit.
# Run on chore/validate-blueprint-dispatches; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_validate_blueprint.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = chore/validate-blueprint-dispatches ] || { echo "not on chore/validate-blueprint-dispatches" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  "${PY[@]}" staging/patch_validate_blueprint.py .
  git add .claude/commands
  git commit -q -m "chore(commands): /validate-blueprint's main session dispatches the passes and holds only their JSON" -m "One audit spent 40.6M of 154M tokens, its main session averaging 477k a call, with implementers spawned inside it. The runbook now dispatches one general-purpose agent per group of checks, reads no chapter whole, makes no fix and runs once; the caller fixes after /clear, or as /ship step 2 does. docs/commands/validate-blueprint.md, Dispatch, holds the argument." -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QyEjmj8YqVfFNhy5QLW8P1"
  git rm -r -q staging
  git commit -q -m "chore: staging/ goes once its files are in place" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QyEjmj8YqVfFNhy5QLW8P1"
  "${PY[@]}" -m unittest .claude/scripts/test_command_runbooks.py
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
