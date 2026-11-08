#!/usr/bin/env bash
# Is `main` red? Prints each open `main is red` issue and exits 3, prints nothing and exits 0, or exits 2 when it
# cannot say. `/ship` step 6 reads it because a pull request's own checks never rerun when `main` moves
# (docs/commands/ship.md, *Step 6: the gates*). It takes no argument and fixes its fields, so the grant reaches no
# issue body.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"

[ "$#" -eq 0 ] ||
  { echo "usage: main-red-open.sh   (no arguments)" >&2; exit 2; }

TITLE="main is red"
LIMIT=100
rows=$(gh_read issue list --state open --label bug --search "in:title \"$TITLE\"" \
         --limit "$LIMIT" --json number,title,url) ||
  { echo "cannot list the open issues, so whether main is red is unknown" >&2; exit 2; }
count=$(jq length <<<"$rows" | tr -d '\r') ||
  { echo "cannot read the listing, so whether main is red is unknown" >&2; exit 2; }
[ "$count" -lt "$LIMIT" ] ||
  { echo "gh issue list returned $count issues, the limit, so the listing may be cut" >&2; exit 2; }

# The search is full text, so a title that only contains the phrase comes back too. This rule decides, and every
# target's red-main workflow writes the bare title or `main is red: <workflow>`.
open=$(jq -r --arg title "$TITLE" \
  '.[] | select(.title == $title or (.title | startswith($title + ": "))) | "main is red: #\(.number) \(.url)"' \
  <<<"$rows" | tr -d '\r') ||
  { echo "cannot read the listing, so whether main is red is unknown" >&2; exit 2; }
[ -z "$open" ] || { printf '%s\n' "$open"; exit 3; }
