#!/usr/bin/env bash
# Drop the Agent grant from .claude/commands/validate-blueprint.md, run every helper suite, commit.
# Run on chore/validate-blueprint-dispatches; the push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(git rev-parse --show-toplevel)"
  [ -f staging/patch_validate_blueprint.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = chore/validate-blueprint-dispatches ] || { echo "not on chore/validate-blueprint-dispatches" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean" >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  TRAILER="Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01QyEjmj8YqVfFNhy5QLW8P1"
  "${PY[@]}" staging/patch_validate_blueprint.py .
  git add .claude/commands
  git commit -q -m "fix(commands): /validate-blueprint grants no agent type, so it denies none (CI: review-helpers)" -m "test_command_grants requires a command that grants Agent(<type>) to deny every other profile, and a frontmatter deny holds to the end of the turn, so denying branch-reviewer here would stop /ship's own review after its step 2. general-purpose needs no grant: /ship step 6 spawns it with none. The test's subject is the grants, so it needs no new case." -m "$TRAILER"
  git rm -r -q staging
  git commit -q -m "chore: staging/ goes once its files are in place" -m "$TRAILER"
  (cd .claude/scripts && "${PY[@]}" -m unittest test_command_grants test_command_runbooks)
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after commit" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}
main "$@"
exit
