#!/usr/bin/env bash
# Cut _gh_printing's comment to the comment gate's budget on fix/pr-locality-renames-and-crlf, and commit it.
# The push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/pr-locality-renames-and-crlf
  [ -f staging/patch_comment.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local trailers=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #1348")
  local file=.claude/scripts/test_pr_helpers.py

  "${PY[@]}" staging/patch_comment.py .
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_pr_helpers); then
    echo "the suite fails after the comment change" >&2; git checkout -q HEAD -- "$file"; exit 1
  fi
  git add "$file"
  git commit -q -m "test(harness): _gh_printing's comment fits the comment gate's budget" \
    -m "Round 1's R2 rewording touched a six-line block, which the gate counts whole against its five; the same sense now fits three lines." \
    "${trailers[@]}"

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for the comment budget" \
    -m "Its change is the commit before this one." "${trailers[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}

main "$@"
exit
