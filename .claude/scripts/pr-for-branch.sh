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
. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"
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
repo=$(gh_read repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
# Blank counts as missing, and `||` does not see it: `gh` printing an empty
# string exits 0, and the comparison below would then match every row whose
# head repository is absent, as a deleted fork's is, admitting a stranger's
# pull request precisely when its owner cannot be established.
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }
# The listing is bounded where the field set was not: gh's default page is 30
# rows, cut before the filter below. A response of exactly the limit may be a
# prefix, so it is refused rather than returned.
limit=1000
rows=$(gh_read pr list --state all --head "$branch" --limit "$limit" \
         --json number,state,url,headRepository,headRefOid,baseRefName,mergeCommit)
count=$(printf '%s\n' "$rows" | jq length | tr -d '\r') || count=""
[[ "$count" =~ ^[0-9]+$ ]] ||
  { echo "the listing for $branch did not arrive as a JSON array" >&2; exit 3; }
[ "$count" -lt "$limit" ] ||
  { echo "the listing for $branch holds $limit rows, so it may be truncated" >&2; exit 4; }
# Only pull requests into main are this chain's: every one it opens targets main.
mine=$(printf '%s\n' "$rows" | jq --arg repo "$repo" \
  '[ .[] | select((.headRepository.nameWithOwner // "") == $repo)
         | select(.baseRefName == "main") ]')
# A MERGED row whose merge commit the local branch carries is an earlier
# incarnation of a reused name, and goes. Without that branch or commit locally
# the row stays: stopping is the answer when the drop cannot be shown.
dropped=""
tip=$(git rev-parse --verify --quiet "refs/heads/$branch" || true)
if [ -n "$tip" ]; then
  merged=$(printf '%s\n' "$mine" | jq -r '.[] | select(.state == "MERGED")
                  | [.number, (.mergeCommit.oid // "")] | @tsv')
  while IFS=$'\t' read -r number oid; do
    oid="${oid%$'\r'}"
    [ -z "$oid" ] || ! git merge-base --is-ancestor "$oid" "$tip" 2>/dev/null ||
      dropped="$dropped${number%$'\r'},"
  done < <(printf '%s\n' "$merged")
fi
printf '%s\n' "$mine" | jq --argjson dropped "[${dropped%,}]" \
  '[ .[] | select(.number as $n | $dropped | index($n) | not)
         | {number, state, url, headRefOid, baseRefName} ]'
