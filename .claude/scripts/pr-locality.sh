#!/usr/bin/env bash
# Judge a pull request's changed paths against its `| Class |` and `| Touch set |` rows and, as the CI gate does,
# the class's tree set in `.github/locality-gate/classes.yml`: a `class` line, then `outside-class`, `outside` or
# `inside` and the path per changed file, never the author's cell. Read-only, fixed field set; the grammar, exit
# codes and verdict are `docs/harness-boundaries.md`'s (*Pull request reads and the locality verdict*), pinned by tests.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/gh-read-bound.sh"
pr="${1:?usage: pr-locality.sh <pr-number>}"
[[ "$pr" =~ ^[0-9]+$ ]] || { echo "pr must be a number" >&2; exit 2; }
refuse() { echo "$1" >&2; exit 3; }
grep_died() { echo "a pattern's grep did not finish, so nothing is printed" >&2; exit 1; }
# A path token's characters, spelled out so no locale reads a range, judged by bash itself: Git Bash on a loaded
# Windows runner reported a `grep` fed by a process substitution as `Child exited`, refusing a valid token.
is_token() {
  case "$1" in ''|*[!ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_./*?{},\(\)@+-]*) return 1 ;; esac
}
# The token as an anchored regular expression: `**` crosses directories,
# `*` and `?` do not, braces are alternation, and a token also covers
# everything beneath the directory it names — `tests/Ordering.*` is the
# test projects, not files whose name happens to start that way.
to_regex() {
  local re
  # `+` is a literal, and the placeholder is spelled with `%`, which no token
  # may hold: BSD sed reads `\x01` as the literal `x01`.
  re=$(printf '%s' "$1" |
    sed -e 's/[.()+]/\\&/g' -e 's/\*\*/%%GLOBSTAR%%/g' -e 's/\*/[^\/]*/g' \
        -e 's/?/[^\/]/g' -e 's/%%GLOBSTAR%%/.*/g' \
        -e 's/{/(/g' -e 's/}/)/g' -e 's/,/|/g')
  printf '^%s(/.*)?$' "$re"
}
# The body is captured before it is filtered, so a `gh` failure — no
# authentication, no network, no such pull request — is fatal under `set -e`
# rather than indistinguishable from a body with no rows. The rows are found
# and cut by bash itself, as a token is.
body=$(gh_read pr view "$pr" --json body --jq .body)
# A body saved on Windows or through the web form ends each line in CR, which no row grammar below expects.
body=${body//$'\r'/}
class_row="" touch_row="" class_rows=0 touch_rows=0
class_at='^\| *Class *\|' touch_at='^\| *Touch set *\|'
# A row inside a fenced code block is an example, not the declaration, and is skipped as the CI gate skips it, by
# CommonMark's fences: a backtick or tilde run of three or more, indented at most three spaces, a backtick opener's
# info string holding no backtick, closed only by a run of the same character, at least as long, then blanks alone.
# The run is greedy, so a backtick left in the rest is one in the info string. An unclosed fence runs to the end.
fence="" fence_open='^ {0,3}(`{3,}|~{3,})(.*)$' fence_close='^ {0,3}(`{3,}|~{3,})[[:blank:]]*$'
while IFS= read -r line; do
  if [ -n "$fence" ]; then
    if [[ "$line" =~ $fence_close ]] && [ "${BASH_REMATCH[1]:0:1}" = "${fence:0:1}" ] &&
      [ "${#BASH_REMATCH[1]}" -ge "${#fence}" ]; then fence=""; fi
    continue
  fi
  if [[ "$line" =~ $fence_open ]]; then
    case "${BASH_REMATCH[1]:0:1}${BASH_REMATCH[2]}" in
      '`'*'`'*) ;;
      *) fence=${BASH_REMATCH[1]}; continue ;;
    esac
  fi
  if [[ "$line" =~ $class_at ]]; then class_row=$line; class_rows=$((class_rows + 1)); fi
  if [[ "$line" =~ $touch_at ]]; then touch_row=$line; touch_rows=$((touch_rows + 1)); fi
