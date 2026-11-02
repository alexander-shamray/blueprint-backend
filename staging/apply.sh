#!/usr/bin/env bash
# Land the 1711 fixes on fix/dispatch-1711-lows from staging/, each step's cases seen failing
# before its code and passing after, and commit each. The push is /ship's. A failed step puts its files back, so a
# rerun resumes at that step. dry_run.sh sources this file for STEPS, step, land and all_landed.
set -euo pipefail

# Every listed case must fail before its code and pass after; a name led by ~ is a case that holds on both sides.
# Before, a case fails by an assertion, or by an error EXPECT names: any other error is a broken case, not a red one.
CASES_PY='
import re, sys, unittest
mode, expect = sys.argv[1], sys.argv[2]
held = {n[1:] for n in sys.argv[3:] if n.startswith("~")}
names = list(dict.fromkeys(n.lstrip("~") for n in sys.argv[3:]))
outcome, why, errors = {}, {}, {}
class Record(unittest.TestResult):
    def addSuccess(self, test):
        outcome.setdefault(test.id(), "pass")
    def addExpectedFailure(self, test, err):
        outcome.setdefault(test.id(), "pass")
    def addUnexpectedSuccess(self, test):
        outcome.setdefault(test.id(), "pass")
    def addSkip(self, test, reason):
        outcome[test.id()] = "skip"
    def addFailure(self, test, err):
        self.addSubTest(test, None, err)
    addError = addFailure
    def addSubTest(self, test, subtest, err):
        if err is not None:
            outcome[test.id()], why[test.id()] = "fail", self._exc_info_to_string(err, test)
            if not issubclass(err[0], test.failureException):
                errors.setdefault(test.id(), []).append(f"{err[0].__name__}: {err[1]}")
unittest.defaultTestLoader.loadTestsFromNames(names).run(Record())
want = "fail" if mode == "before" else "pass"
cases = sorted(k for k in outcome if k not in held)
wrong = [(k, outcome[k]) for k in cases if outcome[k] != want]
if mode == "before":
    wrong += [(k, "an error EXPECT does not name") for k in cases if outcome[k] == "fail" and not all(
        expect and re.search(expect, text, re.MULTILINE) for text in errors.get(k, []))]
wrong += [(k + " (held)", outcome.get(k, "absent")) for k in sorted(held) if outcome.get(k) != "pass"]
print(f"{mode}: {len(cases) - len(wrong)} of {len(cases)} cases {want}, {len(held)} held")
for k, state in wrong:
    print(f"  {state}: {k}", why.get(k, ""), sep="\n", file=sys.stderr)
sys.exit(1 if wrong or not (cases or held) else 0)
'
# The suites run after every step: the precedents and every suite of the /dispatch helpers.
SUITES="test_command_grants test_ship_landing_method test_command_runbooks test_dispatch_board test_dispatch_run"
SUITES+=" test_watch_session test_issue_decisions test_ship_dispatched test_git_worktree_remove"
STEPS="1 2 3"

# step <n> sets the step's subject, guard phrase, files, cases, the one error its before may show, suites run only
# after, and its commit body.
step() {
  local s=.claude/scripts c=.claude/commands
  MODULES=""
  EXPECT=""
  TESTS=""
  case $1 in
    1)
      SUBJECT="fix(harness): git-worktree-remove.sh removes a worktree holding a path past Windows' 260 characters"
      PHRASE="removes a worktree holding a path past Windows' 260 characters"
      TESTS="$s/test_git_worktree_remove.py"
      CODE="$s/git-worktree-remove.sh"
      CASES="test_git_worktree_remove.RemoveShape.test_a_worktree_holding_a_path_past_260_characters_is_removed"
      MODULES="test_git_worktree_remove"
      BODY=("At S1's landing in run 20261101-195321, removing .claude/worktrees/broker-read-grants failed with Filename too long and exited 255. git had already unregistered the worktree, so the directory was left behind, no longer a worktree, for someone to delete by hand."
        "The helper now runs the removal with core.longpaths set for that one command. The new case commits a file whose checked-out path passes 260 characters, using only git commands that ask for long paths themselves, so the only removal tested is the helper's own. It failed before and passes after.") ;;
    2)
      SUBJECT="fix(harness): the commands that deny every other agent type deny the fork type too"
      PHRASE="deny every other agent type deny the fork type too"
      TESTS="$s/test_command_grants.py"
      CODE="$c/dispatch.md $c/bug-sweep.md $c/security-sweep.md $c/team.md"
      local g=test_command_grants
      CASES="$g.TheDispatcherSpawnsSessionsNotAgents.test_dispatch_grants_no_agent_and_denies_every_profile"
      CASES+=" $g.AnAgentIsGrantedByExactType.test_every_other_exact_agent_grant_denies_every_other_profile"
      MODULES="test_command_grants"
      BODY=("/dispatch, the two sweeps and /team deny every agent type they do not grant, and the suite's list of Claude Code's built-in types lacked fork, which inherits the parent's tools. Whether a deny spelled Agent(fork) parsed was unverified, and a rule that matches nothing can stop Claude Code starting."
        "It was checked in a scratch session on Claude Code 2.1.316: a command whose disallowed-tools held Agent(fork) started, and its call was refused as denied by permission rule Agent(fork) from command; the same command without the rule ran the fork."
        "fork joins the suite's built-in list, and each of the four commands denies it. Both cases failed before and pass after.") ;;
    3)
      SUBJECT="fix(harness): result reads a reviewer spawn from the transcript of a merge reporting no round"
      PHRASE="reads a reviewer spawn from the transcript"
      TESTS="$s/test_dispatch_run.py"
      CODE="$s/dispatch_run.py"
      local r=test_dispatch_run.ResultCommandTests
      CASES="$r.test_a_merge_with_no_round_is_reviewed_when_the_transcript_shows_a_reviewer_spawned"
      CASES+=" ~$r.test_a_merge_with_no_round_is_clean_only_on_a_relaunch_this_launch_read_a_session_of"
      MODULES="test_dispatch_run"
      BODY=("In run 20261101-195321, group M2's result for PR 1699 held no rounds and called its session a re-entry, though its transcript held a branch-reviewer round before the merge. The session had re-entered /ship after a compaction and reported only the rounds it ran since, so the report listed a reviewed merge under Needs a human. That fails safe, but it sends the owner to check a merge that was reviewed."
        "result now reads the session's transcript before it flags a merge with no round, and a branch-reviewer spawn that was not refused counts as the round. A refused spawn, another agent type, or a transcript it cannot read still flags the merge. The result's own rounds stay as reported."
        "The new case failed before and passes after; the existing flag cases hold.") ;;
  esac
}

