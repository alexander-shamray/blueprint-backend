#!/usr/bin/env bash
# Land /dispatch on chore/dispatch-command from staging/, each step's cases seen failing before its code and passing
# after, and commit each. The push is /ship's. A failed step puts its files back, so a rerun resumes at that step.
# dry_run.sh sources this file for step, land and all_landed, so main runs only when it is executed.
set -euo pipefail

# Every listed case must fail before its code and pass after; a name led by ~ is a case that holds on both sides.
# Before, a case fails by an assertion, or by an error EXPECT names: any other error is a broken case, not a red one.
CASES_PY='
import re, sys, unittest
mode, expect = sys.argv[1], sys.argv[2]
held = {n[1:] for n in sys.argv[3:] if n.startswith("~")}
names = [n for n in sys.argv[3:] if not n.startswith("~")]
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
sys.exit(1 if wrong or not cases else 0)
'
# The step's ship.md under the ceiling the staged test_command_runbooks holds it to, before that suite has landed.
CEILING_PY='
import ast, sys
tree = ast.parse(open(sys.argv[1], encoding="utf-8").read())
found = [n.value for n in tree.body
         if isinstance(n, ast.Assign) and [getattr(t, "id", None) for t in n.targets] == ["CEILINGS"]]
ceiling = ast.literal_eval(found[0])[sys.argv[2]]
size = len(open(sys.argv[3], "rb").read().replace(b"\r\n", b"\n"))
print(f"{sys.argv[2]}: {size} bytes, ceiling {ceiling}")
sys.exit(0 if size <= ceiling else 1)
'
# The precedent suites, run after every step. test_command_runbooks joins at step 4: its runbook case fails until
# dispatch.md has a runbook, since docs/commands/dispatch.md landed ahead of the command.
SUITES="test_command_grants test_ship_landing_method"

