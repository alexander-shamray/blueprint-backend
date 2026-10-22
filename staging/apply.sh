#!/usr/bin/env bash
# Apply the sweeps' two fixes on fix/sweeps-refuse-dropped-verifier-records, each case seen failing before its code,
# and commit each. The push is /ship's. A failed step puts its files back, so a rerun resumes at that step.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/sweeps-refuse-dropped-verifier-records
  [ -f staging/patch_sweeps.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local pr
  pr="$(bash .claude/scripts/pr-for-branch.sh "$branch" | "${PY[@]}" -c \
    "import json, sys; print(next((r['number'] for r in json.load(sys.stdin) if r['state'] == 'OPEN'), ''))" \
    | tr -d '\r')"
  [ -n "$pr" ] || { echo "no open pull request for $branch" >&2; exit 2; }
  TRAILERS=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #$pr")
  local t=.claude/scripts/test_gh_issue_suppresses.py
  local c=test_gh_issue_suppresses

  if ! done_already "retry a verifier record that is not a verdict"; then
    fix "fix(harness): the sweeps retry a verifier record that is not a verdict, and a second is a round error" \
      records-tests records "$t .claude/commands/bug-sweep.md .claude/commands/security-sweep.md" \
      "$c.BothSweepsAgreeOnWhatSuppresses.test_a_verifier_record_that_is_not_a_verdict_is_retried_then_an_error" \
      "Both sweeps dropped a verifier record that was not in the declared shape, or that named another file or line than the candidate dispatched, and only counted it. A round whose only survivor was dropped that way filed nothing, so it was clean, and a clean sweep marks the commit it read, so the next incremental sweep never read the candidate's file again: a verification that never happened was recorded as a clean audit." \
      "Such a record is now not a verdict: the verifier is dispatched once more with the same candidate, and a second such record is a round error under Never fail open, so the round is not clean and marks nothing. docs/commands/ carries the argument." \
      "test_a_verifier_record_that_is_not_a_verdict_is_retried_then_an_error pins the rule in both sweeps and their arguments. It failed before the change and passes after it; apply.sh checks both." \
      "Closes #1248"
  fi

  if ! done_already "refuses a list as long as its limit"; then
    fix "fix(harness): gh-issue-list.sh refuses a list as long as its limit, which may be cut" \
      list-tests list "$t .claude/scripts/gh-issue-list.sh" \
      "$c.WhatSuppressesIsDecidedByCodeNow.test_a_list_as_long_as_its_limit_is_refused_as_cut" \
      "gh issue list stops at --limit without a word, and the helper asked for 1000 with no check, so past 1000 issues the sweeps' de-duplication would lose the oldest and file a duplicate of a closed or accepted finding with no signal. The repository is within a few sweeps of that." \
      "The limit is now 5000 and a list that long is refused with exit 3, since it may be cut, so the limit is raised before the de-duplication goes blind. realm.yml and object-store.yml, which cite this helper for their own listing, refuse the same way in this branch's earlier commit." \
      "test_a_list_as_long_as_its_limit_is_refused_as_cut stubs gh to answer with as many issues as --limit asks for, as a cut list does. It failed before the change and passes after it; apply.sh checks both." \
      "Closes #1279"
  fi

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for the sweeps" \
    -m "Its two fixes are the commits before this one." "${TRAILERS[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -4
  echo DONE
}

# Whether a commit on this branch already carries this phrase in its subject.
done_already() {
  git log --format=%s origin/main..HEAD | tr -d '\r' | grep -qF "$1"
}

# fix <subject> <test part> <code part> <files> <case> <body paragraphs...>
fix() {
  local subject=$1 test_part=$2 code_part=$3 files=$4 case=$5
  shift 5
  "${PY[@]}" staging/patch_sweeps.py . "$test_part"
  # The new case must fail on the code as it stands, or it pins nothing.
  if (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
    echo "passed before the fix: $case" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  "${PY[@]}" staging/patch_sweeps.py . "$code_part"
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_gh_issue_suppresses test_command_runbooks test_command_grants); then
    echo "the suites fail after: $subject" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  # Each paragraph wrapped at 80, as /commit's bodies are; the CR strip is for Windows' print.
  local message
  message="$("${PY[@]}" -c 'import sys, textwrap
print(sys.argv[1] + "\n\n" + "\n\n".join(textwrap.fill(p, 80) for p in sys.argv[2:]))' "$subject" "$@" | tr -d '\r')"
  git add $files
  git commit -q -F - "${TRAILERS[@]}" <<<"$message"
}

main "$@"
exit
