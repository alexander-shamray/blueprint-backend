#!/usr/bin/env bash
# Bring the current branch up to date with origin/main by rebasing it, and
# publish the result. This is the only force push in this repository.
#
# The helper exists because a permission rule cannot express the thing that
# makes the push safe. A rule matches the text of a command; "the branch you
# are standing on", "not main" and "the remote holds nothing this would
# discard" are facts about the checkout, and only a script can read them.
# `.claude/settings.json` denies the raw force push and that deny is untouched:
# the flags below are this file's and never a caller's.
#
# Three modes, because a conflict is the case rebase is here for. `start`
# leaves a conflicted rebase **in progress** rather than aborting it — backing
# out would send the caller to the merge commit this repository stopped making
# — so the resolution lands in the replayed commit and the history stays a
# line. `continue` publishes once the caller has resolved and staged; `abort`
# is the way back out without a raw `git rebase` grant.
#
# The lease names an expected value rather than being left bare.
# `--force-with-lease` alone trusts the remote-tracking ref, which any fetch in
# the session may have moved; `--force-with-lease=<ref>:<sha>` names the commit
# this run read, so a push landing in between is refused, not overwritten.
#
# A lease is not consent, which is why there is a second check. Another
# session's commits that this checkout has already fetched satisfy the lease
# and would still be discarded, so a remote carrying anything the branch's work
# did not start from stops the run — and stops it **before** the replay, since
# a refusal after the rewrite leaves the caller somewhere neither of them
# chose. The replay's own starting point is what it is measured against,
# because after a rebase the published commits are no longer ancestors of
# anything: the old tip and the new one share only history before the base.
set -euo pipefail

[ "$#" -eq 2 ] ||
  { echo "usage: git-rebase-onto-main.sh <branch> <start|continue|abort>" >&2; exit 2; }
branch="$1"
mode="$2"

case "$mode" in
  start|continue|abort) ;;
  *) echo "mode must be start, continue or abort, not: $mode" >&2; exit 2 ;;
esac
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
  *..*) echo "branch name may not contain '..'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }

# By name, before anything reads the checkout. The current-branch test below
# would catch it too, but only while the session happens to be elsewhere, and a
# refusal that depends on where you are standing is not a refusal.
[ "$branch" != main ] ||
  { echo "refusing to rebase or force-push main" >&2; exit 3; }

# Both backends, because which one runs is git's choice and not this file's:
# the merge backend is the default and the am backend still appears behind
# `--apply` and in older versions.
state=""
for candidate in "$(git rev-parse --git-path rebase-merge)" "$(git rev-parse --git-path rebase-apply)"; do
  if [ -d "$candidate" ]; then
    state="$candidate"
    break
  fi
done
in_progress=0
if [ -n "$state" ]; then
  in_progress=1
fi

# The branch under a rebase is not the current branch: HEAD is detached while
# the replay runs, so `git branch --show-current` answers nothing. git records
# the name it will restore, and the commit the replay started from, and both
# are read from the same place.
rebase_branch=""
started_from=""
if [ -n "$state" ]; then
  if [ -f "$state/head-name" ]; then
    rebase_branch=$(sed 's|^refs/heads/||' "$state/head-name")
  fi
  for original in "$state/orig-head" "$state/head"; do
    if [ -f "$original" ]; then
      started_from=$(cat "$original")
      break
    fi
  done
fi

require_remote_branch() {
  git show-ref --verify --quiet "refs/remotes/origin/$branch" ||
    { echo "origin has no $branch: push it normally first, since there is nothing to force" >&2; exit 6; }
}

# $1 is the commit this branch's work started from. See the header.
require_remote_carries_nothing_new() {
  local published
  published=$(git rev-parse "refs/remotes/origin/$branch")
  git merge-base --is-ancestor "$published" "$1" ||
    { echo "origin/$branch carries commits this checkout did not start from: look before publishing over them" >&2
      exit 7; }
}

publish() {
  local current head lease
  current=$(git branch --show-current)
  [ "$current" = "$branch" ] ||
    { echo "the replay ended on ${current:-a detached HEAD} rather than $branch; nothing is published" >&2; exit 4; }
  lease=$(git rev-parse "refs/remotes/origin/$branch")
  head=$(git rev-parse HEAD)
  if [ "$head" = "$lease" ]; then
    echo "already published at $head; nothing to force"
    exit 0
  fi
  git push --force-with-lease="$branch:$lease" origin "$branch"
  echo "published $branch at $head"
}

conflicted() {
  echo "the rebase onto origin/main conflicts and is left in progress, which is the point:" >&2
  git diff --name-only --diff-filter=U >&2
  echo "resolve these, 'git add' them, then run this helper again with 'continue'" >&2
  exit 8
}

case "$mode" in
  start)
    [ "$in_progress" -eq 0 ] ||
      { echo "a rebase is already in progress; finish it with 'continue' or leave it with 'abort'" >&2; exit 9; }
    current=$(git branch --show-current)
    [ -n "$current" ] ||
      { echo "detached HEAD: there is no branch to publish" >&2; exit 4; }
    [ "$current" = "$branch" ] ||
      { echo "on $current, not $branch: this helper only ever touches the current branch" >&2; exit 4; }
    [ -z "$(git status --porcelain)" ] ||
      { echo "the tree is dirty; a rebase would carry or refuse it, and neither is this helper's call" >&2; exit 5; }

    git fetch origin
    git show-ref --verify --quiet refs/remotes/origin/main ||
      { echo "no refs/remotes/origin/main to rebase onto" >&2; exit 6; }
    require_remote_branch
    require_remote_carries_nothing_new "$(git rev-parse HEAD)"

    git rebase "refs/remotes/origin/main" || conflicted
    publish
    ;;

  continue)
    [ "$in_progress" -eq 1 ] ||
      { echo "no rebase is in progress: 'continue' has nothing to finish" >&2; exit 9; }
    [ "$rebase_branch" = "$branch" ] ||
      { echo "the rebase in progress is ${rebase_branch:-unreadable}, not $branch" >&2; exit 4; }
    [ -z "$(git diff --name-only --diff-filter=U)" ] ||
      { echo "these are still unmerged; resolve and 'git add' them first:" >&2
        git diff --name-only --diff-filter=U >&2; exit 9; }
    require_remote_branch
    [ -z "$started_from" ] ||
      require_remote_carries_nothing_new "$started_from"

    # The message is the replayed commit's own. An editor would stop the run on
    # a terminal nothing is attached to, so it is answered rather than opened.
    GIT_EDITOR=true git rebase --continue || conflicted
    publish
    ;;

  abort)
    [ "$in_progress" -eq 1 ] ||
      { echo "no rebase is in progress: 'abort' has nothing to undo" >&2; exit 9; }
    [ "$rebase_branch" = "$branch" ] ||
      { echo "the rebase in progress is ${rebase_branch:-unreadable}, not $branch" >&2; exit 4; }
    git rebase --abort
    echo "aborted; $branch is where it was and nothing was published"
    ;;
esac
