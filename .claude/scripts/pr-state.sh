#!/usr/bin/env bash
# Read a PR's merge-relevant state as JSON — /ship's only `gh pr view` route. Read-only, fixed field set.
#
# ship.md reads through this rather than holding a `gh pr view` grant, which reaches `--json reviews` and
# `--json comments` unfiltered: text anyone can post, on a run nobody is watching.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"
pr="${1:?usage: pr-state.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
# The field set is the union of ship.md's reads — the merge step's poll, its
# pre-merge check and its post-merge confirmation — fixed rather than a
# parameter, because a caller that chooses fields can choose `reviews`.
gh_read pr view "$pr" --json state,mergeable,mergeStateStatus,headRefOid,mergeCommit
