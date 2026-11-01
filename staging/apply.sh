#!/usr/bin/env bash
# Land review round 2's fixes on chore/dispatch-command from staging/, each step's cases seen failing
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
STEPS="1"

# step <n> sets the step's subject, guard phrase, files, cases, the one error its before may show, suites run only
# after, and its commit body.
step() {
  local s=.claude/scripts
  MODULES=""
  EXPECT=""
  TESTS=""
  case $1 in
    1)
      SUBJECT="fix(harness): a reported run that still holds rows is resumed, and wake tells held from drained"
      PHRASE="a reported run that still holds rows is resumed"
      TESTS="$s/test_dispatch_run.py"
      CODE="$s/dispatch_run.py .claude/commands/dispatch.md"
      CASES="test_dispatch_run.RunDirectoryTests.test_a_reported_run_stays_open_while_it_holds_in_progress_rows"
      CASES+=" test_dispatch_run.WakeTests.test_a_ready_group_only_another_sessions_row_holds_is_not_drained"
      CASES+=" test_dispatch_run.WakeTests.test_the_runbook_names_every_ending_wake_prints"
      MODULES="test_dispatch_run"
      BODY=("Step 3 writes report.md on every night, and open_run skipped any run that had one, so after an interrupted night the next start opened a fresh run, although the report said it resumes the old one. That run's tasks done and back refused the old run's In progress rows, and board and wake read them as another session's claim. A reported run now stays open while any group it moved is neither done nor sent back, and the report's resume line appears exactly then."
        "wake printed end: drained when a group was ready but held only by an In progress row, so a blocked backlog read as an empty one. With nothing running, a ready group left unchosen is held, and wake now says end: held. The runbook's night loop names it beside the other endings."
        "The reopened-run case, the held case and a case that reads every ending from the source and finds it in the runbook fail before the code and pass after; apply.sh checks all three.") ;;
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
  local branch=chore/dispatch-command
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
    --trailer "Claude-Session: https://claude.ai/code/session_01QAU7oKG88xPpworsdhz8vg" --trailer "PR: #$pr")

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
  git commit -q -m "chore: remove the applied staging/ for review round 2" \
    -m "Its change is the commit before this one." "${TRAILERS[@]}"
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
