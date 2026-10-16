#!/usr/bin/env bash
# Revert one pull request on the current branch: the commits on origin/main whose `PR: #<n>` trailer names it,
# newest first, each as its own `git revert` carrying `Reverts: #<n>`. The trailer is read by git, so a line quoted in
# a body does not select a commit. History from before the trailer began has none, and is reverted by hand.
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: git-revert-pr.sh <pr-number>" >&2; exit 2; }
pr="$1"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number: $pr" >&2; exit 2; }

branch=$(git branch --show-current)
[ -n "$branch" ] && [ "$branch" != main ] ||
  { echo "not on a working branch: reverts land on a branch, never on main" >&2; exit 3; }
dirty=$(git status --porcelain)
[ -z "$dirty" ] || { echo "the tree is not clean: commit or stash first" >&2; exit 3; }

git fetch --quiet origin main
wanted="PR: #$pr"
picked=()
for sha in $(git log origin/main --topo-order --format=%H -E --grep="^PR: #$pr\$"); do
  named=$(git show -s --format=%B "$sha" | git interpret-trailers --parse | grep -E '^PR:' || true)
  if [ "$named" = "$wanted" ]; then
    picked+=("$sha")
  fi
done
[ "${#picked[@]}" -gt 0 ] ||
  { echo "no commit on origin/main carries '$wanted' as a trailer" >&2; exit 4; }

for sha in "${picked[@]}"; do
  if ! git revert --no-edit "$sha" >/dev/null; then
    git revert --abort || true
    echo "reverting $sha failed; the branch holds the reverts before it and the rest are not made" >&2; exit 5
  fi
  git commit --quiet --amend --no-edit --trailer "Reverts: #$pr"
done
echo "reverted ${#picked[@]} commit(s) of #$pr on $branch"
