#!/usr/bin/env bash
# Cut a sweep's worktree into slices of at most BUDGET bytes, one list per slice
# beside it, by the rows of this checkout's .harness/sweep-rows.tsv; a tracked
# path no row owns refuses the run. Bash 3.2, /bin/bash on macOS, has no
# `mapfile` and no associative array, so the rows are one awk pass over lists.
set -euo pipefail
usage="usage: sweep-slices.sh <bug|security> <worktree> [full]"
[ "$#" -ge 2 ] && [ "$#" -le 3 ] || { echo "$usage" >&2; exit 2; }
kind="$1" path="$2" mode="${3:-}"
case "$kind" in bug|security) : ;; *) echo "$usage" >&2; exit 2 ;; esac
case "$mode" in ''|full) : ;; *) echo "$usage" >&2; exit 2 ;; esac
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
BUDGET=240000

# The shape git-worktree-detach.sh makes and git-worktree-drop.sh requires, so
# the one directory this writes is the sweep's own and never a caller's choice.
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $path" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
out="$resolved.slices"
[ ! -e "$out" ] || { echo "slices already cut: $out" >&2; exit 3; }

# The rows are this checkout's, as the helper is, and trusted as it is: a line
# moving a script into samples, where a file with no fence is not read, changes
# what a sweep reads, and is reviewed as a change to the helper would be.
rows="$(dirname "${BASH_SOURCE[0]}")/../../.harness/sweep-rows.tsv"
[ -r "$rows" ] || { echo "the row file cannot be read: .harness/sweep-rows.tsv" >&2; exit 3; }
# Tab-separated `kind pattern row holds`, `#` opening a comment line; a line of
# any other shape is refused naming it, so a typo never moves a path silently.
# A row `=<dir>/` is a mirror: a matching path whose copy under <dir>/ is the
# same blob is owned and counted, not listed, since that copy is read as itself;
# one whose copy is absent or differs falls through to the next row, read.
LC_ALL=C awk -F'\t' '
  { sub(/\r$/, "") }
  $0 == "" || /^#/ { next }
  NF != 4 { bad("not four tab-separated fields") }
  $1 != "bug" && $1 != "security" && $1 != "*" { bad("kind is not bug, security or *") }
  $2 == "" { bad("an empty pattern") }
  $3 != "-" && $3 !~ /^[a-z][a-z-]*$/ && $3 !~ /^=[A-Za-z0-9._-]+\/$/ { bad("row is not -, a lower-case word or =<dir>/") }
  $4 == "" { bad("an empty holds") }
  # A pattern awk cannot compile stops awk here, which the caller refuses too.
  { if ("" ~ $2) n++ }
  function bad(why) { printf "sweep-rows.tsv line %d: %s: %s\n", NR, why, $0 > "/dev/stderr"; failed = 1; exit 3 }
  END { if (failed) exit 3 }
' "$rows" || { echo "the row file is malformed: .harness/sweep-rows.tsv" >&2; exit 3; }

g() { git -C "$resolved" "$@"; }
pinned=$(g rev-parse --verify HEAD)
since=""
if [ "$mode" != full ] && last=$(g rev-parse --verify --quiet "refs/sweeps/$kind^{commit}"); then
  # A ref that is not an ancestor marks a sweep of another line of history, so
  # the diff from it would skip files this commit never had swept.
  if g merge-base --is-ancestor "$last" "$pinned"; then since="$last"; fi
fi
# The swept tree's .gitattributes would decide what `grep -I` calls binary, so
# they are read from the empty tree instead; a git older than 2.41 refuses the
# option, and the run fails rather than trusting them.
empty=$(g hash-object -t tree --stdin </dev/null)

