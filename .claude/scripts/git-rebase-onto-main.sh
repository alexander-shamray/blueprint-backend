#!/usr/bin/env bash
# Bring the current branch up to date with origin/main by rebasing it, and
# publish the result. This is the only force push in this repository, and its
# guards live here rather than in a permission rule because each is a fact
# about the checkout: the branch is the one in hand, it is not main, the tree
# is clean, and the remote carries nothing the work did not start from.
# `.claude/settings.json` denies the raw force push, and that deny is untouched.

# Four modes, because a conflict is the case rebase is here for. `start`
# leaves a conflicted rebase in progress rather than aborting it: backing out
# would send the caller to the merge commit this repository stopped making, so
# the resolution lands in the replayed commit and the history stays a line.
# `continue` publishes once the caller has resolved and staged. `publish` is
# the retry when the replay finished and only the push failed, and `abort` is
# the way back out without a raw `git rebase` grant.
set -euo pipefail

[ "$#" -eq 2 ] ||
  { echo "usage: git-rebase-onto-main.sh <branch> <start|continue|publish|abort>" >&2; exit 2; }
branch="$1"
mode="$2"

case "$mode" in
  start|continue|publish|abort) ;;
  *) echo "mode must be start, continue, publish or abort, not: $mode" >&2; exit 2 ;;
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
#
# Folded and spelled out, because one string compare is one spelling. This
# host's filesystem is case-insensitive, so `git branch Main` answers that it
# already exists: `Main` and `main` are one ref, and `!= main` let it through.
lowered=$(printf %s "$branch" | tr '[:upper:]' '[:lower:]')
case "$lowered" in
  main|heads/main|refs/heads/main|origin/main|refs/remotes/origin/main)
    echo "refusing to rebase or force-push main, spelled $branch" >&2; exit 3 ;;
esac

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
# the name it will restore and the commit the replay started from, and both
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

# A lease is not consent: another session's commits that this checkout has
# already fetched satisfy it and would still be discarded. $1 is the commit the
# branch's work started from, which is what the remote is measured against —
# after a replay the published commits are ancestors of nothing, so this has to
# run before the rebase rather than after it.

# The value it approves is kept, and `publish` forces against that one.
# Re-reading the ref there would lease against whatever a fetch had since made
# of it, with the whole replay in between, so the lease would name the very
# commits this check refused.
approved_lease=""

# It is written down as well as held, because the push can fail after the
# replay has finished and taken the rebase state with it. Without the record
# `start` then refuses the rewritten branch as non-ancestral and `continue`
# finds no rebase, so the only granted way to publish is shut. git removes
# nothing here, so `publish` clears it on success and `abort` on the way out.
pending=$(git rev-parse --git-path claude-rebase-pending)
require_remote_carries_nothing_new() {
  approved_lease=$(git rev-parse "refs/remotes/origin/$branch")
  git merge-base --is-ancestor "$approved_lease" "$1" ||
    { echo "origin/$branch carries commits this checkout did not start from: look before publishing over them" >&2
      exit 7; }
}

# The lease names an expected value. `--force-with-lease` left bare trusts the
# remote-tracking ref, which any fetch in the session may have moved, so the
# `<ref>:<sha>` form names the commit this run read and a push landing in
# between is refused rather than overwritten.
publish() {
  local current head lease
  current=$(git branch --show-current)
  [ "$current" = "$branch" ] ||
    { echo "the replay ended on ${current:-a detached HEAD} rather than $branch; nothing is published" >&2; exit 4; }
  lease="$approved_lease"
  [ -n "$lease" ] ||
    { echo "no lease was approved, so nothing here may force" >&2; exit 7; }
  head=$(git rev-parse HEAD)
  if [ "$head" = "$lease" ]; then
    rm -f "$pending"
    echo "already published at $head; nothing to force"
    exit 0
  fi
  git push --force-with-lease="$branch:$lease" origin "$branch"
  rm -f "$pending"
  echo "published $branch at $head"
}

# Written before the replay, so it survives a push that fails after it.
remember_lease() {
  printf '%s %s\n' "$branch" "$approved_lease" > "$pending"
}

# A rebase drops merge commits, and a merge can carry content that is in
# neither parent — a conflict resolved while merging, or an edit made while
# resolving. Replaying such a branch loses it before the push, which no lease
# can see. `--cc` shows only what differs from every parent, so an ordinary
# merge-forward prints nothing and is flattened without complaint, and one
# that invented something stops the run. `/ship` step 0 reads the same thing
# for the same reason.
require_no_merge_invented_anything() {
  local invented
  invented=$(git log --merges --cc --format="" "refs/remotes/origin/main..HEAD")
  [ -z "$invented" ] ||
    { echo "a merge on $branch carries content neither parent has, and a replay would drop it:" >&2
      git log --merges --oneline "refs/remotes/origin/main..HEAD" >&2
      echo "land or re-commit that content before rebasing" >&2
      exit 10; }
}

# Only a rebase that actually stopped is left in progress. Every other way
# `git rebase` can fail — a pre-rebase hook, a refused argument — leaves no
# state at all, and saying "resolve these" there names no files and sends the
# caller to a `continue` that has nothing to finish.
#
# The marker is what `continue` looks for. git removes this directory when the
# rebase ends however it ends, so the mark cannot outlive the thing it marks,
# and a rebase somebody ran by hand does not carry it.
conflicted() {
  local now=""
  for c in "$(git rev-parse --git-path rebase-merge)" "$(git rev-parse --git-path rebase-apply)"; do
    if [ -d "$c" ]; then
      now="$c"
      break
    fi
  done
  [ -n "$now" ] ||
    { echo "the rebase did not start, so there is nothing to continue; git's own message is above" >&2; exit 11; }
  : > "$now/started-by-this-helper"
  echo "the rebase onto origin/main conflicts and is left in progress, which is the point:" >&2
  git diff --name-only --diff-filter=U >&2
  echo "resolve these, 'git add' them, then run this helper again with 'continue'" >&2
  exit 8
}