# land <root>: the step's tests from <root>/staging, every case failing, its code, then every case and suite passing.
land() {
  local from=$1/staging
  copy_in "$from" $TESTS || { echo "could not copy the tests in: $SUBJECT" >&2; put_back $TESTS; exit 1; }
  if ! (cd .claude/scripts && "${PY[@]}" -c "$CASES_PY" before "$EXPECT" $CASES); then
    echo "a case passed, or broke, before its code: $SUBJECT" >&2; put_back $TESTS; exit 1
  fi
  copy_in "$from" $CODE || { echo "could not copy the code in: $SUBJECT" >&2; put_back $TESTS $CODE; exit 1; }
  if ! (cd .claude/scripts && "${PY[@]}" -c "$CASES_PY" after "" $CASES $MODULES $SUITES); then
    echo "the cases or suites fail after: $SUBJECT" >&2; put_back $TESTS $CODE; exit 1
  fi
}

# Called where a failure is handled, so set -e is off inside it: each copy says for itself that it failed.
copy_in() {
  local from=$1 path
  shift
  for path in "$@"; do cp -- "$from/$path" "$path" || return 1; done
}

# A path HEAD holds is checked out again; one it does not is a staged file this step added, unstaged and removed.
put_back() {
  local path
  for path in "$@"; do
    if git cat-file -e "HEAD:$path" 2>/dev/null; then
      git checkout -q HEAD -- "$path"
    else
      git rm -q --cached --ignore-unmatch -- "$path" >/dev/null
      rm -f -- "$path"
    fi
  done
}

# all_landed <root>: every file staged under <root>/staging/.claude/ is in place, so no step forgot one. A listing
# that failed or found nothing would pass every file it did not name.
all_landed() {
  local path listed missing=0
  listed=$(git -C "$1" ls-files -- staging/.claude | tr -d '\r') || { echo "could not list staging/" >&2; return 1; }
  [ -n "$listed" ] || { echo "git lists nothing under $1/staging/.claude" >&2; return 1; }
  while IFS= read -r path; do
    path=${path#staging/}
    cmp -s -- "$1/staging/$path" "$path" || { echo "not landed: $path" >&2; missing=1; }
  done <<<"$listed"
  [ "$missing" = 0 ]
}

# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/dispatch-1711-lows
  [ -f staging/.claude/scripts/dispatch_run.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local pr
  pr="$(bash .claude/scripts/pr-for-branch.sh "$branch" | "${PY[@]}" -c \
    "import json, sys; print(next((r['number'] for r in json.load(sys.stdin) if r['state'] == 'OPEN'), ''))" \
    | tr -d '\r')"
  [ -n "$pr" ] || { echo "no open pull request for $branch" >&2; exit 2; }
  TRAILERS=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01QAU7oKrRQD8Gub3WVP1kf9" --trailer "PR: #$pr")

  local n
  for n in $STEPS; do
    step "$n"
    if ! done_already "$PHRASE"; then
      land .
      commit_step
    fi
  done
  all_landed . || exit 1

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for the 1711 fixes" \
    -m "Its changes are the three commits before this one." "${TRAILERS[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -5
  echo DONE
}

# Whether a commit on this branch already carries this phrase in its subject.
done_already() {
  git log --format=%s origin/main..HEAD | tr -d '\r' | grep -qF -- "$1"
}

# Each paragraph wrapped at 80, as /commit's bodies are; the CR strip is for Windows' print.
# A failed add or commit puts the step's files back, so a rerun lands the step afresh rather than finding it half done.
commit_step() {
  local message
  message="$("${PY[@]}" -c 'import sys, textwrap
print(sys.argv[1] + "\n\n" + "\n\n".join(textwrap.fill(p, 80) for p in sys.argv[2:]))' "$SUBJECT" "${BODY[@]}" \
    | tr -d '\r')" || { echo "could not build the message: $SUBJECT" >&2; put_back $TESTS $CODE; exit 1; }
  if ! { git add -- $TESTS $CODE && git commit -q -F - "${TRAILERS[@]}" <<<"$message"; }; then
    echo "could not commit: $SUBJECT" >&2; put_back $TESTS $CODE; exit 1
  fi
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
  exit
fi
