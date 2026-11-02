#!/usr/bin/env bash
# Land #1692's fixes on fix/dispatch-low-findings from staging/, each step's cases seen failing
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
SUITES+=" test_watch_session test_issue_decisions test_ship_dispatched"
STEPS="1 2"

# step <n> sets the step's subject, guard phrase, files, cases, the one error its before may show, suites run only
# after, and its commit body.
step() {
  local s=.claude/scripts
  MODULES=""
  EXPECT=""
  TESTS=""
  case $1 in
    1)
      SUBJECT="fix(harness): the board and run halves of /dispatch close seven of #1692's low findings"
      PHRASE="the board and run halves of /dispatch close seven of"
      TESTS="$s/test_dispatch_board.py $s/test_dispatch_run.py"
      CODE="$s/dispatch_board.py $s/dispatch_run.py .claude/commands/dispatch.md"
      local paths=test_dispatch_board.PathTests writes=test_dispatch_board.WriteTests
      local runs=test_dispatch_run.RunDirectoryTests weekly=test_dispatch_run.WeeklyTests
      CASES="$paths.test_a_backticked_slash_token_is_a_path_only_with_a_dot_or_a_top_level_directory"
      CASES+=" $writes.test_an_undo_that_fails_is_named_beside_the_refused_list_write"
      CASES+=" $runs.test_pairs_that_is_a_bool_is_refused"
      CASES+=" $weekly.test_no_source_is_not_read_and_never_ends_the_night"
      CASES+=" $weekly.test_a_real_reading_after_unread_wakes_is_the_start"
      CASES+=" $weekly.test_every_wake_prints_one_weekly_line"
      CASES+=" $weekly.test_a_failed_reading_is_recorded_and_is_not_the_limit"
      CASES+=" $weekly.test_failures_keep_a_count_and_the_last_reason_not_a_list"
      CASES+=" $weekly.test_the_next_wake_reads_again_after_a_failure"
      CASES+=" test_dispatch_run.ReportTests.test_an_unread_week_is_said_so"
      CASES+=" test_dispatch_run.ResultCommandTests.test_usage_sums_every_session_the_group_ran"
      CASES+=" ~test_dispatch_run.PathWhichTests.test_a_git_planted_in_the_cwd_is_not_used_by_git_or_git_bash"
      # The old state.json kept a failures list, so a case reading the new failed record raises KeyError before.
      EXPECT="^KeyError: 'failed'$"
      MODULES="test_dispatch_board test_dispatch_run"
      BODY=("Seven of the low findings #1692 lists from #1691's reviews and the first dispatch night, in the board and run halves of the /dispatch helper. A backticked token with a slash, such as and/or, counted as a path in a group's claim; it now counts only with a dot in it or a first segment naming one of the checkout's top-level directories, read from the checkout, and every slash token still counts when that listing fails, the over-claiming side. A move whose list write was refused undid its state.json record inside a suppress, so a failed undo left a record of a move the list never got and said nothing; the undo now raises and the refusal names both failures."
        "start took --pairs True as one pair, since a bool is an int to Python; it is refused as not a whole number. A source failing all night appended to usage.failures at every wake; state.json now keeps a count, the time and the last reason. A wake with no source wrote a null start point, so a later real reading could only ever be the end; only a real reading is a point now. wake printed a weekly line only when a reading failed; it prints one every wake past the stop file, as the runbook now says, and an unread week says no source is set. result overwrote a group's token usage with each session's, so the night's report showed a relaunched group as its last session only (S1's three sessions read 2.8M beside S3's one at 14.0M); it is summed over the move. The no-source case asserted only that the last line was not end week; it asserts the weekly and launch lines, and the planted-git case asserts the refusal's message."
        "Every new case failed before the code and passes after, the ones reading the failed record by KeyError on the old shape; the planted-git case holds on both sides. apply.sh checks each.") ;;
    2)
      SUBJECT="fix(harness): issue_decisions.py refuses an answer-marker question and says refused on a usage error"
      PHRASE="issue_decisions.py refuses an answer-marker question"
      TESTS="$s/test_issue_decisions.py"
      CODE="$s/issue_decisions.py"
      local shapes=test_issue_decisions.Shapes cli=test_issue_decisions.Cli
      CASES="$shapes.test_a_question_opening_with_the_answer_marker_is_refused"
      CASES+=" $shapes.test_an_empty_body_gets_the_section_with_no_blank_lines_before_it"
      CASES+=" $cli.test_a_usage_error_exits_2_with_the_refused_prefix"
      CASES+=" ~$cli.test_a_gh_that_cannot_start_exits_3"
      CASES+=" ~test_issue_decisions.PathWhichParity.test_both_copies_find_the_same_executable_and_skip_the_same_planted_one"
      MODULES="test_issue_decisions"
      BODY=("The rest of #1692's findings in issue_decisions.py. A question opening with A colon and a space passed the check for the answer marker, which looked only for one with a space before it, and the entry then read Q: A: with a later split in the wrong place; the check now reads the question as the entry holds it, after Q: and a space. An empty issue body got its Owner decisions section after two blank lines; the section now opens the body. An argparse usage error exited 2 with argparse's usage and no refused prefix, which the runbook reads exit 2 by; a refused line now follows."
        "Two cases hold on both sides, since what they pin was already right but untested: a gh that cannot start exits 3 through the OSError branch, and this file's copy of path_which answers as dispatch_board's does over the same PATH, a planted executable in the working directory included. The shutil import the finding suspected is not in the file."
        "The three changed behaviours' cases failed before the code and pass after; apply.sh checks each.") ;;
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
  local branch=fix/dispatch-low-findings
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
  git commit -q -m "chore: remove the applied staging/ for #1692's fixes" \
    -m "Its changes are the two commits before this one." "${TRAILERS[@]}"
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