done < <(printf '%s\n' "$body")
# Exactly one of each, or none, checked before either grammar: with two rows a
# valid first would carry an invalid second past a check of whether any row
# matched.
[ "$class_rows" -le 1 ] || refuse "more than one Class row"
[ "$touch_rows" -le 1 ] || refuse "more than one Touch set row"
if [ -z "$class_row" ] && [ -z "$touch_row" ]; then exit 0; fi
[ -n "$class_row" ] && [ -n "$touch_row" ] || refuse "one row without the other"
# Cut `cell`, a row labelled `$1`, to the text after its label's `|`, less a closing `|` and the spaces around both.
cell_of() {
  local head="^\\| *$1 *\\| *" tail=' *\| *$'
  if [[ "$cell" =~ $head ]]; then cell=${cell:${#BASH_REMATCH[0]}}; fi
  if [[ "$cell" =~ $tail ]]; then cell=${cell:0:${#cell}-${#BASH_REMATCH[0]}}; fi
}
cell=$class_row
cell_of Class
class=$cell
# Spelled out, here and in the map's grammar below: a regex library that collates by locale reads a range as taking
# lower case too, which the gate refuses.
class_re='^([ABCDE](\+[ABCDE])?|A\+D\+E)$'
[[ "$class" =~ $class_re ]] || refuse "the Class row is not a class"
[ "${class:0:1}" != "${class:2:1}" ] || refuse "the Class row repeats a class"
# The touch-set cell, then each comma-separated token on its own.
cell=$touch_row
cell_of "Touch set"
cells=$cell
case "$cells" in *'|'*) refuse "the Touch set row is not one cell" ;; esac
[ -n "$cells" ] || refuse "the Touch set row is empty"
# Split on commas outside braces, because a brace glob carries its own —
# `.claude/commands/{pr,ship}.md` is one token, not two halves of one.
# A backticked token may carry its reason beside it in parentheses, the form
# `/pr` prescribes for a widened row: a `(` outside backticks and after a space
# opens one, whose commas and braces are the reason's, and it is dropped below.
items=(); cur=""; depth=0; inbt=0; rdepth=0; prev=""
for ((i = 0; i < ${#cells}; i++)); do
  ch="${cells:i:1}"
  if [ "$rdepth" -gt 0 ]; then
    case "$ch" in
      '(') rdepth=$((rdepth + 1)) ;;
      ')') rdepth=$((rdepth - 1)) ;;
    esac
    cur+="$ch"; prev="$ch"; continue
  fi
  case "$ch" in
    '`') inbt=$((1 - inbt)) ;;
    '(') if [ "$inbt" -eq 0 ] && [[ "$prev" == [[:space:]] ]]; then rdepth=1; fi ;;
    '{') depth=$((depth + 1)) ;;
    '}') depth=$((depth - 1)); [ "$depth" -ge 0 ] || refuse "the Touch set row has an unbalanced brace" ;;
    ',') if [ "$depth" -eq 0 ]; then items+=("$cur"); cur=""; prev=""; continue; fi ;;
  esac
  cur+="$ch"; prev="$ch"
done
[ "$rdepth" -eq 0 ] || refuse "the Touch set row has an unbalanced parenthesis"
items+=("$cur")
[ "$depth" -eq 0 ] || refuse "the Touch set row has an unbalanced brace"
patterns=()
for item in "${items[@]}"; do
  t="${item#"${item%%[! ]*}"}"
  t="${t%"${t##*[! ]}"}"
  # The reason beside a backticked token is dropped unread and never printed;
  # it holds no backtick and no nested parenthesis.
  if [[ "$t" =~ ^(\`[^\`]+\`)[[:space:]]+\([^\`\(\)]*\)$ ]]; then t="${BASH_REMATCH[1]}"; fi
  # A reason that failed the form above is named as such, not as a backtick.
  if [[ "$t" =~ ^\`[^\`]+\`[[:space:]]+\( ]]; then refuse "the Touch set row has a reason outside its grammar"; fi
  case "$t" in
    '`'*'`') t="${t:1:${#t}-2}" ;;
    *'`'*) refuse "the Touch set row has an unbalanced backtick" ;;
  esac
  is_token "$t" || refuse "the Touch set row is not a path list"
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
  # An empty alternative is an empty ERE alternative, which GNU and BSD grep
  # read differently; the boundary check above took the leading one.
  case "$t" in
    *'{}'*|*'{,'*|*',,'*|*',}'*)
      refuse "the Touch set row has an empty brace alternative" ;;
  esac
  # A trailing `/` names the directory the same way `docs` would, and was
  # dropped above before the boundary was judged.
  patterns+=("$(to_regex "$t")")
done
# The class's tree set, from this checkout's copy of the map CI reads out of the
# base commit, so a pull request that edits the map is judged here by its own
# copy; in the one shape it may take, a map absent, short or malformed refuses the
# run, because the gate refuses that map and a verdict read from it is no one's.
map="$(dirname "${BASH_SOURCE[0]}")/../../.github/locality-gate/classes.yml"
[ -r "$map" ] || refuse "the class map .github/locality-gate/classes.yml cannot be read"
# Space-delimited sets of the class letters named and of those given an item:
# bash 3.2, /bin/bash on macOS, has no associative array.
named=" " stocked=" "
class_patterns=(); member=""
while IFS= read -r raw || [ -n "$raw" ]; do
  raw=${raw%$'\r'}
  t="${raw#"${raw%%[! ]*}"}"
  case "$t" in ''|'#'*) continue ;; esac
  if [[ "$raw" =~ ^([ABCDE]):[[:space:]]*$ ]]; then
    member=${BASH_REMATCH[1]}
    case "$named" in *" $member "*) refuse "classes.yml names class $member twice" ;; esac
    named="$named$member "
    continue
  fi
  [[ "$raw" =~ ^\ \ -\ \'([^\']+)\'[[:space:]]*$ ]] && [ -n "$member" ] ||
    refuse "classes.yml has a line outside its grammar"
  item=${BASH_REMATCH[1]}
  # An item takes the touch-set token checks, since an unchecked one widens a
  # class: `docs/a,docs/b` would admit `docs/admin`. A target's own gate may
  # admit fewer characters than this grammar does.
  is_token "$item" && [[ "$item" == *[/.]* ]] ||
    refuse "classes.yml has an item that is not a path token"
  depth=0
  for ((i = 0; i < ${#item}; i++)); do
    case "${item:i:1}" in
      '{') depth=$((depth + 1)) ;;
      '}') depth=$((depth - 1)); [ "$depth" -ge 0 ] || refuse "classes.yml has an item with an unbalanced brace" ;;
      ',') [ "$depth" -gt 0 ] || refuse "classes.yml has an item with a comma outside a brace" ;;
    esac
  done
  [ "$depth" -eq 0 ] || refuse "classes.yml has an item with an unbalanced brace"
  item=${item%/}
  n="${item//[\{\}]/}"
  n="${n//,//}"
  case "/$n/" in *//*|*/./*|*/../*) refuse "classes.yml has an item outside the repository" ;; esac
  case "$item" in
    *'{}'*|*'{,'*|*',,'*|*',}'*) refuse "classes.yml has an item with an empty brace alternative" ;;
  esac
  case "$stocked" in *" $member "*) : ;; *) stocked="$stocked$member " ;; esac
  case "+$class+" in *"+$member+"*) class_patterns+=("$(to_regex "$item")") ;; esac
