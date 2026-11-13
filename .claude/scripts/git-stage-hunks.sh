#!/usr/bin/env bash
# `git add -p` without its prompts, the index the only thing written: `--list <path>` numbers one tracked
# file's unstaged hunks from 1, and `<path> <n>[,<n>...]` cuts those from `git diff` and stages them with
# `git apply --cached`. Exits: 0 done; 2 usage; 3 a path refused (not one tracked file, or binary, unmerged,
# added, deleted or retyped); 4 a hunk number past the last; 5 `git apply` refused, and nothing was staged.
set -euo pipefail
export GIT_LITERAL_PATHSPECS=1

usage() {
  echo "usage: git-stage-hunks.sh --list <path> | git-stage-hunks.sh <path> <n>[,<n>...]" >&2
  exit 2
}

[ "$#" -eq 2 ] || usage
if [ "$1" = "--list" ]; then
  mode=list path=$2 numbers=""
else
  mode=stage path=$1 numbers=$2
  # Nine digits at most, so `-gt` below never meets a number past its integers and reads the error as false.
  [[ "$numbers" =~ ^[1-9][0-9]{0,8}(,[1-9][0-9]{0,8})*$ ]] \
    || { echo "hunk numbers are <n>[,<n>...], counted from 1, nine digits at most: $numbers" >&2; exit 2; }
fi
case "$path" in
  "" | -*) echo "path may not be empty or start with '-': $path" >&2; exit 2 ;;
  *) : ;;
esac

# Unmerged first: `ls-files` lists an unmerged path once per stage, which would read as several files.
if [ -n "$(git ls-files -u -- "$path")" ]; then
  echo "refused: $path is unmerged; resolve it before staging hunks" >&2
  exit 3
fi
if [ -d "$path" ]; then
  echo "refused: $path is a directory, not one tracked file of this checkout" >&2
  exit 3
fi
names=()
while IFS= read -r -d '' name; do
  names+=("$name")
done < <(git ls-files -z --full-name -- "$path")
if [ "${#names[@]}" -ne 1 ]; then
  echo "refused: $path is not one tracked file of this checkout (untracked, outside it, or a directory)" >&2
  exit 3
fi
file=${names[0]}
cd "$(git rev-parse --show-toplevel)"

if [ "$(git diff --numstat --no-ext-diff --no-textconv --no-relative -- "$file" | cut -f1)" = "-" ]; then
  echo "refused: $file is binary, and a binary diff has no hunks" >&2
  exit 3
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# Each option a config could set to make the diff unreadable to `git apply` is spelled out: its
# `diff.noprefix`, colour, textconv or `diff.suppressBlankEmpty`.
git -c diff.suppressBlankEmpty=false diff --no-color --no-ext-diff --no-textconv --no-relative \
  --src-prefix=a/ --dst-prefix=b/ -U3 -- "$file" >"$work/diff"

# What precedes the first hunk is the file's header: one `diff --git`, and a new, deleted or retyped
# file is not a modification whose hunks can be taken apart.
header=$(sed -n '/^@@ /q;p' "$work/diff")
if [ "$(printf '%s\n' "$header" | grep -c '^diff --git ')" -gt 1 ] \
  || printf '%s\n' "$header" | grep -Eq '^(new file mode|deleted file mode|diff --cc )'; then
  echo "refused: $file is added, deleted or changed in type; stage it whole with git add" >&2
  exit 3
fi
total=$(grep -c '^@@ ' "$work/diff" || true)

if [ "$mode" = "list" ]; then
  if [ "$total" -eq 0 ]; then
    echo "no unstaged hunks in $file"
    exit 0
  fi
  # Each hunk: its number and `@@` header, its first three changed lines, and how many it adds and drops.
  awk '
    function close_hunk() {
      if (n > 0) {
        if (shown < changed) printf "    ... %d more changed line(s)\n", changed - shown
        printf "    (+%d -%d)\n", added, dropped
      }
    }
    { sub(/\r$/, "") }
    /^@@ / { close_hunk(); n++; changed = shown = added = dropped = 0; printf "%d: %s\n", n, $0; next }
    n == 0 { next }
    /^[+-]/ {
      changed++
      if (/^\+/) added++; else dropped++
      if (shown < 3) { shown++; printf "    %s\n", $0 }
    }
    END { close_hunk() }
  ' "$work/diff"
  exit 0
fi

wanted=","
IFS=, read -r -a picked <<EOF
$numbers
EOF
for number in "${picked[@]}"; do
  if [ "$number" -gt "$total" ]; then
    echo "refused: hunk $number is past the last of $file's $total unstaged hunk(s); see --list" >&2
    exit 4
  fi
  wanted="$wanted$number,"
done

# The header's `diff --git`, `---` and `+++` lines, then each wanted hunk whole. A mode change's lines
# are left out, so only content is staged. Before the first `@@` a line is header; after it, a line
# opening `@@` is always a hunk's, since a body line opens with a space, `+`, `-` or `\`. Git for
# Windows' gawk drops a CRLF line's `\r` unless BINMODE says read and write bytes; other awks ignore it.
awk -v BINMODE=3 -v wanted="$wanted" '
  /^@@ / { n++; keep = index(wanted, "," n ",") > 0 }
  n == 0 { if (/^(diff --git |--- |\+\+\+ )/) print; next }
  keep { print }
' "$work/diff" >"$work/patch"

# A config's `apply.whitespace=error` would refuse a hunk with trailing blanks.
if ! git -c apply.whitespace=nowarn -c apply.ignoreWhitespace=false \
  apply --cached --recount <"$work/patch"; then
  echo "refused: git apply could not stage hunk(s) $numbers of $file; nothing was staged" >&2
  exit 5
fi
echo "staged hunk(s) $numbers of $total from $file"
git diff --cached --stat -- "$file"
