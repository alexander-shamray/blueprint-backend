#!/usr/bin/env bash
# Rebase the current branch onto origin/main and publish it: the only force
# push here, denied raw in `.claude/settings.json`. Its guards, owned by this
# list, are facts about the checkout: the branch is the one in hand and not
# main, the tree is clean, the remote carries nothing the work did not start
# from, no merge holds content neither parent has, a replay that stops with
# nothing unmerged is reported and never skipped, and a retry publishes only
# on the replay and only while origin holds the leased tip.

# Four modes, because a conflict is the case rebase is here for. `start`
# leaves a conflicted rebase in progress rather than aborting it: backing out
# would send the caller to the merge commit this repository stopped making, so
# the resolution lands in the replayed commit and the history stays a line.
# `continue` publishes once the caller has resolved and staged. `publish` is
# the retry when the replay finished and only the push failed, and `abort` is
# the way back out without a raw `git rebase` grant.

# There is no `skip` mode, and nothing here skips. The merge backend, which
# `start` spells, drops a commit a resolution emptied by itself; any other
# commit a caller could skip holds content, and the chain is unattended.
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

# Both state directories, although `start` only ever makes rebase-merge: a
# rebase this helper did not start may be either, and it has to be seen to be
# refused. A function, because the answer moves: a replay that stops leaves
# state where there was none, and `stopped` has to ask again.
current_state() {
  local candidate
  for candidate in "$(git rev-parse --git-path rebase-merge)" "$(git rev-parse --git-path rebase-apply)"; do
    if [ -d "$candidate" ]; then
      printf %s "$candidate"
      return 0
    fi
  done
  return 1
}

state=""
in_progress=0
if state=$(current_state); then
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

# The branch's tip on origin itself, empty when origin has none. `publish`
# and `abort` ask this rather than the tracking ref, which a rejected push
# leaves where it was: read stale, `publish` fails at the push on every
# retry and `abort` keeps a record it cannot finish, each naming the other.
remote_tip() {
  local listed
  listed=$(git ls-remote origin "refs/heads/$branch") ||
    { echo "cannot ask origin where $branch is, so whether the replay can still be published is unknown" >&2
      exit 12; }
  printf '%s\n' "$listed" | awk -v ref="refs/heads/$branch" '$2 == ref { print $1 }'
}

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
# between is refused rather than overwritten. Both sides of the refspec are
# spelled, so no `remote.origin.push` mapping can send it to another ref the
# lease does not name.
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
  git push --force-with-lease="refs/heads/$branch:$lease" origin "refs/heads/$branch:refs/heads/$branch"
  rm -f "$pending"
  echo "published $branch at $head"
}

# Read from three modes. `read` returns non-zero on a record with no trailing
# newline, having assigned what it did read, so the emptiness check decides
# and the read's status never does. Every call site tests this function's
# status, which suspends `set -e` inside it; the `|| true` keeps a future
# untested call from ending on a bare exit 1 this script assigns to nothing.
# `abort` passes `abort` to be told of an unreadable record rather than
# refused by it, since clearing one is the only way past it.
recorded_branch=""
recorded_lease=""
recorded_head=""
read_pending() {
  recorded_branch=""
  recorded_lease=""
  recorded_head=""
  [ -f "$pending" ] || return 1
  read -r recorded_branch recorded_lease recorded_head < "$pending" || true
  [ -z "$recorded_branch" ] || return 0
  [ "${1:-}" != abort ] || return 2
  echo "the waiting record is unreadable; run 'abort' to clear it and start again" >&2
  exit 9
}

# Written before the replay, so it survives a push that fails after it.
remember_lease() {
  printf '%s %s\n' "$branch" "$approved_lease" > "$pending"
}

# Rewritten by `start` and `continue` once the replay has finished and before
# the push, so the record names the commit this helper produced; a retry
# never rewrites it, whatever has since been committed on top. A record still
# holding two fields is a replay that stopped or was abandoned, and without
# the head `publish` would force whatever HEAD had since become.
remember_replay() {
  printf '%s %s %s\n' "$branch" "$approved_lease" "$1" > "$pending"
}

