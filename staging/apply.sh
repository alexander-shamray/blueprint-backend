#!/usr/bin/env bash
# Land review round 1's fixes and the CI fix on chore/dispatch-command from staging/, each step's cases seen failing
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
STEPS="1 2 3"

# step <n> sets the step's subject, guard phrase, files, cases, the one error its before may show, suites run only
# after, and its commit body.
step() {
  local s=.claude/scripts
  MODULES=""
  EXPECT=""
  TESTS=""
  case $1 in
    1)
      SUBJECT="fix(harness): issue_decisions' gh stub runs on Linux too"
      PHRASE="gh stub runs on Linux too"
      # The fix is the suite itself, so its before is HEAD's copy: on Linux it misses the stub, finds the real gh,
      # and fails these cases; on Windows it finds gh.cmd and passes them, so there they hold on both sides.
      EXPECT="^FileNotFoundError: .*stub/written[.]bin.$"
      CODE="$s/test_issue_decisions.py"
      MODULES="test_issue_decisions"
      local held="" id
      if [ "${OS:-}" = Windows_NT ]; then held="~"; fi
      CASES=""
      for id in test_a_crlf_body_keeps_crlf test_the_default_date_is_today \
          test_a_double_section_body_is_refused_and_nothing_is_written test_a_failed_gh_exits_3 \
          test_a_gh_planted_in_the_cwd_is_not_run_and_the_one_on_path_is \
          test_a_russian_answer_with_an_em_dash_reaches_the_body_byte_for_byte \
          test_close_comments_then_closes_as_not_planned \
          test_close_posts_the_text_it_checked_even_when_the_file_is_swapped_after \
          test_close_takes_only_a_short_comment_the_run_wrote_under_its_directory \
          test_record_reads_then_writes_the_body_with_repo_pinned test_unblock_removes_the_label_with_repo_pinned; do
        CASES+=" ${held}test_issue_decisions.Cli.$id"
      done
      BODY=("The stub was gh.cmd alone, which only Windows runs through PATHEXT. On CI's Linux runner the helper looked up the bare name gh, found the real one, which refused with no token, and review-helpers failed nine cases and errored two. The suite now writes gh.cmd on Windows and an executable POSIX gh elsewhere, and plants a POSIX gh in the cwd case."
        "The old suite fails those eleven cases on Linux and the new one passes them. On Windows both pass, so apply.sh holds them there: the red shows on Linux only.") ;;
    2)
      SUBJECT="fix(harness): a refused relaunch frees its slot, and wake follows the live session"
      PHRASE="a refused relaunch frees its slot"
      TESTS="$s/test_dispatch_run.py"
      CODE="$s/dispatch_run.py"
      local wake=test_dispatch_run.WakeTests
      CASES="$wake.test_a_relaunch_after_the_stop_file_starts_nothing_and_its_group_goes_back"
      CASES+=" $wake.test_wake_follows_a_relaunched_executor_not_the_session_it_read"
      MODULES="test_dispatch_run"
      BODY=("A relaunch's run removed the earlier session's record only after the stop file, prompt, model, worktree and claude checks, so a refused relaunch left its refusal beside that record. result then refused the pair, resume printed relaunch again, and the group held its slot all night. The read record and its copied result now go before any check can refuse, so a refusal leaves only the marker and result sends the group back."
        "wake looked for a live transcript only for a running group, so a relaunched one, which keeps its read status while its new executor runs, showed the dead session's. It now looks from the pid file's time for every active group."
        "The stop-file case, given an earlier session record, and the new transcript case fail before the code and pass after; apply.sh checks both.") ;;
    3)
      SUBJECT="docs(harness): /dispatch's grants auto-approve, and a stopped window group goes back"
      PHRASE="grants auto-approve, and a stopped window group goes back"
      TESTS="$s/test_command_grants.py $s/test_command_runbooks.py"
      CODE=".claude/commands/dispatch.md"
      CASES="test_command_grants.TheDispatcherSpawnsSessionsNotAgents.test_dispatch_says_its_grants_only_auto_approve"
      CASES+=" test_command_runbooks.DispatchSendsAStoppedWindowGroupBack"
      CASES+=".test_the_window_bullet_and_its_resume_row_send_the_group_back"
      BODY=("The runbook said its frontmatter is the whole of what the session may do. A grant only auto-approves: a tool it does not list stays callable under the permission settings, and only a deny refuses, as docs/harness-boundaries.md's Grant semantics states. The line now says so and cites it."
        "A window group whose wait printed stop file: yes was left window, so the report listed it as interrupted while docs/commands/dispatch.md says it goes back. The window bullet and its Resume row now send it back with tasks back."
        "Each line has a case that fails on the runbook as it stood and passes after; apply.sh checks both.") ;;
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
  git commit -q -m "chore: remove the applied staging/ for review round 1" \
    -m "Its three changes are the commits before this one." "${TRAILERS[@]}"
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
