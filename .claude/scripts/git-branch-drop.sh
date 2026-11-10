#!/usr/bin/env bash
# Delete a local branch once it has landed, by `/ship` step 0's predicate, or
# is a superseded sync's; but for a failed fork's own, the only route, as raw
# `git branch` deletes stay denied. Its guards, compare-and-swap and exit codes
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
ended="merged into main"
if ! { [[ "$landed" =~ ^[0-9]+$ ]] && [ "$landed" -gt 0 ]; }; then
  # A sync head and no other name: harness_sync closes a superseded sync with
  # --delete-branch, so no merge can land it and nobody can reopen it. A row
  # that is not closed, or a tip no closed row holds, is work this cannot weigh.
  [[ "$branch" =~ ^chore/harness-sync-[0-9a-f]{7}$ ]] ||
    { echo "$branch has not landed: no pull request merged it into main at its tip $tip; it is kept" >&2
      exit 7; }
  closed=$(printf '%s' "$rows" | jq --arg tip "$tip" \
    'all(.[]; .state == "CLOSED") and any(.[]; .baseRefName == "main" and .headRefOid == $tip)') ||
    { echo "cannot read pr-for-branch.sh's answer for $branch, so whether it was superseded is unknown; it is kept" >&2
      exit 12; }
  [ "${closed%$'\r'}" = true ] ||
    { echo "$branch has not landed, nor is every pull request for it closed with one at its tip $tip; it is kept" >&2
      exit 7; }
  # Read from origin: a remote-tracking ref outlives the branch until a prune.
  held=$(git ls-remote --heads origin "refs/heads/$branch") ||
    { echo "cannot ask origin whether it still has $branch; it is kept" >&2; exit 12; }
  [ -z "$held" ] ||
    { echo "$branch is still on origin, so its pull request can be reopened; it is kept" >&2; exit 7; }
  ended="superseded: its pull request closed and origin's branch gone,"
fi

named() {
  tr -d '\r' < "$1" | tr '[:upper:]' '[:lower:]'
}

# What holds the branch, in words, or nothing; 12 when that cannot be read. Every
# caller tests it, which turns `set -e` off inside it, so each read says `return`.
holder() {
  # `update-ref` does not ask, as `branch -D` would, whether a worktree has the
  # branch checked out, so this is the only check. Folded, so a worktree on
  # `Feat/x` keeps `feat/x` on a filesystem where the two are one ref.
  local worktrees held common gitdir state text
  worktrees=$(git worktree list --porcelain) || return 12
  # Held apart from the count, whose `|| true` for no match would swallow this.
  worktrees=$(printf '%s\n' "$worktrees" | tr -d '\r' | tr '[:upper:]' '[:lower:]') || return 12
  held=$(grep -cxF -- "branch refs/heads/$lowered" < <(printf '%s\n' "$worktrees") || true)
  [ "$held" = 0 ] || { echo "checked out in a worktree (git worktree list)"; return 0; }

  # A worktree mid-rebase or mid-bisect has HEAD detached while the branch is
  # still the one it works on, as a conflicted `git-rebase-onto-main.sh start`
  # leaves it. git keeps the name in each git dir's rebase-merge/head-name,
  # rebase-apply/head-name, BISECT_START and rebase-merge/update-refs, where
  # `git branch -D` looks too, from 2.38. Read here from every git dir, folded.
  common=$(git rev-parse --git-common-dir) || return 12
  for gitdir in "$common" "$common"/worktrees/*; do
    [ -d "$gitdir" ] || continue
    for state in rebase-merge rebase-apply; do
      if [ -f "$gitdir/$state/head-name" ]; then
        text=$(named "$gitdir/$state/head-name") || return 12
        if [ "$text" = "refs/heads/$lowered" ]; then
          echo "being rebased in a worktree ($gitdir/$state)"; return 0
        fi
      fi
    done
    # `rebase --update-refs` names each stacked branch it will move here, one
    # line per ref name followed by its before and after oids, so a whole-line
    # match on the name is the test; an oid line never reads as a ref name.
    if [ -f "$gitdir/rebase-merge/update-refs" ]; then
      text=$(named "$gitdir/rebase-merge/update-refs") || return 12
      if grep -qxF -- "refs/heads/$lowered" < <(printf '%s\n' "$text"); then
        echo "to be updated by a rebase in a worktree ($gitdir/rebase-merge)"; return 0
      fi
    fi
    if [ -f "$gitdir/BISECT_START" ]; then
      text=$(named "$gitdir/BISECT_START") || return 12
      if [ "$text" = "$lowered" ] || [ "$text" = "refs/heads/$lowered" ]; then
        echo "being bisected in a worktree ($gitdir)"; return 0
      fi
    fi
  done
}

# Last before the delete, and once more after it.
why=$(holder) ||
  { echo "cannot read the worktrees or their state, so whether $branch is in use is unknown; it is kept" >&2; exit 12; }
[ -z "$why" ] || { echo "$branch is $why: it is in use, and kept" >&2; exit 4; }

git update-ref --no-deref -d "refs/heads/$branch" "$tip" ||
  { echo "$branch moved from $tip after it was checked, or could not be deleted; it is kept" >&2; exit 7; }

# The read above and the delete are two steps, so a worktree can take the branch
# between them. The tip was only just there, so it goes back rather than leaving
# that worktree on a branch that no longer exists.
code=0
why=$(holder) || code=$?
if [ "$code" -ne 0 ] || [ -n "$why" ]; then
  git update-ref --no-deref "refs/heads/$branch" "$tip" "" ||
    { echo "$branch was deleted while a worktree took it, and could not be put back at $tip" >&2
      echo "if it is still absent: git branch $branch $tip" >&2
      exit 4; }
  [ "$code" -eq 0 ] ||
    { echo "cannot read the worktrees or their state, so whether $branch is in use is unknown; it is kept" >&2; exit 12; }
  echo "$branch is $why: it is in use, and kept" >&2; exit 4
fi
git config --remove-section "branch.$branch" 2>/dev/null || true
echo "dropped $branch, $ended at $tip"
