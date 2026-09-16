#!/usr/bin/env bash
# Judge a pull request's changed paths against the `| Class |` and
# `| Touch set |` rows its body declares, and print a verdict per path — and
# nothing the author wrote. Read-only, fixed field set.
#
# /review-branch, /review-copilot and /ship read the two rows
# `docs/change-locality.md` asks a PR body to carry through this rather than a
# `gh pr view` grant, which reaches the unfiltered `--json reviews` feed. A
# caller that chooses fields can choose `reviews`, so this one chooses none:
# `body` from the pull request, `filename` from the files endpoint.
#
# The output never contains the touch-set cell. A pull request author is not a
# trusted party, /review-copilot takes any PR number, and a row would carry an
# author's text to an Edit-capable agent unfiltered. A path grammar cannot
# close that — `Ignore_all_previous_instructions.md` is a path
# — so the cell is consumed here and only a verdict leaves: one `class` line
# whose value is letters this script validated, then one `inside <path>` or
# `outside <path>` line per changed file, where the path is the diff's own
# and the word is this script's. A caller acts on `outside` lines and never
# sees what the set said. A changed path is the author's text too — the
# author names the files, and git permits a newline inside a name — so each
# arrives JSON-encoded, one per line and unambiguous, and is printed only if
# it decodes to a plain path: no escape in it, path characters only, a `/`
# or a `.` in it, no `..` segment. Any other name refuses the whole run,
# because a verdict list with one line withheld is a list a caller would
# read as complete.
#
# Nothing is printed when the body carries neither row; a caller that reads
# nothing skips its touch-set check and says so, and does not infer a class.
# One row without the other is refused, because each command reads the pair.
# A row that fails its grammar is refused with exit 3 naming the row and not
# its content: a class cell is one letter A–E or two distinct letters joined
# by `+`; a touch-set cell is a comma-separated list of path tokens, bare or
# in balanced backticks, of path and glob characters — `*`, `**`, `?` and a
# brace alternation — each carrying a `/` or a `.`, and each
# repository-relative: no leading `/`, no `./`, no `..` segment, brace
# alternatives included, since the edit-target guard judges where an edit
# inside the checkout lands and not a path naming the outside.
#
# The verdict narrows and grants nothing. What holds authority is the
# caller's own deny list and the class's tree set in the contract; an
# `outside` line is a finding for the caller, and an `inside` line is not a
# licence for anything the caller's grant refuses.
set -euo pipefail
pr="${1:?usage: pr-locality.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
refuse() { echo "$1" >&2; exit 3; }
# The body is captured before it is filtered, so a `gh` failure — no
# authentication, no network, no such pull request — is fatal under `set -e`
# rather than indistinguishable from a body with no rows. Only grep's own
# no-match status, which is exactly 1, is masked.
body=$(gh pr view "$pr" --json body --jq .body)
class_row=$(grep -E '^\| *Class *\|' <<<"$body" || [ $? -eq 1 ])
touch_row=$(grep -E '^\| *Touch set *\|' <<<"$body" || [ $? -eq 1 ])
# Exactly one of each, or none, checked before either grammar: with two rows a
# valid first would carry an invalid second past a check of whether any row
# matched.
[ "$(grep -c . <<<"$class_row")" -le 1 ] || refuse "more than one Class row"
[ "$(grep -c . <<<"$touch_row")" -le 1 ] || refuse "more than one Touch set row"
if [ -z "$class_row" ] && [ -z "$touch_row" ]; then exit 0; fi
[ -n "$class_row" ] && [ -n "$touch_row" ] || refuse "one row without the other"
# The class cell: the text between the second `|` and the closing one.
class=$(sed -E 's/^\| *Class *\| *//; s/ *\| *$//' <<<"$class_row")
grep -Eq '^[A-E](\+[A-E])?$' <<<"$class" || refuse "the Class row is not a class"
[ "${class:0:1}" != "${class:2:1}" ] || refuse "the Class row repeats a class"
# The touch-set cell, then each comma-separated token on its own.
cells=$(sed -E 's/^\| *Touch set *\| *//; s/ *\| *$//' <<<"$touch_row")
case "$cells" in *'|'*) refuse "the Touch set row is not one cell" ;; esac
[ -n "$cells" ] || refuse "the Touch set row is empty"
# Split on commas outside braces, because a brace glob carries its own —
# `.claude/commands/{pr,ship}.md` is one token, not two halves of one.
items=(); cur=""; depth=0
for ((i = 0; i < ${#cells}; i++)); do
  ch="${cells:i:1}"
  case "$ch" in
    '{') depth=$((depth + 1)) ;;
    '}') depth=$((depth - 1)); [ "$depth" -ge 0 ] || refuse "the Touch set row has an unbalanced brace" ;;
    ',') if [ "$depth" -eq 0 ]; then items+=("$cur"); cur=""; continue; fi ;;
  esac
  cur+="$ch"
