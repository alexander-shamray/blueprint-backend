#!/usr/bin/env bash
# Apply the --dispatched turn rule on fix/dispatched-session-keeps-its-turn, its case seen failing before the rule,
# and commit it. The push is /ship's. A failed step puts its files back, so a rerun resumes at that step.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/dispatched-session-keeps-its-turn
  [ -f staging/patch_dispatched.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local pr
  pr="$(bash .claude/scripts/pr-for-branch.sh "$branch" | "${PY[@]}" -c \
    "import json, sys; print(next((r['number'] for r in json.load(sys.stdin) if r['state'] == 'OPEN'), ''))" \
    | tr -d '\r')"
  [ -n "$pr" ] || { echo "no open pull request for $branch" >&2; exit 2; }
  TRAILERS=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01QAU7oLQm5rZyEgaTxZSs5c" --trailer "PR: #$pr")
  local files=".claude/scripts/test_ship_dispatched.py .claude/commands/ship.md"
  local case=test_ship_dispatched.TheDispatchedTokenChangesOnlyWhatTheSpecLists.test_a_step_status_is_not_a_reply

  if ! done_already "keeps its turn until it writes its result"; then
    "${PY[@]}" staging/patch_dispatched.py . test
    # The new case must fail on the runbook as it stands, or it pins nothing.
    if (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
      echo "passed before the fix: $case" >&2; git checkout -q HEAD -- $files; exit 1
    fi
    "${PY[@]}" staging/patch_dispatched.py . code
    if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_ship_dispatched test_command_runbooks \
        test_command_grants test_ship_landing_method test_pr_helpers test_system_sleep test_git_worktree_remove \
        test_git_argv_guard test_dispatch_run test_dispatch_board); then
      echo "the suites fail after the rule" >&2; git checkout -q HEAD -- $files; exit 1
    fi
    commit "fix(harness): a dispatched /ship session keeps its turn until it writes its result" \
      "In the dispatch run 20261101-195321 group M1 committed, wrote a step report ending in a status table and the apply line, and its turn ended. A claude -p session exits when its turn ends, so it wrote no result file and the dispatcher read it as handed back. The owner's standing rules close every step with that table and line, and every session here loads them; headless, nothing answers a step report, so the first one is the session's last message, and no dispatched group can reach a merge." \
      "The --dispatched clause now says the session keeps its turn until the result is written: a step's status is not a reply, carries no status table and no apply line, and the next step follows in the same turn. It also says each shell read is one command the grants name, since a headless session refuses rather than asks about one they do not, as a compound read in the same session was. docs/commands/ship.md carries the argument in this branch's earlier commit." \
      "test_a_step_status_is_not_a_reply pins the clause's text. It failed before the change and passes after it; apply.sh checks both." \
      "Closes #1698"
  fi

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for the dispatched turn rule" \
    -m "Its fix is the commit before this one." "${TRAILERS[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -4
  echo DONE
}

# Whether a commit on this branch already carries this phrase in its subject.
done_already() {
  git log --format=%s origin/main..HEAD | tr -d '\r' | grep -qF "$1"
}

# commit <subject> <body paragraphs...>, each paragraph wrapped at 80 as /commit's bodies are.
commit() {
  local subject=$1 message
  shift
  message="$("${PY[@]}" -c 'import sys, textwrap
print(sys.argv[1] + "\n\n" + "\n\n".join(textwrap.fill(p, 80) for p in sys.argv[2:]))' "$subject" "$@" | tr -d '\r')"
  git add .claude/scripts/test_ship_dispatched.py .claude/commands/ship.md
  git commit -q -F - "${TRAILERS[@]}" <<<"$message"
}

main "$@"
exit
