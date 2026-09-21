#!/usr/bin/env bash
# Land one of this repository's pull requests by rebase, bound to a named
# head, and nothing else: /ship step 7's one merge.
#
# A raw `gh pr merge --rebase` grant is a prefix match, so a trailing
# `--admin` sits inside it and merges past the failing checks step 7 treats
# as a stop (`docs/harness-boundaries.md`). This helper takes two positional
# arguments and spells every flag itself, so there is nowhere to put one.
#
# The method is fixed: `--squash` discards the commits /commit split.
set -euo pipefail
[ "$#" -eq 2 ] ||
  { echo "usage: gh-pr-merge.sh <pr-number> <head-oid>" >&2; exit 2; }
pr="$1"
oid="$2"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number: $pr" >&2; exit 2; }
# The oid is required rather than optional. `--match-head-commit` is the one
# guard in step 7 that fails closed: without it a push landing between the
# checks and the merge lands a commit whose checks never ran.
[[ "$oid" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "head oid must be a full 40-character sha: $oid" >&2; exit 2; }

# The repository is resolved from the checkout, never accepted, so `--repo`
# cannot be aimed elsewhere. Blank counts as missing: `gh` printing an empty
# string exits 0, and an empty `--repo` falls back to whatever the checkout
# resolves to at merge time.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 3; }

# The caller supplies the number and the oid both, so the oid alone binds
# nothing: any open pull request named with its own head would pass. The pull
# request must be the one for the branch checked out here, from this
# repository rather than a fork, into `main`, with that head.
branch=$(git branch --show-current)
[ -n "$branch" ] && [ "$branch" != main ] ||
  { echo "not on a PR branch: nothing to bind pull request #$pr to" >&2; exit 3; }
head=$(gh pr view "$pr" --repo "$repo" \
  --json headRefName,headRefOid,isCrossRepository,baseRefName \
  --jq '[.headRefName, .headRefOid, (.isCrossRepository|tostring), .baseRefName] | @tsv') ||
  { echo "cannot read pull request #$pr" >&2; exit 3; }
IFS=$'\t' read -r head_branch head_oid cross base <<<"${head%$'\r'}"
[ "$base" = main ] ||
  { echo "pull request #$pr targets $base, not main" >&2; exit 3; }
[ "$cross" = false ] ||
  { echo "pull request #$pr comes from another repository" >&2; exit 3; }
[ "$head_branch" = "$branch" ] ||
  { echo "pull request #$pr is for $head_branch, not the checked-out $branch" >&2; exit 3; }
[ "$head_oid" = "$oid" ] ||
  { echo "pull request #$pr's head is $head_oid, not $oid" >&2; exit 3; }

gh pr merge --rebase --repo "$repo" --match-head-commit "$oid" "$pr"
