#!/usr/bin/env bash
# Switch the checkout to an existing local branch, as /ship does to reach `main`.
# A prefix grant on `git switch` also licenses `-C` and `--discard-changes` past
# the denies on their other spellings, and the flags combine (`-fC`) beyond what
# a deny list can close, so no flags reach git: `--` ends option parsing.
set -euo pipefail
branch="${1:?usage: git-switch-existing.sh <branch>}"
[ "$#" -eq 1 ] || { echo "exactly one argument: the branch name" >&2; exit 2; }
# Branch names here are <type>/<kebab>, and feat(scope)/ carries parentheses.
# Leading '-' is refused outright so nothing can arrive looking like a flag,
# and '..' is refused because a refname may not contain it.
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# It must already exist as a local branch, or the helper would detach onto a
# tag or a commit that satisfied the pattern above.
git show-ref --verify --quiet "refs/heads/$branch" ||
  { echo "no such local branch: $branch" >&2; exit 3; }
git switch -- "$branch"
