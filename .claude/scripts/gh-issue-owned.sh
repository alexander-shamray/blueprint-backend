#!/usr/bin/env bash
# The numbers of the issues the repository owner opened, as one JSON list: what
# /dispatch may launch (`docs/harness-boundaries.md`, *Headless dispatched sessions*).
# `gh-issue-suppresses.sh`'s test over every issue at once, so no reader holds a
# login; a failed read exits non-zero and never reads as an empty list.
set -euo pipefail

[ "$#" -eq 0 ] ||
  { echo "usage: gh-issue-owned.sh   (no arguments)" >&2; exit 2; }

owner=$(gh repo view --json owner --jq .owner.login) ||
  { echo "could not resolve the repository owner from this checkout" >&2; exit 3; }
[ -n "$owner" ] ||
  { echo "the repository owner resolved to an empty login" >&2; exit 3; }

# `--state all`, so a closed issue's row reads as the owner's rather than a
# stranger's; a cut list is refused as `gh-issue-list.sh` refuses one.
limit=5000
list=$(gh issue list --state all --limit "$limit" --json number,author)
count=$(jq length < <(printf '%s\n' "$list") | tr -d '\r')
[ "$count" -lt "$limit" ] ||
  { echo "gh-issue-owned: $count issues is the limit, so the list may be cut; raise it here" >&2; exit 3; }
jq -c --arg owner "$owner" '[.[] | select(.author.login == $owner) | .number]' < <(printf '%s\n' "$list") | tr -d '\r'