# A rebase drops merge commits, and a merge can carry content that is in
# neither parent — a conflict resolved while merging, or an edit made while
# resolving. Replaying such a branch loses it before the push, which no lease
# can see. `--cc` shows only what differs from every parent, so an ordinary
# merge-forward prints nothing and is flattened without complaint, and one
# that invented something stops the run. `/ship` step 0 asks ancestry against
# the head a pull request merged rather than content, so this is the one read.
require_no_merge_invented_anything() {
  local invented
  invented=$(git log --merges --cc --format="" "refs/remotes/origin/main..HEAD")
  [ -z "$invented" ] ||
    { echo "a merge on $branch carries content neither parent has, and a replay would drop it:" >&2
      git log --merges --oneline "refs/remotes/origin/main..HEAD" >&2
      echo "land or re-commit that content before rebasing" >&2
      exit 10; }
}

# A replay stops in one of two states: something unmerged, which is the
# conflict `continue` exists for, or a stop this helper cannot name, which is
# reported and never skipped. The merge backend drops a commit a resolution
# emptied without asking, and reschedules a pick it could not apply, so no
# state here is one this helper may drop a commit from. The marker is what
# `continue` and `abort` look for; git removes the state directory when the
# rebase ends however it ends, and a rebase somebody ran by hand lacks it.
stopped() {
  local now unmerged untracked
  now=$(current_state) ||
    { # No state left, so nothing to continue. The record here is the
      # two-field one `remember_lease` wrote, which `publish` refuses, and
      # left behind it would refuse every later `start` on any branch.
      rm -f "$pending"
      if [ "$in_progress" -eq 1 ]; then
        echo "the replay of $branch ended without finishing, so there is nothing to continue;" >&2
      else
        echo "the rebase did not start, so there is nothing to continue;" >&2
      fi
      echo "git's own message is above" >&2
      exit 11; }
  # Checked, because an unwritten marker wedges the rebase: `continue` and
  # `abort` both refuse one without it, and no raw `git rebase` is granted.
  : > "$now/started-by-this-helper" ||
    { echo "cannot mark $now as this helper's, so neither 'continue' nor 'abort'" >&2
      echo "would accept the rebase afterwards; the replay is left where it is" >&2
      exit 14; }

  unmerged=$(git diff --name-only --diff-filter=U)
  if [ -n "$unmerged" ]; then
    echo "the rebase onto origin/main conflicts and is left in progress, which is the point:" >&2
    printf '%s\n' "$unmerged" >&2
    echo "resolve these, 'git add' them, then run this helper again with 'continue'" >&2
    exit 8
  fi
  # Untracked files are named because they are the commonest such stop: one
  # standing where the next commit adds a file refuses the pick.
  untracked=$(git ls-files --others --exclude-standard) || untracked=""
  echo "the replay of $branch stopped with nothing unmerged, for a reason git gives above;" >&2
  if [ -n "$untracked" ]; then
    echo "these untracked files may be what stopped it:" >&2
    printf '%s\n' "$untracked" >&2
  fi
  echo "answer it, then 'continue', or 'abort'" >&2
  exit 14
}

