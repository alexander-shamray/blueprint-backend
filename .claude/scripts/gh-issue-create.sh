#!/usr/bin/env bash
# File one issue on this repository, from a sweep or by hand: kind, severity
# and route from closed vocabularies on the command line, title and body on
# stdin, and the repository from the checkout this script stands in.
#
# A prefix grant on `gh issue create` leaves `--repo` and `--label` free to a
# caller reading an audited tree, which is prompt-injection input. Here the
# repository is resolved, the labels are two words out of a closed set, and
# title and body are bytes on stdin, because an inline `--body` mangles the
# wrapping and a temp file would need the `Write` grant the sweeps withhold.
#
# The title is on stdin because a sweep composes it from a verdict record a
# crafted tree can steer: as an argument, `"$(…)"` expands in the parent's
# shell before any check here runs, while a quoted heredoc expands nothing.
# The first line of stdin is the title, the second is blank, and the rest is
# the body.
#
# The body's last line is a detector, not a guard. A repository line equal to
# the heredoc's delimiter closes it early and hands the rest of the payload to
# the parent's shell, which nothing here can prevent — the sweeps check the
# delimiter against the payload for that. What this script refuses is the
# truncated body such a close leaves, because it has lost its trailer.
#
# MSYS argument conversion rewrites an argument that looks like an absolute
# POSIX path before a native `gh.exe` sees it, so a title beginning with `/`
# files as a Windows path. An env-prefixed command no longer matches a
# `gh issue create` prefix grant; a script sets MSYS2_ARG_CONV_EXCL for its
# own child.
#
# The trailer is chosen by route rather than constant, because a provenance
# sentence every issue carries distinguishes nothing, and a sweep's sentence
# on a hand filing is false. The sweeps deny raw `gh issue create`, so this is
# their only route; nothing denies it to a session, so `hand` is what a filer
# gets by choosing this helper, not a rule that hand filings come here.
set -euo pipefail

[ "$#" -eq 3 ] || {
  echo "usage: gh-issue-create.sh <security|bug> <critical|high|medium|low>" \
       "<sweep|hand> < title, blank line, body ending in the trailer" >&2
  exit 2
}
kind="$1"; severity="$2"; route="$3"

# A word outside the vocabulary is refused rather than passed on.
# `documentation` is absent because neither sweep files one, and it is a
# GitHub default label `gh-label-ensure.sh` must not create; CLAUDE.md states
# that the issue vocabulary is wider than the label helper.
case "$kind" in
  security|bug) ;;
  *) echo "not a kind this helper will file under: $kind" >&2; exit 2 ;;
esac
case "$severity" in
  critical|high|medium|low) ;;
  *) echo "not a severity this helper will file under: $severity" >&2; exit 2 ;;
esac
# The route decides the fixed last line, and it is the same closed-set test as
# the other two: a spelling outside the set is refused rather than defaulted,
# because a default is an unconditional claim. There is no spelling for a
# sweep whose auditor did not run, because such a sweep does not file.
case "$route" in
  sweep) trailer='Filed by an authorised sweep and verified at filing by a second read-only auditor.' ;;
  hand) trailer='Filed by hand rather than by a sweep: no second auditor verified it at filing.' ;;
  *) echo "not a route this helper will file under: $route" >&2; exit 2 ;;
esac

# The title is the first line of stdin and the second line must be blank, so a
# body that arrives without its title line is refused rather than filed under
# its own first sentence. An empty title files an issue nobody can find in the
# tracker; a title cannot contain a newline, because a line is what it is. A
# stdin that ends before the second line is refused too: `read` fails at EOF,
# and an unset separator is not a blank one.
IFS= read -r title || true
title="${title%$'\r'}"
[ -n "$title" ] || { echo "the title is empty" >&2; exit 2; }
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

# Both labels are ensured through the sibling helper, so a fresh clone gets the
# colour and description this repository already uses rather than a bare name.
here=$(dirname "$0")
bash "$here/gh-label-ensure.sh" "$kind" >/dev/null
bash "$here/gh-label-ensure.sh" "$severity" >/dev/null

# `--body-file -` reads bytes, so the body goes back out on stdin unchanged.
# The title is the one argument the conversion below could ever have touched,
# and it is excluded for this child.
printf '%s' "$body" | MSYS2_ARG_CONV_EXCL='*' gh issue create \
  --repo "$repo" \
  --title "$title" \
  --label "$kind" \
  --label "$severity" \
  --body-file -
