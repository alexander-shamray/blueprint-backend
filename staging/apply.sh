#!/usr/bin/env bash
# Apply pr-locality.sh's two fixes on fix/pr-locality-renames-and-crlf, each case seen failing before its code where
# this host can show it, and commit each. The push is /ship's. A failed step puts its files back for a rerun.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/pr-locality-renames-and-crlf
  [ -f staging/patch_locality.py ] || { echo "no staging/ here" >&2; exit 2; }
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
  local c=test_pr_helpers.NoCommandReadsAPullRequestFeedUnfiltered

  if ! done_already "judges a rename's source"; then
    fix "fix(harness): pr-locality.sh judges a rename's source beside its destination" \
      rename-tests rename-script "$c.test_a_rename_is_judged_at_both_ends" \
      "The helper read only each changed file's filename from the files endpoint, so a rename's source, previous_filename, was never judged, while the CI locality gate judges both ends because a move out of a tree changes that tree. A rename out of src into a declared docs tree read as inside to branch-reviewer and outside to CI." \
      "The jq now emits a rename's previous_filename after its filename, each through the same JSON-string validation, so each end gets its own inside or outside line. The call no longer fits 120 columns, so it continues onto a second line, and test_the_locality_rows_go_through_the_fixed_helper, which pins both gh calls whole, now joins a continued line as bash does before it compares, so the tail is still pinned." \
      "test_a_rename_is_judged_at_both_ends runs the helper's own --jq over a files payload holding a rename, through real jq. It failed before the change and passes after it; apply.sh checks both." \
      "Closes #1254"
  fi

  if ! done_already "strips carriage returns"; then
    # MSYS grep and sed read CRLF as a line end, so on Windows the case passes before the fix; Linux CI shows it.
    fix "fix(harness): pr-locality.sh strips carriage returns from the pull request body" \
      crlf-tests crlf-script - \
      "A body saved on Windows or through the web form ends each line in CR. On Linux grep keeps it and the sed that strips the row's closing pipe cannot match a pipe followed by CR, so the Class cell fails its grammar and the helper exits 3; the review round then skips its locality check while CI, which splits lines with splitlines, accepts the body." \
      "The body is stripped of CR once captured, after the capture, so a gh failure is still fatal under set -e." \
      "test_a_crlf_body_is_read_as_its_rows makes the CR at run time with printf. It cannot fail on this Windows host, whose MSYS grep and sed read CRLF as a line end, so apply.sh does not ask it to; on the Linux review-helpers job it fails without the strip." \
      "Closes #1278"
  fi

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for pr-locality.sh" \
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

# fix <subject> <test part> <code part> <case, or - for none this host can fail> <body paragraphs...>
fix() {
  local subject=$1 test_part=$2 code_part=$3 case=$4
  shift 4
  local files=".claude/scripts/test_pr_helpers.py .claude/scripts/pr-locality.sh"
  "${PY[@]}" staging/patch_locality.py . "$test_part"
  # The new case must fail on the code as it stands, or it pins nothing.
  if [ "$case" != - ] && (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
    echo "passed before the fix: $case" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  "${PY[@]}" staging/patch_locality.py . "$code_part"
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_pr_helpers); then
    echo "the suite fails after: $subject" >&2; git checkout -q HEAD -- $files; exit 1
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
