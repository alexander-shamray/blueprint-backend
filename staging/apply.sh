#!/usr/bin/env bash
# Apply review round 1's two .claude/ fixes on fix/dispatched-session-keeps-its-turn and commit each. The push is
# /ship's. A failed step puts its files back, so a rerun resumes at that step.
set -euo pipefail
# A function, so bash has read all of it before the run deletes this file.
main() {
  cd "$(dirname "$0")/.."
  local branch=fix/dispatched-session-keeps-its-turn
  [ -f staging/patch_r1.py ] || { echo "no staging/ here" >&2; exit 2; }
  [ "$(git branch --show-current)" = "$branch" ] || { echo "not on $branch" >&2; exit 2; }
  [ -z "$(git status --porcelain)" ] || { echo "the tree is not clean:" >&2; git status --short >&2; exit 2; }
  if command -v py >/dev/null 2>&1; then PY=(py -3.12); else PY=(python3); fi
  local pr
  pr="$(bash .claude/scripts/pr-for-branch.sh "$branch" | "${PY[@]}" -c \
    "import json, sys; print(next((r['number'] for r in json.load(sys.stdin) if r['state'] == 'OPEN'), ''))" \
    | tr -d '\r')"
  [ -n "$pr" ] || { echo "no open pull request for $branch" >&2; exit 2; }
  TRAILERS=(--trailer "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    --trailer "Claude-Session: https://claude.ai/code/session_01QAU7oLQm5rZyEgaTxZSs5c" --trailer "PR: #$pr")

  if ! done_already "the auto classifier's to refuse"; then
    fix classifier .claude/commands/ship.md \
      "fix(harness): the dispatched clause says an ungranted read is the auto classifier's to refuse (review round 1)" \
      "The clause said each shell read is one granted command since nothing answers a prompt for one the grants do not name. An executor inherits the owner's defaultMode auto, so such a command goes to the classifier, which may approve it. What holds is that a refusal hands the change back, which is what the clause now says." \
      "docs/commands/ship.md carries the argument and cites Headless dispatched sessions in harness-boundaries.md, in this branch's earlier commit."
  fi

  if ! done_already "consequences docs/commands/ship.md spells out"; then
    fix docstring .claude/scripts/test_ship_dispatched.py \
      "fix(harness): test_ship_dispatched's docstring names the consequences docs/commands/ship.md spells out (review round 1)" \
      "The docstring said the token changes only what the design's section 6.2 lists. This branch adds a rule 6.2 does not list, that the session keeps its turn until its result is written, and docs/commands/ship.md already carried two more consequences, so the docstring now says what the section's opening says."
  fi

  # test_harness_denies wants every tracked tree edit-denied, so staging/ goes before it runs.
  git rm -r -q staging
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_harness_denies); then
    git reset -q HEAD -- staging && git checkout -q HEAD -- staging; exit 1
  fi
  git commit -q -m "chore: remove the applied staging/ for review round 1" \
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

# fix <part> <file> <subject> <body paragraphs...>
fix() {
  local part=$1 file=$2 subject=$3 message
  shift 3
  "${PY[@]}" staging/patch_r1.py . "$part"
  if ! (cd .claude/scripts && "${PY[@]}" -m unittest test_ship_dispatched test_command_runbooks test_command_grants \
      test_ship_landing_method test_pr_helpers test_system_sleep test_git_worktree_remove test_git_argv_guard \
      test_dispatch_run test_dispatch_board); then
    echo "the suites fail after: $subject" >&2; git checkout -q HEAD -- "$file"; exit 1
  fi
  # Each paragraph wrapped at 80, as /commit's bodies are; the CR strip is for Windows' print.
  message="$("${PY[@]}" -c 'import sys, textwrap
print(sys.argv[1] + "\n\n" + "\n\n".join(textwrap.fill(p, 80) for p in sys.argv[2:]))' "$subject" "$@" | tr -d '\r')"
  git add "$file"
  git commit -q -F - "${TRAILERS[@]}" <<<"$message"
}

main "$@"
exit
