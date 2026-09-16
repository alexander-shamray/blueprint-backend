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

# `--state all`, because the sweeps de-duplicate against closed issues too: a
# finding already filed and fixed must not be re-filed. `--limit 1000`, because
# the default 30 hides older issues and a de-duplication gate that cannot see an
# issue reports a duplicate as new.
gh issue list --state all --limit 1000 --json number,title,state,labels