# step <n> sets the step's subject, guard phrase, files, cases, the one error its before may show, suites run only
# after, the runbook it holds to its staged ceiling, and its commit body.
step() {
  local s=.claude/scripts
  MODULES=""
  EXPECT=""
  CEILING=""
  case $1 in
    1)
      SUBJECT="feat(harness): dispatch_board.py reads the task list and picks free groups"
      PHRASE="reads the task list and picks free groups"
      EXPECT="^ModuleNotFoundError: No module named .(dispatch_board|dispatch_run|watch_session).$"
      TESTS="$s/test_dispatch_board.py $s/test_dispatch_run.py $s/test_watch_session.py"
      CODE="$s/dispatch_board.py $s/dispatch_run.py $s/watch_session.py"
      CASES="test_dispatch_board test_dispatch_run test_watch_session"
      BODY=("dispatch_board.py parses the task list into its sections and groups, says which groups are ready and free, picks the next to launch, and makes the dispatcher's only task-list writes, each re-reading the file as it writes so another session's edit is merged, not overwritten."
        "dispatch_run.py is its run half: it starts a run, launches one headless executor with the prompt on stdin and no permission flag, reads its result and session as untrusted data, verifies a claimed merge against GitHub and decides each wake from state.json, which only the dispatcher writes. watch_session.py follows any executor's transcript."
        "The three suites cannot import before the code lands and pass after it; apply.sh checks both.") ;;
    2)
      SUBJECT="feat(harness): issue_decisions.py records an owner's answer on the issue"
      PHRASE="records an owner's answer on the issue"
      EXPECT="^ModuleNotFoundError: No module named .issue_decisions.$"
      TESTS="$s/test_issue_decisions.py"
      CODE="$s/issue_decisions.py"
      CASES="test_issue_decisions"
      BODY=("The dispatcher's interview records each answer under Owner decisions in the issue body, and removes the question or paused label or closes the issue. No grant reaches gh issue edit or close: this helper is the one way those writes happen, with the repository pinned and the issue text never interpreted."
        "test_issue_decisions cannot import before the code lands and passes after it; apply.sh checks both.") ;;
    3)
      SUBJECT="feat(harness): /ship and /branch take --dispatched"
      PHRASE="take --dispatched"
      TESTS="$s/test_ship_dispatched.py"
      CODE=".claude/commands/ship.md .claude/commands/branch.md"
      CEILING="ship.md"
      # Two cases hold on both sides by design: the token adds no grant, and the prompt case reads dispatch_run.
      local held=test_ship_dispatched.TheDispatchedTokenChangesOnlyWhatTheSpecLists
      CASES="test_ship_dispatched ~$held.test_the_grants_did_not_change"
      CASES+=" ~$held.test_the_prompt_the_dispatcher_builds_reads_back_as_that_path"
      BODY=("The token /dispatch passes changes only what the design lists: three hand-back cases beside the seven stops, no task-list write in /branch step 5 or /ship step 6, no teardown in step 6, which the dispatcher does once it has verified the merge, and a result file on every end whose keys test_ship_dispatched holds equal to dispatch_run's validator. branch.md's In progress row gains its group column. No grant changes."
        "Every case of test_ship_dispatched fails on the commands as they stood and passes after, apart from two that hold on both sides by design: the grants did not change, and the prompt dispatch_run builds, which the first commit landed, reads back as the result path. apply.sh checks both."
        "ship.md grows past test_command_runbooks' ceiling. The raised ceiling lands with dispatch.md's in the next commit, where that suite first passes.") ;;
    4)
      SUBJECT="feat(harness): /dispatch runs the backlog overnight in headless /ship sessions"
      PHRASE="runs the backlog overnight in headless"
      TESTS="$s/test_command_grants.py $s/test_command_runbooks.py"
      CODE=".claude/commands/dispatch.md"
      local runbooks=test_command_runbooks.EachSplitCommandHasBothHalves
      CASES="test_command_grants.TheDispatcherSpawnsSessionsNotAgents $runbooks.test_each_runbook_stays_under_its_ceiling"
      CASES+=" $runbooks.test_every_split_command_has_a_runbook_that_cites_its_argument"
      MODULES="test_command_runbooks"
      EXPECT="^FileNotFoundError: .*dispatch[.]md.$"
      BODY=("The command drives the night from the helpers the earlier commits landed: step 0 checks the main checkout and rebuilds the task list, step 1 interviews the owner one question at a time, step 2 launches executors as background tasks whose exits are the only wakes, and step 3 writes the report."
        "Its frontmatter grants no agent type and denies every one, and claude as a shell command; test_command_grants reads the profiles on every run, so one added later fails until the command denies it."
        "test_command_runbooks gains dispatch.md's ceiling and ship.md's raised one. Its runbook case has failed since docs/commands/dispatch.md landed ahead of the command, so both ceilings land here, where the suite first passes. The grant cases and the two runbook cases fail before dispatch.md lands and pass after; apply.sh checks both.") ;;
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
  # The ceiling's own suite lands a step later, so until then nothing else would see this runbook outgrow it.
  if [ -n "$CEILING" ] && ! "${PY[@]}" -c "$CEILING_PY" "$from/.claude/scripts/test_command_runbooks.py" \
      "$CEILING" ".claude/commands/$CEILING"; then
    echo "$CEILING is over its staged ceiling: $SUBJECT" >&2; put_back $TESTS $CODE; exit 1
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
  [ -f staging/.claude/commands/dispatch.md ] || { echo "no staging/ here" >&2; exit 2; }
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
  for n in 1 2 3 4; do
    step "$n"
    if ! done_already "$PHRASE"; then
      land .
      commit_step
    fi
  done
  # The plan's steps 5 and 6 would land command grants and staging/.claude/settings.json. The spike that would
  # have staged them was skipped, so neither exists and neither step is written.
  all_landed . || exit 1

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for /dispatch" \
    -m "Its four changes are the commits before this one." "${TRAILERS[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -6
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
