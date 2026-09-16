#!/usr/bin/env bash
# Fork the sibling worktree /branch step 5 creates, and nothing else.
#
# The whole command is fixed: `git worktree add --no-track -b <branch> <path>
# origin/main`. A prefix grant on `git worktree add` also buys `-B`, which
# resets an existing branch to the start point past the `git branch --force`
# and `git branch -M` denies, and a prefix rule cannot exclude a flag.
#
# `--no-track` is fixed because the start point is a remote-tracking ref, so
# without it the new branch's upstream becomes origin/main and /pr never sets
# the right one. origin/main is fixed because step 5 forks only from the
# fetched base.
set -euo pipefail
[ "$#" -eq 2 ] || { echo "usage: git-worktree-fork.sh <path> <branch>" >&2; exit 2; }
path="$1"
branch="$2"
# A sibling of this checkout, which is the only shape step 5 creates. Enforced
# here as well as there so the helper reads safely on its own terms: neither
# argument can begin with '-', so nothing a caller passes arrives as a flag.
[[ "$path" =~ ^\.\./[A-Za-z0-9][A-Za-z0-9._-]*$ ]] ||
  { echo "path must be a sibling of the checkout: ../<name>" >&2; exit 2; }
[ ! -e "$path" ] || { echo "path already exists: $path" >&2; exit 2; }
# Branch names here are <type>/<kebab>, and feat(scope)/ carries parentheses.
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# It must not exist: refusing here is what makes the missing -B harmless
# rather than merely unavailable, since a caller cannot reset a branch by
# passing its name.
! git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "branch already exists: $branch" >&2; exit 3; }
git show-ref --verify --quiet refs/remotes/origin/main ||
  { echo "no refs/remotes/origin/main — fetch first (step 1)" >&2; exit 4; }
git worktree add --no-track -b "$branch" "$path" origin/main
