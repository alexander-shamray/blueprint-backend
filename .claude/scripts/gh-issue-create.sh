#!/usr/bin/env bash
# File one issue here with no free parameter for a sweep reading an injectable
# tree: closed-set kind, severity, area and route, title and body as bytes on
# stdin with a fixed last line, and the repository resolved from the checkout.
# Why each: the filing step of the sweep runbooks under docs/commands/.
set -euo pipefail

[ "$#" -eq 4 ] || {
  echo "usage: gh-issue-create.sh <security|bug|test-gap|enhancement> <critical|high|medium|low>" \
       "<harness|code|documentation|harness,test|code,test> <sweep|mutation|hand|request>" \
       "< title, blank line, body ending in the trailer" >&2
  exit 2
}
kind="$1"; severity="$2"; area="$3"; route="$4"

# A word outside the vocabulary is refused rather than passed on.
# `documentation` is absent because no sweep files one and a wrong statement
# filed by hand is a `bug`; the contract's §6 (docs/change-locality.md) states
# that the issue vocabulary is wider than the label helper.
case "$kind" in
  security|bug|test-gap|enhancement) ;;
  *) echo "not a kind this helper will file under: $kind" >&2; exit 2 ;;
esac
case "$severity" in
  critical|high|medium|low) ;;
  *) echo "not a severity this helper will file under: $severity" >&2; exit 2 ;;
esac
# The area is the tree the finding is in, as the contract's §6 maps it; `test`
# goes beside `harness` or `code` alone, because a suite is one of the two.
case "$area" in
  harness|code|documentation|harness,test|code,test) ;;
  *) echo "not an area this helper will file under: $area" >&2; exit 2 ;;
esac
# The route decides the fixed last line, and it is the same closed-set test as
# the other two: a spelling outside the set is refused rather than defaulted,
# because a default is an unconditional claim. Each sentence names the evidence
# behind the filing: a second auditor's reading, each mutant run against its
# suite, no second reading for a finding filed by hand, or none owed by a request.
case "$route" in
  sweep) trailer='Filed by an authorised sweep and verified at filing by a second read-only auditor.' ;;
  mutation) trailer='Filed by an authorised mutation sweep: each mutant listed ran against its suite and was not detected.' ;;
  hand) trailer='Filed by hand rather than by a sweep: no second auditor verified it at filing.' ;;
  request) trailer='Filed by hand as a requested change, not a finding: nothing was verified at filing.' ;;
  *) echo "not a route this helper will file under: $route" >&2; exit 2 ;;
esac
# The request route's sentence is the one that says the issue is not a finding,
# so an enhancement files under it alone and nothing else does: under any other
# route a request would read as a finding, and under it a finding as a request.
case "$kind:$route" in
  enhancement:request) ;;
  enhancement:*|*:request)
    echo "an enhancement files under the request route, and nothing else does: $kind $route" >&2; exit 2 ;;
esac

# The title is the first line of stdin and the second must be blank, so a body
# that arrives without its title line is refused rather than filed under its
# first sentence; so is a heading in its place, which passes the separator
# check. An empty title is refused, and so is a stdin that ends before the
# second line: `read` fails at EOF, and an unset separator is not a blank one.
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

# The body is what is left of stdin, and it is read whole here rather than
# streamed, because its last line is checked before anything is filed.
body=$(cat; printf x); body="${body%x}"
last=$(printf '%s' "$body" | sed -e 's/\r$//' -e '/^[[:space:]]*$/d' | tail -n 1)
[ "$last" = "$trailer" ] ||
  { echo "the body does not end with the trailer line; a heredoc closed early or the line was left off" >&2; exit 2; }

# The repository is resolved, never accepted. `gh repo view` reads the checkout
# this process is standing in, so the answer is a property of the filesystem
# rather than of anything a finding could have said.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }

# Each label but `enhancement` is ensured through the sibling helper, so a
# fresh clone gets the colour and description this repository already uses
# rather than a bare name. `enhancement` is no sweep's label, so the sibling
# refuses it, and GitHub creates it with every repository, so it is filed
# under as the repository already has it.
here=$(dirname "$0")
[ "$kind" = enhancement ] || bash "$here/gh-label-ensure.sh" "$kind" >/dev/null
bash "$here/gh-label-ensure.sh" "$severity" >/dev/null
area_labels=()
IFS=, read -r -a areas <<<"$area"
for label in "${areas[@]}"; do
  bash "$here/gh-label-ensure.sh" "$label" >/dev/null
  area_labels+=(--label "$label")
done

# `--body-file -` reads bytes, so the body goes back out on stdin unchanged.
# The title is the one argument the conversion below could ever have touched,
# and it is excluded for this child.
printf '%s' "$body" | MSYS2_ARG_CONV_EXCL='*' gh issue create \
  --repo "$repo" \
  --title "$title" \
  --label "$kind" \
  --label "$severity" \
  "${area_labels[@]}" \
  --body-file -