# The bytes inside fences, the only part of a sample the sweeps audit, one line
# per blob of a `git cat-file --batch` stream. A fence closes only on its own
# character, at least as long, with only blanks or a CR after it. A blob ends at
# a byte count, so LC_ALL=C, as gawk counts characters in a UTF-8 locale, and
# BINMODE=3, as gawk under MSYS strips a CRLF line's CR; other awks ignore it.
fenced_bytes() {
  LC_ALL=C awk -v BINMODE=3 '
    state == 2 { if ($0 != "") bad = 1; state = 0; next }
    state == 0 {
      if (NF != 3 || $2 != "blob") { bad = 1; exit }
      left = $3; open = 0; total = 0; state = 1; next
    }
    {
      left -= length($0) + 1; fence = 0
      if (match($0, /^[ \t]*(```+|~~~+)/)) {
        mark = substr($0, RSTART, RLENGTH); sub(/^[ \t]*/, "", mark)
        ch = substr(mark, 1, 1); n = length(mark); rest = substr($0, RSTART + RLENGTH)
        if (!open) { open = 1; och = ch; on = n; fence = 1 }
        else if (ch == och && n >= on && rest ~ /^[ \t\r]*$/) { open = 0; fence = 1 }
      }
      if (open && !fence) total += length($0) + 1
      if (left <= 0) { print total + 0; state = left == 0 ? 2 : 0 }
    }
    END { if (bad || state) { print "unreadable cat-file stream" > "/dev/stderr"; exit 1 } }'
}

# A NUL-separated list as one name a line. A name holding a line break cannot
# be a line of a list, and refusing it is the only answer that does not drop it
# unsaid: the NULs counted before must equal the lines counted after.
lines_of() {
  local nuls lines
  nuls=$(tr -cd '\0' < "$1" | wc -c)
  tr '\0' '\n' < "$1" > "$2"
  lines=$(wc -l < "$2")
  [ $((lines)) -eq $((nuls)) ] ||
    { echo "a tracked name with a line break" >&2; exit 3; }
}

# Each git read lands in a file with its status checked, NUL-separated, so a
# failing git fails the run and a name holding a newline stays one name.
mkdir "$out"
# Any exit before the plan is printed takes the half-cut lists with it.
trap 'rm -rf "$out"' EXIT
g ls-tree -r -l -z --full-tree "$pinned" > "$out/tree.z"
g --attr-source="$empty" grep -I -l -z -e '' "$pinned" -- . > "$out/text.z" || [ "$?" -eq 1 ]
: > "$out/changed.z"
[ -z "$since" ] || g diff --name-only --no-renames -z "$since" "$pinned" > "$out/changed.z"
lines_of "$out/tree.z" "$out/tree.txt"
lines_of "$out/text.z" "$out/text.raw"
lines_of "$out/changed.z" "$out/changed.txt"
# Each tracked path's blob, for the mirror rows' comparison: `oid<TAB>path`.
awk -F'\t' '{ split($1, m, " "); print m[3] "\t" $2 }' "$out/tree.txt" > "$out/oids.txt"
sed "s/^[0-9a-f]*://" "$out/text.raw" > "$out/text.txt"
[ -s "$out/tree.txt" ] || { echo "no tracked files at $pinned" >&2; exit 3; }
# A tab or a carriage return in a name would split or trim a list line, and
# `git ls-tree` puts exactly one tab between the metadata and the name.
[ $(($(tr -cd '\r' < "$out/tree.txt" | wc -c))) -eq 0 ] &&
  awk -F'\t' 'NF != 2 { bad = 1 } END { exit bad }' "$out/tree.txt" ||
  { echo "a tracked name with a tab or a carriage return" >&2; exit 3; }

# The rows of this kind in file order, the first match winning (a mirror row
# passes a path with no equal counterpart on to the next match); row `-` and no
# match alike leave a path unowned, printed on stderr and counted, and the run
# refuses below. The other lines are `L row size file` for what the sweep reads
# and `B file` for a binary one it does not.
awk -F'\t' -v kind="$kind" -v since="$since" '
  function row_of(f,   i) {
    for (i = 1; i <= nrows; i++) {
      if (f !~ pattern[i]) continue
      if (row[i] ~ /^=/) {
        copy = substr(row[i], 2) f
        if ((copy in oid) && oid[copy] == oid[f]) return "="
        continue
      }
      return row[i] == "-" ? "" : row[i]
    }
    return ""
  }
  FILENAME == ARGV[1] {
    sub(/\r$/, "")
    if ($0 == "" || $0 ~ /^#/) next
    if ($1 == kind || $1 == "*") { nrows++; pattern[nrows] = $2; row[nrows] = $3 }
    next
  }
  FILENAME == ARGV[2] { text[$0] = 1; next }
  FILENAME == ARGV[3] { changed[$0] = 1; next }
  FILENAME == ARGV[4] { oid[$2] = $1; next }
  {
    tracked++
    file = $2; n = split($1, meta, " "); size = meta[n]
    if (size !~ /^[0-9]+$/) size = 0
    r = row_of(file)
    if (r == "") { print "no row owns: " file > "/dev/stderr"; unowned++; next }
    if (r == "=") { mirrored++; next }
    if (since != "" && !(file in changed)) { unchanged++; next }
    if (size == 0) { empty++; next }
    if (!(file in text)) { binary++; printf "B\t%s\n", file; next }
    # Closed records are never edited to match the code, so a defect in their
    # samples has no fix to file; owned by the row, counted, not read.
    if (file ~ /^docs\/superpowers\// || file == "docs/pr-decision-log.md" || file == "docs/lessons.md") {
      record++; next
    }
    printf "L\t%s\t%s\t%s\n", r, size, file
  }
  END { printf "T\t%d\t%d\t%d\t%d\t%d\t%d\t%d\n", tracked, unowned, unchanged, empty, binary, record, mirrored }
' "$rows" "$out/text.txt" "$out/changed.txt" "$out/oids.txt" "$out/tree.txt" > "$out/plan.raw"

totals=$(grep '^T' "$out/plan.raw")
IFS=$'\t' read -r _ tracked unowned unchanged empty binary record mirrored <<<"$totals"
[ "$unowned" -eq 0 ] || { echo "$unowned tracked path(s) no row owns" >&2; exit 3; }
# No auditor reads a binary file, so each is named in a list the plan points at:
# a NUL byte is all it takes to make a script one.
awk -F'\t' '$1 == "B" { print $2 }' "$out/plan.raw" > "$out/binary.txt"

# A sample counts only its fenced lines; one with none is counted, not listed.
# Every sample goes through one cat-file and one awk, in tree order, rather than
# a pipeline per file, which under MSYS is a fork per file and most of a run;
# the answers are counted, so a short stream fails rather than mislists.
awk -F'\t' '$1 == "L" && $2 != "samples" { print $2 "\t" $3 "\t" $4 }' \
  "$out/plan.raw" > "$out/plan.rows"
awk -F'\t' '$1 == "L" && $2 == "samples" { print $4 }' "$out/plan.raw" > "$out/samples.txt"
fenceless=0
if [ -s "$out/samples.txt" ]; then
  awk -v pinned="$pinned" '{ print pinned ":" $0 }' "$out/samples.txt" |
    g cat-file --batch | fenced_bytes > "$out/samples.fenced"
  want=$(($(wc -l < "$out/samples.txt"))) got=$(($(wc -l < "$out/samples.fenced")))
  [ "$got" -eq "$want" ] || { echo "fences read for $got of $want samples" >&2; exit 3; }
  fenceless=$(awk '$1 == 0 { n++ } END { print n + 0 }' "$out/samples.fenced")
  paste "$out/samples.fenced" "$out/samples.txt" |
    awk -F'\t' '$1 > 0 { print "samples\t" $1 "\t" $2 }' >> "$out/plan.rows"
fi
listed=$(($(wc -l < "$out/plan.rows")))
if [ -z "$since" ] && [ "$listed" -eq 0 ]; then
  echo "a full run that lists nothing has read nothing" >&2; exit 3
fi
# A stable sort by row keeps each row's files in tree order.
LC_ALL=C sort -t$'\t' -k1,1 -s "$out/plan.rows" > "$out/plan.sorted"

echo "pinned $pinned"
if [ -n "$since" ]; then echo "mode since $since"; else echo "mode full"; fi
echo "tracked $tracked listed $listed unchanged $unchanged empty $empty binary $binary record $record mirrored $mirrored fenceless $fenceless"
if [ "$binary" -gt 0 ]; then echo "binary $binary $out/binary.txt"; else rm -f "$out/binary.txt"; fi

n=0 bytes=0 files=0 current="" list=""
flush() {
  [ "$files" -gt 0 ] || return 0
  n=$((n + 1))
  printf '%s' "$list" > "$out/$n.txt"
  echo "slice $n $current $files $bytes $out/$n.txt"
  bytes=0 files=0 list=""
}
while IFS=$'\t' read -r row size file; do
  [ "$row" = "$current" ] || { flush; current="$row"; }
  [ $((bytes + size)) -le "$BUDGET" ] || flush
  bytes=$((bytes + size)) files=$((files + 1)) list="$list$file"$'\n'
done < "$out/plan.sorted"
flush
# Only the lists stay beside the worktree.
rm -f "$out"/*.z "$out"/*.raw "$out"/tree.txt "$out"/oids.txt "$out"/text.txt "$out"/changed.txt \
  "$out"/plan.rows "$out"/plan.sorted "$out"/samples.txt "$out"/samples.fenced
trap - EXIT
