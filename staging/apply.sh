#!/usr/bin/env bash
# Apply review round 2's two findings on fix/argv-guards-decode-utf8: a docstring and test comments the fallback
# change made false. Nothing here changes what the guard decides, so the suites are the check. The push is /ship's.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/argv-guards-decode-utf8
  [ -f staging/patch_comments.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local trailers=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01VsGF8Tb74uv1Y16xLCdUyB" --trailer "PR: #1345")
  local files=".claude/hooks/guard-git-argv.py .claude/scripts/test_git_argv_guard.py"

  "${PY[@]}" staging/patch_comments.py .
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_git_argv_guard test_index_argv_guard); then
    echo "the suites fail after the comment patch" >&2; git checkout -q HEAD -- $files; exit 1
  fi
  git add $files
  git commit -q -F - "${trailers[@]}" <<'MSG'
chore(harness): the guard's docstring and test comments say the fallback refuses (review R3, R4)

R2 made the untokenised fallback refuse whatever reaches it, and four passages
still described the path it replaced. undecodable_heredoc's docstring said a
force push after an undecodable delimiter would pass the fallback; two test
names and four comments said a parse failure only degraded to the settings
deny's substring scan, or that the fallback could not see a quote-split update.
Each now says an untokenisable command is refused, the docstring within the
comment budget, since a touched block counts whole. Nothing the guard decides
changes; both guard suites pass.
MSG

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for review round 2" \
    -m "Its comment fix is the commit before this one." "${trailers[@]}"
  "${PY[@]}" .github/comment-gate/comment_gate.py --base origin/main
  [ -z "$(git status --porcelain)" ] || { echo "tree not clean after the commits" >&2; exit 1; }
  git log --oneline -3
  echo DONE
}

main "$@"
exit
