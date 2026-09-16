#!/usr/bin/env bash
# Read a PR's merge-relevant state as JSON — /ship's only `gh pr view` route.
# Read-only, fixed field set.
#
# ship.md reads through this rather than holding a `gh pr view` grant, which
# reaches `--json reviews` and `--json comments` unfiltered. /ship invokes
# /review-copilot as a skill, and `allowed-tools` entries are cumulative
# auto-approvals rather than a whitelist, so a broad grant here would reopen
# the feeds on the unattended path; with no such grant in either file, no
# inheritance rule leaves an unfiltered route.
#
# The field set is the union of ship.md's reads — the resume table's `state`,
# step 7's pre-merge check and its post-merge confirmation — fixed rather than
# a parameter, because a caller that chooses fields can choose `reviews`.
set -euo pipefail
pr="${1:?usage: pr-state.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh pr view "$pr" --json state,mergeable,mergeStateStatus,headRefOid,mergeCommit
