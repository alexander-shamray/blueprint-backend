#!/usr/bin/env bash
# Force-push the current branch to origin under a lease, for a branch amended or
# reworded for a reason neither git-rebase-onto-main.sh nor git-trailer-pr.sh
# covers. It takes no argument. Its guards and its exit codes are
# `docs/harness-boundaries.md`'s, *Grant inventory and push helpers*.
set -euo pipefail

[ "$#" -eq 0 ] ||
  { echo "usage: git-push-force.sh (no argument: it publishes the branch checked out)" >&2; exit 2; }

branch=$(git branch --show-current)
[ -n "$branch" ] ||
  { echo "detached HEAD: there is no branch to publish" >&2; exit 4; }

# Folded and spelled out, as git-rebase-onto-main.sh does and for its reason:
# this host's filesystem is case-insensitive, so a branch checked out as `Main`
# is `main`'s ref, and one string compare is one spelling.
lowered=$(printf %s "$branch" | tr '[:upper:]' '[:lower:]')
case "$lowered" in
  main|heads/main|refs/heads/main|origin/main|refs/remotes/origin/main)
    echo "refusing to force-push main, checked out as $branch" >&2; exit 3 ;;
esac

# Origin itself, not the tracking ref, which any earlier fetch or a branch that
# was deleted on origin may have left behind. The match is exact, because
# `ls-remote` matches a pattern by its tail.
listed=$(git ls-remote origin "refs/heads/$branch") ||
  { echo "cannot ask origin where $branch is, so nothing is forced" >&2; exit 12; }
on_origin=$(printf '%s\n' "$listed" | awk -v ref="refs/heads/$branch" '$2 == ref { print $1 }')
[ -n "$on_origin" ] ||
  { echo "origin has no $branch: push it normally first, since there is nothing to force" >&2; exit 6; }

# Fresh, and into the tracking ref by an explicit refspec, so the lease below
# is what origin held a moment ago and no `remote.origin.fetch` setting decides
# where it lands. Forced, because the remote branch being rewritten is the case
# this helper is for.
git fetch --quiet origin "+refs/heads/$branch:refs/remotes/origin/$branch" ||
  { echo "cannot fetch $branch from origin, so nothing is forced" >&2; exit 12; }
lease=$(git rev-parse --verify "refs/remotes/origin/$branch^{commit}")
head=$(git rev-parse HEAD)
if [ "$head" = "$lease" ]; then
  echo "nothing to force: origin already has $branch at $head"
  exit 0
fi

# A lease is not consent: once another session's commits have been fetched,
# they satisfy it and would be discarded. So origin's tip must be a commit this
# branch has been at: git-rebase-onto-main.sh's
# require_remote_carries_nothing_new with no fork point to measure from. A
# reflog that has expired the entry, or was never kept, fails closed.
held=$(git reflog show --format=%H "refs/heads/$branch" --) ||
  { echo "cannot read the reflog of $branch, so whether origin carries work it lacks is unknown" >&2; exit 7; }
printf '%s\n' "$held" | grep -qxF -- "$lease" ||
  { echo "origin/$branch is at $lease, a commit this checkout's $branch has never been at:" >&2
    echo "it carries work from elsewhere. Look before publishing over it; where $branch already" >&2
    echo "contains it, a plain 'git push origin $branch' needs no force" >&2
    exit 7; }

# The lease names the commit just fetched, not the tracking ref left bare, and
# both sides of the refspec are spelled, so no `remote.origin.push` mapping can
# send it to a ref the lease does not name.
git push --force-with-lease="refs/heads/$branch:$lease" origin "HEAD:refs/heads/$branch" ||
  { status=$?
    echo "origin refused the push or could not be reached; nothing was forced over what git reports above" >&2
    exit "$status"; }
echo "published $branch at $head over $lease"
