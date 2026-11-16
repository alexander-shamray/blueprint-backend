#!/usr/bin/env bash
# Open this checkout's branch as a pull request into main: /pr's one create. A raw `gh pr create` grant is a prefix
# match, so a trailing `--repo`, `--head`, `--base` or `--body-file` would choose what is opened; this helper fixes all
# four, and takes the title and body as files git ignores under `artifacts/`, never as text on the command line, since
# `--body-file` publishes what it reads (`docs/harness-boundaries.md`, *Grant inventory and push helpers*).
set -euo pipefail
[ "$#" -eq 2 ] ||
  { echo "usage: gh-pr-create.sh <title-file> <body-file>, both under artifacts/" >&2; exit 2; }

checkout=$(git rev-parse --show-toplevel) ||
  { echo "not inside a git checkout" >&2; exit 3; }
checkout=$(cd "$checkout" && pwd -P)
inputs="$checkout/artifacts"

# Prints the input's path relative to the checkout, or refuses it.
input() {
  local arg="$1" dir rel
  case "$arg" in
    ''|*$'\n'*|*$'\r'*) echo "not a path this helper reads: $arg" >&2; exit 2 ;;
  esac
  [ ! -L "$arg" ] || { echo "a symbolic link is not a PR input: $arg" >&2; exit 2; }
  [ -f "$arg" ] || { echo "the caller-written file is missing: $arg" >&2; exit 2; }
  # `pwd -P` resolves every directory above the file, so a linked parent or a
  # `..` that leaves `artifacts/` lands outside the prefix and is refused.
  dir=$(cd -- "$(dirname -- "$arg")" && pwd -P) ||
    { echo "cannot resolve the directory holding $arg" >&2; exit 2; }
  case "$dir/" in
    "$inputs"/*) ;;
    *) echo "a PR input lives under $inputs/: $arg" >&2; exit 2 ;;
  esac
  rel="${dir#"$checkout"/}/$(basename -- "$arg")"
  # Without `--no-index`, check-ignore answers no for a tracked path, so this
  # one read refuses both a tracked input and an unignored one.
  git -C "$checkout" check-ignore -q -- "$rel" ||
    { echo "$rel is not ignored by git, so the branch could have written it" >&2; exit 2; }
  printf '%s\n' "$rel"
}
title_file=$(input "$1")
body=$(input "$2")
[ "$title_file" != "$body" ] ||
  { echo "the title and the body are one file: $title_file" >&2; exit 2; }

# One line. A newline in a title is not a title, and `gh` would take the rest
# as body text with nothing in the report to show what was sent. The one line
# ending a file closes with, LF or CRLF, is not part of it, and any other
# carriage return or newline is refused. The bytes are counted in the file,
# since Git Bash's command substitution drops a carriage return itself.
tf="$checkout/$title_file"
nl=$(tr -cd '\n' < "$tf" | wc -c)
cr=$(tr -cd '\r' < "$tf" | wc -c)
tail2=$(tail -c 2 -- "$tf" | od -An -tx1 | tr -d ' \n')
case "$((nl)):$((cr)):$tail2" in
  0:0:*|1:0:*0a|1:1:0d0a) ;;
  *) echo "the title spans more than one line" >&2; exit 2 ;;
esac
title=$(tr -d '\r\n' < "$tf")
[ -n "$title" ] || { echo "the title is empty" >&2; exit 2; }

branch=$(git branch --show-current)
[ -n "$branch" ] || { echo "detached HEAD: there is no branch to open a PR for" >&2; exit 3; }
# Folded and spelled out as git-trailer-pr.sh does: on a case-insensitive filesystem `Main` is `main`'s ref.
case "$(printf %s "$branch" | tr '[:upper:]' '[:lower:]')" in
  main|heads/main|refs/heads/main|origin/main|refs/remotes/origin/main)
    echo "refusing to open a pull request from main into main, spelled $branch" >&2; exit 3 ;;
esac

repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 3; }
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 3; }

# The title is passed as a value, never spliced, so a leading `-` is a title
# and not a flag. The body goes as a path relative to the checkout, which `gh`
# runs in: with MSYS path conversion off, which a title beginning with `/`
# needs, an absolute Git Bash path would not reach a native `gh`.
cd "$checkout"
MSYS2_ARG_CONV_EXCL='*' gh pr create --repo "$repo" --base main --head "$branch" \
  --title "$title" --body-file "$body"

# Removed once published, and only then: a failed create keeps both for the
# retry, and a later run cannot publish this one's body by mistake.
rm -f -- "$body" "$title_file"
