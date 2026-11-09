#!/usr/bin/env bash
# Delete a merged sync's branch from origin, which no merge does here. It takes a
# sync head and no other name, and `dispatch_board.py sync-drop` is its caller; what
# bounds it is its own checks, not that caller. Its guards, its lease and its exit
# codes are `docs/harness-boundaries.md`'s, *Grant inventory and push helpers*.
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: git-sync-head-drop.sh <branch>" >&2; exit 2; }
branch="$1"
[[ "$branch" =~ ^chore/harness-sync-[0-9a-f]{7}$ ]] ||
  { echo "not a sync head: $branch" >&2; exit 2; }
ref="refs/heads/$branch"

# Read before origin's tip is: the tip and the delete have to name one repository,
# and `pushurl`, `insteadOf` or a second `url` can part them. This is housekeeping,
# so any rewrite keeps the branch, rather than this helper weighing which is harmless.
pushes=$(git remote get-url --push --all origin) ||
  { echo "cannot read origin's URLs; $branch is kept" >&2; exit 12; }
added=$(git config --get remote.origin.url) ||
  { echo "cannot read origin's URLs; $branch is kept" >&2; exit 12; }
fetches=$(git remote get-url origin) ||
  { echo "cannot read origin's URLs; $branch is kept" >&2; exit 12; }
[ "$pushes" = "$added" ] && [ "$fetches" = "$added" ] ||
  { echo "origin was added as '$added' but fetches from '$fetches' and pushes to '$pushes'; $branch is kept" >&2
    exit 7; }

# Whole-name: `ls-remote` matches a pattern's tail, so a/refs/heads/<branch> answers too.
listed=$(git ls-remote --heads origin "$ref") ||
  { echo "cannot ask origin for $branch; it is kept" >&2; exit 12; }
tip=$(printf '%s\n' "$listed" | tr -d '\r' | awk -v ref="$ref" '$2 == ref { print $1 }')
[ -n "$tip" ] || { echo "origin has no $branch: nothing to delete" >&2; exit 6; }

# The rows are pr-for-branch.sh's, this repository's alone. A tip no merged row
# holds is work no merge holds, and an open row is a sync still to be taken.
rows=$(bash "$(dirname "${BASH_SOURCE[0]}")/pr-for-branch.sh" "$branch") ||
  { echo "cannot read the pull requests for $branch, so whether it landed is unknown; it is kept" >&2; exit 12; }
landed=$(printf '%s' "$rows" | jq --arg tip "$tip" \
  'all(.[]; .state != "OPEN")
   and any(.[]; .state == "MERGED" and .baseRefName == "main" and .headRefOid == $tip)') ||
  { echo "cannot read pr-for-branch.sh's answer for $branch, so whether it landed is unknown; it is kept" >&2
    exit 12; }
[ "${landed%$'\r'}" = true ] ||
  { echo "no pull request merged $branch into main at origin's tip $tip, or one is still open; it is kept" >&2
    exit 7; }

# The read of the pull requests takes seconds, so the lease holds the delete to the tip checked.
git push -q --force-with-lease="$ref:$tip" origin ":$ref"
echo "deleted $branch from origin, merged into main at $tip"
