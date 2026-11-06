#!/usr/bin/env bash
# Delete one local branch once it has landed: `/ship` step 0's finished
# predicate, made the only route to deleting a branch, whose raw `git branch`
# spellings stay denied. Its guards, its compare-and-swap and its exit codes
# are `docs/harness-boundaries.md`'s, *Grant inventory and push helpers*.
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: git-branch-drop.sh <branch>" >&2; exit 2; }
branch="$1"
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
# pr-for-branch.sh's own shape, which it would refuse the name by in any case.
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }

# By name, before anything reads the checkout, folded and spelled out as
# git-rebase-onto-main.sh does: on this host's case-insensitive filesystem
# `Main` is `main`'s ref, and one string compare is one spelling.
lowered=$(printf %s "$branch" | tr '[:upper:]' '[:lower:]')
case "$lowered" in
  main|heads/main|refs/heads/main|origin/main|refs/remotes/origin/main)
    echo "refusing to delete main, spelled $branch" >&2; exit 3 ;;
esac

# A symbolic ref is not a branch, and is refused before its tip is read:
# `rev-parse` follows it, so a refs/heads/<name> pointing at `main` reads
# main's tip, and `update-ref -d` without `--no-deref` would delete main
# through it. `branch -D` would have deleted the link; nothing here does.
if git symbolic-ref -q "refs/heads/$branch" > /dev/null 2>&1; then
  echo "$branch is a symbolic ref, not a branch: this helper will not take it" >&2; exit 2
fi

tip=$(git rev-parse -q --verify "refs/heads/$branch^{commit}") ||
  { echo "no local branch $branch: nothing to drop" >&2; exit 6; }

# The row is pr-for-branch.sh's, which keeps only this repository's pull
# requests, so a fork's same-named one cannot land this branch. The head is
# compared with the tip read above: a commit added after the merge is work no
# merge holds, and a branch merged into anything but `main` has not landed.
rows=$(bash "$(dirname "${BASH_SOURCE[0]}")/pr-for-branch.sh" "$branch") ||
  { echo "cannot read the pull requests for $branch, so whether it landed is unknown; it is kept" >&2; exit 12; }
landed=$(printf '%s' "$rows" | jq --arg tip "$tip" \
  '[ .[] | select(.state == "MERGED" and .baseRefName == "main" and .headRefOid == $tip) ] | length') ||
  { echo "cannot read pr-for-branch.sh's answer for $branch, so whether it landed is unknown; it is kept" >&2
    exit 12; }
landed=${landed%$'\r'}
[[ "$landed" =~ ^[0-9]+$ ]] && [ "$landed" -gt 0 ] ||
  { echo "$branch has not landed: no pull request merged it into main at its tip $tip; it is kept" >&2
    exit 7; }

# Every worktree, this one included, and last before the delete: `update-ref`
# does not ask, as `branch -D` would, whether a worktree has the branch checked
# out, so this is the only check. Folded, so a worktree on `Feat/x` keeps
# `feat/x` on a filesystem where the two are one ref.
worktrees=$(git worktree list --porcelain) ||
  { echo "cannot list the worktrees, so whether $branch is checked out is unknown; it is kept" >&2; exit 12; }
held=$(printf '%s\n' "$worktrees" | tr -d '\r' | tr '[:upper:]' '[:lower:]' |
  grep -cxF -- "branch refs/heads/$lowered" || true)
[ "$held" = 0 ] ||
  { echo "$branch is checked out in a worktree (git worktree list): it is in use, and kept" >&2; exit 4; }

# A worktree mid-rebase or mid-bisect has HEAD detached while the branch is
# still the one it works on, as a conflicted `git-rebase-onto-main.sh start`
# leaves it. git keeps the name in each git dir's rebase-merge/head-name,
# rebase-apply/head-name, BISECT_START and rebase-merge/update-refs, where
# `git branch -D` looks too, from 2.38. Read here from every git dir, folded.
named() {
  tr -d '\r' < "$1" | tr '[:upper:]' '[:lower:]'
}
common=$(git rev-parse --git-common-dir) ||
  { echo "cannot find the git dirs, so whether $branch is being rebased or bisected is unknown; it is kept" >&2
    exit 12; }
for gitdir in "$common" "$common"/worktrees/*; do
  [ -d "$gitdir" ] || continue
  for state in rebase-merge rebase-apply; do
    if [ -f "$gitdir/$state/head-name" ] && [ "$(named "$gitdir/$state/head-name")" = "refs/heads/$lowered" ]; then
      echo "$branch is being rebased in a worktree ($gitdir/$state): it is in use, and kept" >&2; exit 4
    fi
  done
  # `rebase --update-refs` names each stacked branch it will move here, one
  # line per ref name followed by its before and after oids, so a whole-line
  # match on the name is the test; an oid line never reads as a ref name.
  if [ -f "$gitdir/rebase-merge/update-refs" ] &&
     named "$gitdir/rebase-merge/update-refs" | grep -qxF -- "refs/heads/$lowered"; then
    echo "$branch is to be updated by a rebase in a worktree ($gitdir/rebase-merge): it is in use, and kept" >&2
    exit 4
  fi
  if [ -f "$gitdir/BISECT_START" ]; then
    started=$(named "$gitdir/BISECT_START")
    if [ "$started" = "$lowered" ] || [ "$started" = "refs/heads/$lowered" ]; then
      echo "$branch is being bisected in a worktree ($gitdir): it is in use, and kept" >&2; exit 4
    fi
  fi
done

git update-ref --no-deref -d "refs/heads/$branch" "$tip" ||
  { echo "$branch moved from $tip after it was checked, or could not be deleted; it is kept" >&2; exit 7; }
git config --remove-section "branch.$branch" 2>/dev/null || true
echo "dropped $branch, merged into main at $tip"