done <"$map"
for member in A B C D E; do
  case "$stocked" in *" $member "*) : ;; *) refuse "classes.yml gives class $member no tree set" ;; esac
done
# The changed paths are the diff's own, each with the one word this script chooses: `filename`, and a rename's
# `previous_filename`, since a move out of a tree changes that tree, as the CI gate judges it. Each is a JSON string, so
# a newline inside a name cannot be a second line and a name needing an escape is refused. A source is tagged because
# `changedFiles` counts a rename as one file, and the one read judged is the one counted.
changed=$(gh_read pr view "$pr" --json changedFiles --jq .changedFiles)
case "$changed" in ''|*[!0123456789]*) refuse "changedFiles is not a count" ;; esac
files=$(gh_read api "repos/{owner}/{repo}/pulls/$pr/files" --paginate \
  --jq '.[] | (.filename | @json), (.previous_filename // empty | "from " + @json)')
verdicts=()
list=""
names=0
while IFS= read -r line; do
  [ -n "$line" ] || continue
  case "$line" in
    'from '*) line=${line#from } ;;
    *) names=$((names + 1)) ;;
  esac
  case "$line" in
    '"'*'"') ;;
    *) refuse "a changed path did not arrive as a JSON string" ;;
  esac
  case "$line" in *\\*) refuse "a changed path is not a plain path" ;; esac
  path="${line:1:${#line}-2}"
  # The characters are spelled out, not ranged, so no locale reads them and
  # no process is spawned per changed path.
  case "$path" in
    ''|*[!ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_./@+\(\)-]*)
      refuse "a changed path is not a plain path" ;;
  esac
  case "$path" in *[/.]*) ;; *) refuse "a changed path is not a plain path" ;; esac
  case "/$path/" in *//*|*/./*|*/../*) refuse "a changed path is not a plain path" ;; esac
  verdicts+=("$path")
  list+="$path"$'\n'
done < <(printf '%s\n' "$files")
# The endpoint stops short of a very large pull request, and a short list would read as the whole diff.
[ "$names" -eq "$changed" ] || refuse "the files endpoint returned $names names against changedFiles $changed"
# One grep per pattern over every path, reading a pipe, and line N's hit is
# path N's: a validated path holds no newline. Status 2, a pattern grep
# cannot compile, matches nothing; a status past 2 stops the run, since a
# verdict read past a grep that did not finish is `outside` for a path
# nothing judged.
hits=(); class_hits=()
if [ -n "$list" ]; then
  for re in "${patterns[@]}"; do
    matched=$(printf '%s\n' "${list%$'\n'}" | grep -nE -e "$re") || [ $? -le 2 ] || grep_died
    while IFS=: read -r n _; do
      [ -z "$n" ] || hits[n - 1]=1
    done < <(printf '%s\n' "$matched")
  done
  for re in "${class_patterns[@]}"; do
    matched=$(printf '%s\n' "${list%$'\n'}" | grep -nE -e "$re") || [ $? -le 2 ] || grep_died
    while IFS=: read -r n _; do
      [ -z "$n" ] || class_hits[n - 1]=1
    done < <(printf '%s\n' "$matched")
  done
fi
# Printed once every grep has finished, so a run stopped above prints nothing that reads as a pull request with no paths.
printf 'class %s\n' "$class"
i=0
# bash 3.2 calls an empty array unbound under `set -u`, and a pull request can have no changed file.
for path in ${verdicts[@]+"${verdicts[@]}"}; do
  verdict=outside
  [ -z "${hits[i]:-}" ] || verdict=inside
  # The class's verdict overrides the row's, because no row may declare a path
  # its class does not admit: `outside-class` is resolved by a narrower diff or
  # a different class, and `outside` by a narrower diff or a wider row.
  [ -n "${class_hits[i]:-}" ] || verdict=outside-class
  printf '%s %s\n' "$verdict" "$path"
  i=$((i + 1))
done
