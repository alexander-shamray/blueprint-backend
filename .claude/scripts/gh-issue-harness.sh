#!/usr/bin/env bash
# File one issue in the harness repository from a target, for /ship's sync row,
# and nowhere else: closed-set kind, severity and area, title and body as bytes
# on stdin with the hand route's last line. An open issue of that exact title
# takes the body as a comment, so a re-taken sync adds to its issue.
set -euo pipefail

[ "$#" -eq 3 ] || {
  echo "usage: gh-issue-harness.sh <bug|test-gap|enhancement> <critical|high|medium|low>" \
       "<harness|harness,test|documentation|code|code,test> < title, blank line, body ending in the trailer" >&2
  exit 2
}
kind="$1"; severity="$2"; area="$3"

case "$kind" in
  bug|test-gap|enhancement) ;;
  *) echo "not a kind this helper will file under: $kind" >&2; exit 2 ;;
esac
case "$severity" in
  critical|high|medium|low) ;;
  *) echo "not a severity this helper will file under: $severity" >&2; exit 2 ;;
esac
case "$area" in
  harness|harness,test|documentation|code|code,test) ;;
  *) echo "not an area this helper will file under: $area" >&2; exit 2 ;;
esac
# The sentence of gh-issue-create.sh's hand route, or its request route for an
# enhancement, which files under that sentence alone and nothing else does.
trailer='Filed by hand rather than by a sweep: no second auditor verified it at filing.'
[ "$kind" != enhancement ] ||
  trailer='Filed by hand as a requested change, not a finding: nothing was verified at filing.'

# The title is the first line of stdin and the second must be blank, so a body
# that arrives without its title line is refused rather than filed under its
# first sentence; a heading in its title's place is refused too.
IFS= read -r title || true
title="${title%$'\r'}"
[ -n "$title" ] || { echo "the title is empty" >&2; exit 2; }
[ "${title:0:1}" != "#" ] ||
  { echo "the title begins with a hash, so it is a heading: title, blank line, body" >&2; exit 2; }
IFS= read -r separator ||
  { echo "stdin ended before the blank line: title, blank line, body" >&2; exit 2; }
separator="${separator%$'\r'}"
[ -z "$separator" ] ||
  { echo "the second line of stdin must be blank: title, blank line, body" >&2; exit 2; }

# Read whole, because the last line is checked before anything is filed.
body=$(cat; printf x); body="${body%x}"
last=$(printf '%s' "$body" | sed -e 's/\r$//' -e '/^[[:space:]]*$/d' | tail -n 1)
[ "$last" = "$trailer" ] ||
  { echo "the body does not end with the trailer line; a heredoc closed early or the line was left off" >&2; exit 2; }

# The repository is a literal, and every gh call is under a timeout. Labels are
# not ensured: the sibling resolves the checkout's repository, and the harness
# holds the vocabulary, so a label it lacks fails the create.
repo=alexander-shamray/blueprint-harness
# The open list is read whole and compared here, with no --search: GitHub reads a
# commit scope such as `chore(harness):` in a query as a qualifier and returns
# nothing, so a search on the title files a duplicate. A failed read files
# nothing, since filing would repeat what a match takes; so does a list that
# fills the limit, which may hide the match.
limit=1000
open=$(timeout 60 gh issue list --repo "$repo" --state open --limit "$limit" \
  --json number,title --jq '.[] | "\(.number)\t\(.title)"') ||
  { echo "cannot search the harness's open issues" >&2; exit 3; }
if [ -n "$open" ] && [ "$(printf '%s\n' "$open" | wc -l)" -ge "$limit" ]; then
  echo "cannot search the harness's open issues: $limit or more are open, so the list may hide a match" >&2
  exit 3
fi
while IFS=$'\t' read -r number existing; do
  existing="${existing%$'\r'}"
  if [ "$existing" = "$title" ] && [[ "$number" =~ ^[0-9]+$ ]]; then
    printf '%s' "$body" | timeout 60 gh issue comment "$number" --repo "$repo" --body-file -
    exit 0
  fi
done <<<"$open"
area_labels=()
IFS=, read -r -a areas <<<"$area"
for label in "${areas[@]}"; do area_labels+=(--label "$label"); done
printf '%s' "$body" | MSYS2_ARG_CONV_EXCL='*' timeout 60 gh issue create \
  --repo "$repo" \
  --title "$title" \
  --label "$kind" \
  --label "$severity" \
  "${area_labels[@]}" \
  --body-file -
