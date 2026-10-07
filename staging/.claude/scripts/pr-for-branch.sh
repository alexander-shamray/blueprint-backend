#!/usr/bin/env bash
# List the pull requests for one branch — number, state, url, headRefOid,
# baseRefName — and nothing else. Read-only, fixed field set.
#
# `gh pr list --json reviews,comments` returns the review bodies and issue
# comments of every pull request at once, so a `gh pr list` grant held to find
# a branch's PR bypasses the author-filtering feed helpers. The field set is
# fixed here instead. The head and the base are in it because /ship step 0
# compares the tip with the head a row MERGED into `main` records, and neither
# carries anything an author wrote beyond a ref name.
set -euo pipefail
# The branch is optional and defaults to the checkout's current branch. When
# given it is shape-checked, because it reaches an argument position: a value
# starting with `-` would be read as a flag, and `gh pr list` has flags that
# change what is returned.
branch="${1:-}"
if [ -z "$branch" ]; then
  branch=$(git branch --show-current)
  [ -n "$branch" ] || { echo "detached HEAD and no branch given" >&2; exit 2; }
fi
case "$branch" in
  -*) echo "branch name may not start with '-'" >&2; exit 2 ;;
esac
[[ "$branch" =~ ^[A-Za-z0-9][A-Za-z0-9._/()-]*$ ]] ||
  { echo "not a branch name this helper will take: $branch" >&2; exit 2; }
# `--head` matches across forks, so a stranger's same-named branch is a candidate when /ship step 0 asks whether the
# branch landed and /pr whether one is open: the head repository must also be this checkout's. Both sides are the
# filesystem's — `gh repo view` reads the checkout, and the branch came from `git branch --show-current` or was
# shape-checked above — and the value reaches jq through `--arg`, never as program text.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
# Blank counts as missing, and `||` does not see it: `gh` printing an empty
# string exits 0, and the comparison below would then match every row whose
# head repository is absent, as a deleted fork's is, admitting a stranger's
# pull request precisely when its owner cannot be established.
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }
gh pr list --state all --head "$branch" --json number,state,url,headRepository,headRefOid,baseRefName |
  jq --arg repo "$repo" \
    '[ .[] | select((.headRepository.nameWithOwner // "") == $repo)
       | {number, state, url, headRefOid, baseRefName} ]'