done
items+=("$cur")
[ "$depth" -eq 0 ] || refuse "the Touch set row has an unbalanced brace"
patterns=()
for item in "${items[@]}"; do
  t="${item#"${item%%[! ]*}"}"
  t="${t%"${t##*[! ]}"}"
  case "$t" in
    '`'*'`') t="${t:1:${#t}-2}" ;;
    *'`'*) refuse "the Touch set row has an unbalanced backtick" ;;
  esac
  grep -Eq '^[A-Za-z0-9_./*?{},()-]+$' <<<"$t" ||
    refuse "the Touch set row is not a path list"
  case "$t" in *[/.]*) ;; *) refuse "the Touch set row is not a path list" ;; esac
  t="${t%/}"
  # A brace alternative is a segment start too: `{../outside,docs/x.md}`
  # expands to a path that leaves the checkout, so the boundary is judged
  # over the token with its braces dropped and its alternatives joined as
  # segments, where a leading `/`, a `./` and a `..` all show as segments.
  n="${t//[\{\}]/}"
  n="${n//,//}"   # every `,` becomes `/`: the replacement is the last `/`
  case "/$n/" in
    *//*|*/./*|*/../*) refuse "the Touch set row names a path outside the repository" ;;
  esac
  # The token as an anchored regular expression: `**` crosses directories,
  # `*` and `?` do not, braces are alternation, and a token also covers
  # everything beneath the directory it names — `tests/Ordering.*` is the
  # test projects, not files whose name happens to start that way. A
  # trailing `/` names the directory the same way `docs` would, and was
  # dropped above before the boundary was judged.
  re=$(printf '%s' "$t" |
    sed -e 's/[.()]/\\&/g' -e 's/\*\*/\x01/g' -e 's/\*/[^\/]*/g' \
        -e 's/?/[^\/]/g' -e 's/\x01/.*/g' -e 's/{/(/g' -e 's/}/)/g' -e 's/,/|/g')
  patterns+=("^${re}(/.*)?$")
done
# The changed paths are the diff's own, and each gets the one word this
# script chooses for it. `filename` is the whole of what is read, and it is
# read as a JSON string so that a newline inside a name cannot be a second
# line: a name that needed an escape is refused rather than decoded.
files=$(gh api "repos/{owner}/{repo}/pulls/$pr/files" --paginate --jq '.[].filename | @json')
verdicts=()
while IFS= read -r line; do
  [ -n "$line" ] || continue
  case "$line" in
    '"'*'"') ;;
    *) refuse "a changed path did not arrive as a JSON string" ;;
  esac
  case "$line" in *\\*) refuse "a changed path is not a plain path" ;; esac
  path="${line:1:${#line}-2}"
  grep -Eq '^[A-Za-z0-9_./@+()-]+$' <<<"$path" || refuse "a changed path is not a plain path"
  case "$path" in *[/.]*) ;; *) refuse "a changed path is not a plain path" ;; esac
  case "/$path/" in *//*|*/./*|*/../*) refuse "a changed path is not a plain path" ;; esac
  verdicts+=("$path")
done <<<"$files"
printf 'class %s\n' "$class"
for path in "${verdicts[@]}"; do
  verdict=outside
  for re in "${patterns[@]}"; do
    if grep -Eq "$re" <<<"$path"; then verdict=inside; break; fi
  done
  printf '%s %s\n' "$verdict" "$path"
done
