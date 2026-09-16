#!/usr/bin/env bash
# Feed the closure gate — a PR's number, url, body, commits, GitHub's own
# closing-issue parse and the head oid, as JSON on stdout. Read-only, fixed
# field set.
#
# pr.md reads through this rather than holding a `gh pr view` grant. The field
# set is exactly what closure_gate.py reads, fixed because a caller that
# chooses its own fields can choose `reviews`, the unfiltered route the feed
# helpers exist to close.
#
# `body` and `commits` cross here on purpose: both are the repository's own
# text, and the consumer is a parser rather than a model.
set -euo pipefail
pr="${1:?usage: pr-closure-input.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
gh pr view "$pr" --json number,url,body,commits,closingIssuesReferences,headRefOid
