#!/usr/bin/env bash
# Apply /ship's teardown fix on fix/ship-teardown-reads-the-main-checkout-branch, its case seen failing first, and
# commit it. The push is /ship's. A failed step puts its files back, so a rerun resumes there.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/ship-teardown-reads-the-main-checkout-branch
  [ -f staging/patch_ship.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local pr
  pr="$(bash .claude/scripts/pr-for-branch.sh "$branch" | "${PY[@]}" -c \
    "import json, sys; print(next((r['number'] for r in json.load(sys.stdin) if r['state'] == 'OPEN'), ''))" \
    | tr -d '\r')"
  [ -n "$pr" ] || { echo "no open pull request for $branch" >&2; exit 2; }
  local trailers=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #$pr")
  local files=".claude/scripts/test_ship_landing_method.py .claude/commands/ship.md"
  local case=test_ship_landing_method.TheGrantAndTheInvocationAgree.test_the_teardown_reads_the_branch_before_it_pulls

  "${PY[@]}" staging/patch_ship.py . test
  # The new case must fail on the runbook as it stands, or it pins nothing.
  if (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
    echo "passed before the fix: $case" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  "${PY[@]}" staging/patch_ship.py . runbook
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_ship_landing_method test_command_runbooks \
      test_command_grants test_git_worktree_remove); then
    echo "the suites fail after the fix" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  git add $files
  git commit -q -F - "${trailers[@]}" <<'MSG'
fix(harness): /ship's forked teardown reads the main checkout's branch before it pulls

After ExitWorktree the teardown assumed the main checkout was on main and ran
git pull there, then checked ancestry against HEAD. /branch puts a branch in
place in the main checkout whenever main is dirty, so another session's branch
can be the one sitting there: the pull then ran in that session's tree, the
ancestry check could not confirm main held the merge, and the worktree was
never removed.

The teardown now reads the current branch first. On main it pulls as before;
on any other branch it refreshes main with git fetch origin main:main and
leaves that branch alone. The ancestry check reads main on both paths.

test_the_teardown_reads_the_branch_before_it_pulls failed before the change
and passes after it; apply.sh checks both.

Closes #1249
MSG

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for the /ship teardown" \
    -m "Its fix is the commit before this one." "${trailers[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}

main "$@"
exit
