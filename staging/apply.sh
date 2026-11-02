#!/usr/bin/env bash
# Land the three /dispatch fixes on fix/dispatch-refusal-and-titles from staging/<n>/, each step's cases seen
# failing before its code and passing after, and commit each. The push is /ship's. A failed step puts its files back, so a rerun
# resumes at that step. Each staging/<n>/ holds its step's whole files, so a later step's copy builds on an earlier
# one's. dry_run.sh sources this file for STEPS, step, land and all_landed.
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
  local r=test_dispatch_run.ResultCommandTests t=test_dispatch_run.ReportTests
  MODULES="test_dispatch_run"
  EXPECT=""
  TESTS="$s/test_dispatch_run.py"
  case $1 in
    1)
      SUBJECT="fix(harness): result sends back a group whose refused run could not write its marker"
      PHRASE="whose refused run could not write its marker"
      CODE="$s/dispatch_run.py $c/dispatch.md"
      EXPECT="SystemExit: 2"  # argparse refuses --refused before its code adds it
      CASES="$r.test_a_run_whose_marker_was_not_written_goes_back_on_its_refusal_line"
      CASES+=" $r.test_a_refusal_line_after_an_executor_started_waits_for_it_or_hands_it_back"
      CASES+=" $r.test_a_refusal_line_beside_a_marker_or_a_session_record_is_not_read"
      CASES+=" test_dispatch_run.WakeTests.test_a_relaunch_removes_the_earlier_sessions_pid_before_anything_can_refuse"
      CASES+=" ~$r.test_result_before_the_session_ended_is_refused"
      BODY=("A run whose launch was refused and whose marker could not be written either exited 2 with neither a marker nor a session record, so result refused for want of a session record and the group held its executor slot all night. Found by #1691's review and carried by #1711 and #1718."
        "result now takes run's refusal line as --refused, which the runbook passes after every exit 2, and reads it only when it finds no session record and no marker from this launch: with no pid file the group is crashed-before-launch, with the pid file of an ended executor it is a crash-hand-back, since the executor ran and may have merged, and with a live one result refuses. run now removes an earlier pid file before its launch checks; one a refusal left reads as an executor that ran, a hand-back, the cautious side. Without the line result still refuses, since it cannot tell a run that never started from one still running."
        "Rejected: result recording the group as crash-hand-back on its own, the fix #1711 proposed, since nothing then separates a refused run from an executor still running. Recording either outcome writes state.json in the same directory, so a directory refusing every write still holds the slot; this separates a marker only another process holds."
        "The runbook drops the stop-file sentence the argument already owns, to stay under its ceiling. The four cases failed before and pass after; the not-yet-exited case holds.") ;;
    2)
      SUBJECT="fix(harness): the morning report's merge lines carry the PR title the executor's result names"
      PHRASE="carry the PR title the executor's result names"
      CODE="$s/dispatch_run.py $c/ship.md"
      EXPECT="AttributeError: 'Invalid' object has no attribute 'title'"
      CASES="test_dispatch_run.ResultTests.test_a_title_is_null_or_text"
      CASES+=" $r.test_result_records_the_prs_title"
      CASES+=" $t.test_each_merge_line_names_its_pr_title_on_one_line"
      MODULES="test_dispatch_run test_ship_dispatched"
      BODY=("The design's section 10 puts each PR's title on its Merged line, but the report named only the issues, since no helper the dispatcher holds reads a title. Found by #1691's review and carried by #1711 and #1718."
        "The dispatched result gains a title key, the PR's title as /pr opened it, or null where the session opened none, as a relaunch step 0 ended on a merge. result records it and report shows it on every merge line, kept to one line since the executor wrote it."
        "Rejected: a read-only title helper granted to the dispatcher, or a title field added to pr-state.sh, since either brings author-written GitHub text into a night nobody watches; the executor's title stands where its decisions already do."
        "The three cases failed before and pass after.") ;;
    3)
      SUBJECT="fix(harness): Filed overnight counts what the interview filed or closed with a comment"
      PHRASE="Filed overnight counts what the interview filed"
      CODE="$s/dispatch_run.py $c/dispatch.md"
      EXPECT="SystemExit: 2"  # argparse refuses filed before its code adds it
      CASES="$t.test_what_the_interview_files_or_closes_is_filed_overnight"
      BODY=("Filed overnight listed what the results named and what tasks place recorded, but an issue the interview filed or closed with a comment reaches neither, so the report left it out. Found by #1691's review and carried by #1711 and #1718."
        "A filed subcommand records an issue number in the run's state, beside what tasks place records, and the runbook's interview step runs it for each issue it closes or files. A close comment is a comment on an issue, which the spec's Filed overnight counts."
        "The case failed before and passes after.") ;;
  esac
}

# land <root> <n>: step n's tests from <root>/staging/<n>, every case failing, its code, then every case and suite
# passing.
land() {
  local from=$1/staging/$2
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

# all_landed <root>: every file staged under <root>/staging/<n>/.claude/ is in place as the last step holding it
# left it, so no step forgot one. A listing that failed or found nothing would pass every file it did not name.
all_landed() {
  local path listed missing=0 n last
  listed=$(git -C "$1" ls-files -- 'staging/*/.claude/*' | tr -d '\r') ||
    { echo "could not list staging/" >&2; return 1; }
  [ -n "$listed" ] || { echo "git lists nothing under $1/staging/*/.claude" >&2; return 1; }
  while IFS= read -r path; do
    path=${path#staging/*/}
    last=""
    for n in $STEPS; do [ -f "$1/staging/$n/$path" ] && last=$n; done
    cmp -s -- "$1/staging/$last/$path" "$path" || { echo "not landed: $path" >&2; missing=1; }
  done <<<"$listed"
  [ "$missing" = 0 ]
}

# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/dispatch-refusal-and-titles
  [ -f staging/1/.claude/scripts/dispatch_run.py ] || { echo "no staging/ here" >&2; exit 2; }
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
      land . "$n"
      commit_step
    fi
  done
  all_landed . || exit 1

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for #1718's fixes" \
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