case "$mode" in
  start)
    [ "$in_progress" -eq 0 ] ||
      { echo "a rebase is already in progress; finish it with 'continue' or leave it with 'abort'" >&2; exit 9; }
    if read_pending; then
      # Named, because the record is one per checkout and the caller may be
      # on another branch. It is not overwritten for another branch, which
      # would strand the branch it belongs to.
      echo "a replay of $recorded_branch is waiting to be published:" \
           "run 'publish' on that branch, or 'abort' there to discard it" >&2
      exit 9
    fi
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
    # side effect — both from configuration this script does not own. The
    # apply backend, `rebase.backend`'s other choice, leaves a commit it never
    # applied looking exactly like one a resolution emptied.
    git rebase --merge --no-rebase-merges --no-update-refs "refs/remotes/origin/main" || stopped
    remember_replay "$(git rev-parse HEAD)"
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
    # `start` spells `--merge`, so an apply-backend replay carrying the marker
    # is an earlier version's, and continuing it can stop where nothing here
    # would ever let it finish.
    case "$state" in
      */rebase-apply)
        echo "this replay runs on the apply backend, which this helper no longer uses: 'abort' and start again" >&2
        exit 9 ;;
    esac
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
    GIT_EDITOR=true git rebase --continue || stopped
    remember_replay "$(git rev-parse HEAD)"
    publish
    ;;

  publish)
    # The retry path. The replay finished and the push did not, so the rebase
    # state is gone and nothing else here can reach the branch: `start` reads
    # the rewritten tip as non-ancestral and `continue` finds no rebase.
    [ "$in_progress" -eq 0 ] ||
      { echo "a rebase is still in progress; finish it with 'continue'" >&2; exit 9; }
    read_pending ||
      { echo "no replay is waiting to be published" >&2; exit 9; }
    [ "$recorded_branch" = "$branch" ] ||
      { echo "the waiting replay is $recorded_branch, not $branch" >&2; exit 4; }
    # A record with no head is a run that stopped before one existed, and the
    # replay is what gets republished, never whatever the branch has become.
    [ -n "$recorded_head" ] ||
      { echo "the waiting record names no replayed commit, so the rebase never finished: 'abort' and start again" >&2
        exit 9; }
    # Commits made on top of the replay are this checkout's own and go with
    # it. A HEAD that does not descend from it is a rewrite this helper did
    # not make; clearing the record then leaves the branch where `start`
    # refuses it too, so the message names the way back first.
    git merge-base --is-ancestor "$recorded_head" HEAD ||
      { echo "HEAD does not descend from $recorded_head, the commit this helper replayed:" >&2
        echo "put that commit back under the branch and run 'publish' again, or 'abort' to give the replay up" >&2
        exit 9; }
    # The remote must still be where the guard left it. If it moved, this lease
    # was approved against a tip that no longer exists and re-approving it here
    # would be the re-read this helper exists to avoid.
    remote_now=$(remote_tip)
    [ -n "$remote_now" ] ||
      { echo "origin has no $branch any more, so there is nothing to force: 'abort' the record and push it normally" >&2
        exit 6; }
    # A push that landed while its answer was lost reads as a moved remote,
    # and is the one move that needs nothing further.
    if [ "$remote_now" = "$(git rev-parse HEAD)" ]; then
      rm -f "$pending"
      echo "origin already has $branch at $remote_now; nothing to force"
      exit 0
    fi
    # `start` refuses the rewritten branch against a moved remote as well, so
    # the message does not send the caller there.
    [ "$remote_now" = "$recorded_lease" ] ||
      { echo "origin/$branch has moved since the replay was approved, and this checkout's $branch is rewritten:" >&2
        echo "reconcile it with origin's $remote_now by hand; 'abort' only clears the record" >&2
        exit 7; }
    approved_lease="$recorded_lease"
    publish
    ;;

  abort)
    if [ "$in_progress" -eq 0 ] && [ -f "$pending" ]; then
      # The record is the only route left to a replayed branch, so clearing
      # one that belongs to another strands its rewritten tip. The name passed
      # and the branch in hand are two ways to reach the wrong record.
      readable=0
      read_pending abort || readable=$?
      # An unreadable record names no branch and no head, so `publish` can
      # never finish it and there is no tip it could still reach; left, it
      # refuses every mode on every branch.
      if [ "$readable" -eq 2 ]; then
        rm -f "$pending"
        echo "cleared an unreadable waiting record; nothing was published"
        exit 0
      fi
      [ "$readable" -eq 0 ] ||
        { echo "no replay is waiting to be abandoned" >&2; exit 9; }
      [ "$recorded_branch" = "$branch" ] ||
        { echo "the waiting replay is $recorded_branch, not $branch" >&2; exit 4; }
      current=$(git branch --show-current)
      [ "$current" = "$branch" ] ||
        { echo "on ${current:-a detached HEAD}, not $branch: this helper only ever touches the current branch" >&2
          exit 4; }
      # A record `publish` can still finish is kept, and one it cannot is
      # cleared, asked the way `publish` asks it: HEAD descending from the
      # replay and origin's own tip at the lease. Any other test leaves a
      # state where each mode names the other, or clears a record `publish`
      # would have finished. A remote that cannot be asked stops the run
      # rather than reading as a dead lease.
      if [ -n "$recorded_head" ]; then
        remote_now=""
        if git merge-base --is-ancestor "$recorded_head" HEAD; then
          remote_now=$(remote_tip)
        fi
        [ "$remote_now" != "$recorded_lease" ] ||
          { echo "the replay of $branch is under HEAD and 'publish' can finish it; refusing to strand it" >&2
            exit 9; }
      fi
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
