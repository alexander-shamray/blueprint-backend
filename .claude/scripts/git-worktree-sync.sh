#!/usr/bin/env bash
# Open /ship's worktree on a sync pull request's branch, and nothing else:
# `git fetch origin <branch>`, then `git worktree add --track -b <branch>
# <path> origin/<branch>`. A prefix grant on `git worktree add` buys `-B`, which
# resets a branch past the `git branch --force` deny; the branch is a sync head.
set -euo pipefail
[ "$#" -eq 2 ] || { echo "usage: git-worktree-sync.sh <path> <branch>" >&2; exit 2; }
path="$1"
branch="$2"
# Established first because the checks below read `git rev-parse` inside `[ ]`,
# where a failure is an empty string that `set -e` never sees.
git rev-parse --git-dir >/dev/null 2>&1 ||
  { echo "not in a git repository" >&2; exit 2; }
# The whole of pr.py's SYNC_HEAD, anchored at both ends and held equal to it by
# harness_sync/tests/test_sync_pr_constants.py. A leading '-' cannot match, so
# nothing the caller passes arrives as a flag.
[[ "$branch" =~ ^chore/harness-sync-[0-9a-f]{7}$ ]] ||
  { echo "not a sync branch: $branch" >&2; exit 2; }
# git-worktree-fork.sh's shape and checks: the one location EnterWorktree moves
# the session into without a confirmation, and a relative path that means the
# main checkout only when run from its root.
[[ "$path" =~ ^\.claude/worktrees/[A-Za-z0-9][A-Za-z0-9._-]*$ ]] ||
  { echo "path must be .claude/worktrees/<name>" >&2; exit 2; }
[ -z "$(git rev-parse --show-prefix)" ] ||
  { echo "run from the checkout root" >&2; exit 2; }
[ "$(git rev-parse --git-dir)" = "$(git rev-parse --git-common-dir)" ] ||
  { echo "run from the main checkout, not a linked worktree" >&2; exit 2; }
git check-ignore -q "$path" ||
  { echo "$path is not ignored — add .claude/worktrees/ to .gitignore" >&2; exit 2; }
[ ! -e "$path" ] || { echo "path already exists: $path" >&2; exit 2; }
# It must not exist: refusing here is what makes the missing -B harmless, since
# a caller cannot reset a branch by passing its name.
! git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "branch already exists: $branch" >&2; exit 3; }
# A failed fetch stops here even where a stale origin/<branch> is left, so the
# start point below is always the tip just read.
git fetch -q origin "$branch" ||
  { echo "origin has no such branch: $branch" >&2; exit 4; }
# `--track` sets the upstream, which a sync run reads only as `git status -sb`'s
# ahead/behind; the start point is origin's tip, not the local `main` a bare name is.
git worktree add --track -b "$branch" "$path" "origin/$branch"
