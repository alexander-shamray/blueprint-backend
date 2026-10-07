#!/usr/bin/env bash
# Judge a pull request's changed paths against the `| Class |` and `| Touch set |` rows its body declares: one `class`
# line, then `inside <path>` or `outside <path>` per changed file, and never the author's cell. Read-only, fixed field
# set; the grammar, the exit codes and why the verdict grants nothing are `docs/harness-boundaries.md`'s (*Pull
# request reads and the locality verdict*), and `test_pr_helpers.py` pins them.
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
grep -Eq '^([A-E](\+[A-E])?|A\+D\+E)$' <<<"$class" || refuse "the Class row is not a class"
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
