#!/usr/bin/env bash
# Switch the current checkout to an existing local branch, and nothing else.
#
# /branch step 5 needs this on one path: `git worktree add -b` creates the
# branch before the directory, so a fork that fails on an unwritable parent
# leaves the branch behind, and the in-place fallback has to get onto it. A
# prefix grant on `git switch` also licenses `--discard-changes` and `-C`,
# which discard work and force-move a branch past the denies on their other
# spellings (`git reset --hard`, `git clean`, `git branch -D/-M`).
#
# A deny list cannot close that, because the flags combine:
# `git switch -fC <name> <start>` matches no `git switch -C` rule. So no flags
# reach git: `--` ends option parsing, the name is shape-checked, and the
# branch must already exist. The only state changed is which branch is
# checked out.
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
