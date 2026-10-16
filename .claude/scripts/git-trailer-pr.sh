#!/usr/bin/env bash
# Add `PR: #<n>` to every commit of this branch that lacks it and publish the result under a lease: /pr's step after
# creating the pull request, so the SHA rewrite lands before the first review round. A raw `git rebase -x` grant
# would take any command (`docs/harness-boundaries.md`); this takes a number and spells the exec itself.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"

[ "$#" -eq 1 ] || { echo "usage: git-trailer-pr.sh <pr-number>" >&2; exit 2; }
pr="$1"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number: $pr" >&2; exit 2; }

branch=$(git branch --show-current)
[ -n "$branch" ] && [ "$branch" != main ] ||
  { echo "not on a PR branch: nothing to add the trailer to" >&2; exit 3; }
dirty=$(git status --porcelain)
[ -z "$dirty" ] || { echo "the tree is not clean: commit or stash first" >&2; exit 3; }

# The number is the caller's, so it is bound to this branch: a trailer naming another pull request is the one a
# revert helper would act on.
row=$(gh_read pr view "$pr" --json state,headRefName,baseRefName,isCrossRepository \
  --jq '[.state, .headRefName, .baseRefName, (.isCrossRepository|tostring)] | @tsv') ||
  { echo "cannot read pull request #$pr" >&2; exit 3; }
IFS=$'\t' read -r state head_branch base cross <<<"${row%$'\r'}"
[ "$state" = OPEN ] || { echo "pull request #$pr is $state, not OPEN" >&2; exit 3; }
[ "$head_branch" = "$branch" ] || { echo "pull request #$pr is for $head_branch, not $branch" >&2; exit 3; }
[ "$base" = main ] && [ "$cross" = false ] || { echo "pull request #$pr is not a same-repository one into main" >&2; exit 3; }

# Rewriting is only safe over what origin already holds: an unpushed commit would be published by the lease, and an
# unseen one discarded by it.
git fetch --quiet origin main "$branch"
remote=$(git rev-parse "refs/remotes/origin/$branch")
tip=$(git rev-parse HEAD)
# A rewrite whose push failed leaves the same trees under new messages, so origin's tip is then the lease to
# publish against. Any other difference is work this checkout lacks or has not pushed.
if [ "$remote" != "$tip" ]; then
  [ "$(git rev-parse "$remote^{tree}")" = "$(git rev-parse "$tip^{tree}")" ] &&
    [ "$(git rev-list --count "origin/main..$remote")" = "$(git rev-list --count origin/main..HEAD)" ] ||
    { echo "origin/$branch is $remote, not the local $tip: push or pull first" >&2; exit 4; }
fi

base_oid=$(git merge-base origin/main HEAD)
merges=$(git rev-list --merges "$base_oid..HEAD")
[ -z "$merges" ] || { echo "the branch holds a merge commit; the commit gate refuses it, so there is nothing to add" >&2; exit 5; }

wanted="PR: #$pr"
owned=0
for sha in $(git rev-list "$base_oid..HEAD"); do
  named=$(git show -s --format=%B "$sha" | git interpret-trailers --parse | grep -E '^PR:' || true)
  if [ -z "$named" ]; then
    continue
  elif [ "$named" = "$wanted" ]; then
    owned=$((owned + 1))
  else
    echo "$sha carries '$named', not '$wanted'" >&2; exit 5
  fi
done
total=$(git rev-list --count "$base_oid..HEAD")
if [ "$owned" -eq "$total" ] && [ "$remote" = "$tip" ]; then
  echo "every commit already carries $wanted"; exit 0
fi

# The exec skips a commit that has the trailer, so a rerun after a failed push changes nothing already done.
if [ "$owned" -lt "$total" ]; then
  exec_cmd="git log -1 --format=%B | git interpret-trailers --parse | grep -qx '$wanted' || git commit --amend --no-edit --trailer '$wanted'"
  if ! GIT_SEQUENCE_EDITOR=true git rebase --quiet -x "$exec_cmd" "$base_oid"; then
    git rebase --abort || true
    echo "adding the trailer failed; the branch is as it was" >&2; exit 6
  fi
fi

git push --quiet --force-with-lease="refs/heads/$branch:$remote" origin "HEAD:refs/heads/$branch" ||
  { echo "the push failed; the local branch carries the trailers, origin's does not. Rerun once origin answers" >&2; exit 7; }
echo "added $wanted to $((total - owned)) commit(s) and published $branch at $(git rev-parse HEAD)"