case "$mode" in
  start)
    [ "$in_progress" -eq 0 ] ||
      { echo "a rebase is already in progress; finish it with 'continue' or leave it with 'abort'" >&2; exit 9; }
    [ ! -f "$pending" ] ||
      { echo "a replay from an earlier run is waiting to be published; run 'publish', or 'abort' to discard it" >&2
        exit 9; }
    current=$(git branch --show-current)
    [ -n "$current" ] ||
      { echo "detached HEAD: there is no branch to publish" >&2; exit 4; }
    [ "$current" = "$branch" ] ||
      { echo "on $current, not $branch: this helper only ever touches the current branch" >&2; exit 4; }
    # Assigned rather than read inside `[`, where a command substitution
    # discards the exit status and a git that failed reads as a clean tree.
    dirty=$(git status --porcelain)
    [ -z "$dirty" ] ||
      { echo "the tree is dirty; a rebase would carry or refuse it, and neither is this helper's call" >&2; exit 5; }

    git fetch origin
    git show-ref --verify --quiet refs/remotes/origin/main ||
      { echo "no refs/remotes/origin/main to rebase onto" >&2; exit 6; }
    require_remote_branch
    require_remote_carries_nothing_new "$(git rev-parse HEAD)"
    require_no_merge_invented_anything
    remember_lease

    # The flags are spelled rather than inherited. `rebase.rebaseMerges` would
    # keep the merge commits this helper exists to be rid of, and
    # `rebase.updateRefs` would force-update other local branches' refs as a
    # side effect — both from configuration this script does not own.
    git rebase --no-rebase-merges --no-update-refs "refs/remotes/origin/main" || conflicted
    publish
    ;;

  continue)
    [ "$in_progress" -eq 1 ] ||
      { echo "no rebase is in progress: 'continue' has nothing to finish" >&2; exit 9; }
    [ "$rebase_branch" = "$branch" ] ||
      { echo "the rebase in progress is ${rebase_branch:-unreadable}, not $branch" >&2; exit 4; }
    # Only what `start` left behind. A rebase run by hand — an interactive one
    # dropping commits, say — passes every other check here, because the
    # published tip is what it started from; publishing its result would
    # discard the branch's work with the guards all green.
    [ -f "$state/started-by-this-helper" ] ||
      { echo "this rebase was not started by this helper, so it will not be published" >&2; exit 9; }
    unmerged=$(git diff --name-only --diff-filter=U)
    [ -z "$unmerged" ] ||
      { echo "these are still unmerged; resolve and 'git add' them first:" >&2
        printf '%s\n' "$unmerged" >&2; exit 9; }
    require_remote_branch
    # Fails closed, like the branch-name read above it. An unreadable starting
    # point means the divergence check cannot be made, and skipping it would
    # leave the lease as the only guard — which another session's already
    # fetched commits satisfy. That is the case this check exists for.
    [ -n "$started_from" ] ||
      { echo "the rebase state names no starting commit, so divergence cannot be judged: 'abort' and start again" >&2
        exit 9; }
    require_remote_carries_nothing_new "$started_from"
    remember_lease

    # The message is the replayed commit's own. An editor would stop the run on
    # a terminal nothing is attached to, so it is answered rather than opened.
    GIT_EDITOR=true git rebase --continue || conflicted
    publish
    ;;

  publish)
    # The retry path. The replay finished and the push did not, so the rebase
    # state is gone and nothing else here can reach the branch: `start` reads
    # the rewritten tip as non-ancestral and `continue` finds no rebase.
    [ "$in_progress" -eq 0 ] ||
      { echo "a rebase is still in progress; finish it with 'continue'" >&2; exit 9; }
    [ -f "$pending" ] ||
      { echo "no replay is waiting to be published" >&2; exit 9; }
    read -r recorded_branch recorded_lease < "$pending"
    [ "$recorded_branch" = "$branch" ] ||
      { echo "the waiting replay is $recorded_branch, not $branch" >&2; exit 4; }
    require_remote_branch
    # The remote must still be where the guard left it. If it moved, this lease
    # was approved against a tip that no longer exists and re-approving it here
    # would be the re-read this helper exists to avoid.
    [ "$(git rev-parse "refs/remotes/origin/$branch")" = "$recorded_lease" ] ||
      { echo "origin/$branch has moved since the replay was approved: 'abort' the record and start again" >&2
        exit 7; }
    approved_lease="$recorded_lease"
    publish
    ;;

  abort)
    if [ "$in_progress" -eq 0 ] && [ -f "$pending" ]; then
      rm -f "$pending"
      echo "cleared the waiting replay; $branch is left where it is and nothing was published"
      exit 0
    fi
    [ "$in_progress" -eq 1 ] ||
      { echo "no rebase is in progress: 'abort' has nothing to undo" >&2; exit 9; }
    [ "$rebase_branch" = "$branch" ] ||
      { echo "the rebase in progress is ${rebase_branch:-unreadable}, not $branch" >&2; exit 4; }
    [ -f "$state/started-by-this-helper" ] ||
      { echo "this rebase was not started by this helper, so it is not this helper's to undo" >&2; exit 9; }
    git rebase --abort
    rm -f "$pending"
    echo "aborted; $branch is where it was and nothing was published"
    ;;
esac
