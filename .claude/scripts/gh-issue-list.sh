#!/usr/bin/env bash
# Every issue a sweep de-duplicates against, with a fixed field set.
#
# A prefix grant on `gh issue list` lets the caller ask for `--json author`,
# the one field the issue helpers exist to withhold, or dump every body at
# once. So the field set is spelled here and the caller chooses nothing, the
# shape `pr-for-branch.sh` uses.
#
# `labels` stays because the sweeps apply and read them; `author` does not,
# because authorship is `gh-issue-suppresses.sh`'s to decide in code, and
# `body` does not, because `gh-issue-text.sh` hands one over at a time.
set -euo pipefail

[ "$#" -eq 0 ] ||
  { echo "usage: gh-issue-list.sh   (no arguments)" >&2; exit 2; }

# `--state all`, because the sweeps de-duplicate against closed issues too. gh
# cuts a list at `--limit` without a word, and a de-duplication gate that cannot
# see an issue reports a duplicate as new, so a list as long as the limit is
# refused rather than read as whole.
limit=5000
list=$(gh issue list --state all --limit "$limit" --json number,title,state,labels)
count=$(jq length <<<"$list" | tr -d '\r')
[ "$count" -lt "$limit" ] ||
  { echo "gh-issue-list: $count issues is the limit, so the list may be cut; raise it here" >&2; exit 3; }
printf '%s\n' "$list"
