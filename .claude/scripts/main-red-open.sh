#!/usr/bin/env bash
# Is `main` red? Prints each open `main is red` issue and exits 3, prints nothing and exits 0, or exits 2 when it
# cannot say. `/ship` step 6 reads it because a pull request's own checks never rerun when `main` moves
# (docs/commands/ship.md, *Step 6: the gates*), and `/dispatch` before spending an executor on that stop
# (docs/commands/dispatch.md, *A red main*). No argument and fixed fields, so the grant reaches no issue body.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"

[ "$#" -eq 0 ] ||
  { echo "usage: main-red-open.sh   (no arguments)" >&2; exit 2; }

TITLE="main is red"
# Every open bug, with no `--search`: a search reads the search index, which lags a filing, so a `main is red` issue
# filed moments ago would not be listed and a red main would read green. The title is matched below instead.
LIMIT=1000
rows=$(gh_read issue list --state open --label bug \
         --limit "$LIMIT" --json number,title,url) ||
  { echo "cannot list the open issues, so whether main is red is unknown" >&2; exit 2; }
count=$(printf '%s\n' "$rows" | jq length | tr -d '\r') ||
  { echo "cannot read the listing, so whether main is red is unknown" >&2; exit 2; }
[ "$count" -lt "$LIMIT" ] ||
  { echo "gh issue list returned $count issues, the limit, so the listing may be cut" >&2; exit 2; }

# Every open bug comes back, so this rule decides, and every target's red-main workflow writes the bare title or
# `main is red: <workflow>`.
cands=$(printf '%s\n' "$rows" | jq -r --arg title "$TITLE" \
  '.[] | select(.title == $title or (.title | startswith($title + ": "))) | "\(.number) \(.url)"' |
  tr -d '\r') ||
  { echo "cannot read the listing, so whether main is red is unknown" >&2; exit 2; }

# A listing can still hold an issue closed since it was read, which would take the CI-not-green stop. Each
# candidate's own state is read from the issue, and only one reading OPEN counts.
open=""
while read -r number url; do
  [ -n "$number" ] || continue
  state=$(gh_read issue view "$number" --json state --jq .state | tr -d '\r') ||
    { echo "cannot read the state of #$number, so whether main is red is unknown" >&2; exit 2; }
  [ "$state" = "OPEN" ] || [ "$state" = "CLOSED" ] ||
    { echo "state of #$number reads '$state', so whether main is red is unknown" >&2; exit 2; }
  [ "$state" != "OPEN" ] || open+="main is red: #$number $url"$'\n'
done < <(printf '%s\n' "$cands")
[ -z "$open" ] || { printf '%s' "$open"; exit 3; }
