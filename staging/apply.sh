#!/usr/bin/env bash
# Apply review round 1's two findings on fix/pr-locality-renames-and-crlf, the case seen failing first, and commit.
# The push is /ship's. A failed step puts its file back, so a rerun resumes there.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/pr-locality-renames-and-crlf
  [ -f staging/patch_review.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local trailers=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #1348")
  local file=.claude/scripts/test_pr_helpers.py
  local case=test_pr_helpers.NoCommandReadsAPullRequestFeedUnfiltered.test_a_comment_ending_in_a_backslash_swallows_no_call

  "${PY[@]}" staging/patch_review.py . test
  # The new case must fail on the pin's order as it stands, or it pins nothing.
  if (cd .claude/scripts && "${PY[@]}" -m unittest "$case" >/dev/null 2>&1); then
    echo "passed before the fix: $case" >&2; git checkout -q HEAD -- "$file"; exit 1
  fi
  "${PY[@]}" staging/patch_review.py . fix
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_pr_helpers); then
    echo "the suite fails after the fix" >&2; git checkout -q HEAD -- "$file"; exit 1
  fi
  git add "$file"
  git commit -q -F - "${trailers[@]}" <<'MSG'
test(harness): the pinned gh calls drop comments before joining continued lines (review R1, R2)

R1: the pin joined backslash continuations before it dropped comments, while
bash continues no comment line, so a comment ending in a backslash would
swallow the gh call after it and the pin would stop covering that call. The
join now lives in code_lines, which drops comments first, and
test_a_comment_ending_in_a_backslash_swallows_no_call pins it: it failed on
the old order and passes on the new one, which apply.sh checks.

R2: _gh_printing's comment quoted the files query this branch replaced, so it
now names the shape instead.
MSG

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for review round 1" \
    -m "Its fix is the commit before this one." "${trailers[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}

main "$@"
exit
