#!/usr/bin/env bash
# Unstage paths, and nothing else: `git reset HEAD -- <pathspec>...`.
#
# No grant narrows to this: `Bash(git reset HEAD --:*)` is a prefix match, so
# it admits `git reset HEAD --hard`, and the `git reset --hard` deny matches
# only the other word order. A prefix rule cannot say "and then a space"; here
# the separator is written, every argument lands after it, and no argument may
# begin with '-', so `--hard` cannot arrive as a flag.
set -euo pipefail
[ "$#" -ge 1 ] || { echo "usage: git-unstage.sh <path>..." >&2; exit 2; }
for p in "$@"; do
  case "$p" in
    -*) echo "path may not start with '-': $p" >&2; exit 2 ;;
    *) : ;;
  esac
done
git reset HEAD -- "$@"
