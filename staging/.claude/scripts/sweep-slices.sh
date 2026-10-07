#!/usr/bin/env bash
# Cut a sweep's worktree into slices of at most BUDGET bytes, one list per slice
# beside the worktree, and print a summary; /bug-sweep and /security-sweep.
# The row partition is the sweeps' and lives here, so a tracked path no row owns
# refuses the run rather than going unswept.
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

g() { git -C "$resolved" "$@"; }
pinned=$(g rev-parse --verify HEAD)
since=""
if [ "$mode" != full ] && last=$(g rev-parse --verify --quiet "refs/sweeps/$kind^{commit}"); then
  # A ref that is not an ancestor marks a sweep of another line of history, so
  # the diff from it would skip files this commit never had swept.
  if g merge-base --is-ancestor "$last" "$pinned"; then since="$last"; fi
fi

row_of() {
  case "$kind:$1" in
    bug:src/BuildingBlocks/*) echo building-blocks ;;
    bug:src/*) echo services ;;
    bug:tests/*) echo suites ;;
    security:src/*|security:tests/*) echo source ;;
    *:tools/*|*:.github/*|*:.claude/*) echo tooling ;;
    *:deploy/*|*:.config/*) echo deploy ;;
    *:docs/*) echo samples ;;
    */*) return 1 ;;
    *) echo deploy ;;
  esac
}

# The bytes inside fences, the only part of a sample the sweeps audit. A fence
# closes only on its own character, at least as long, with nothing after it.
fenced_bytes() {
  awk '
    match($0, /^[ \t]*(```+|~~~+)/) {
      mark = substr($0, RSTART, RLENGTH); sub(/^[ \t]*/, "", mark)
      ch = substr(mark, 1, 1); n = length(mark); rest = substr($0, RSTART + RLENGTH)
      if (!open) { open = 1; och = ch; on = n; next }
      if (ch == och && n >= on && rest ~ /^[ \t]*$/) { open = 0; next }
    }
    open { total += length($0) + 1 }
    END { print total + 0 }'
}

# Each git read lands in a file with its status checked, NUL-separated, so a
# failing git fails the run and a name holding a newline stays one name.
mkdir "$out"
# Any exit before the plan is printed takes the half-cut lists with it.
trap 'rm -rf "$out"' EXIT
g ls-tree -r -l -z --full-tree "$pinned" > "$out/.tree"
g grep -I -l -z -e '' "$pinned" -- . > "$out/.text" || [ "$?" -eq 1 ]
: > "$out/.changed"
[ -z "$since" ] || g diff --name-only --no-renames -z "$since" "$pinned" > "$out/.changed"
mapfile -d '' tree < "$out/.tree"
mapfile -d '' texts < "$out/.text"
mapfile -d '' changes < "$out/.changed"
rm -f "$out/.tree" "$out/.text" "$out/.changed"
[ "${#tree[@]}" -gt 0 ] || { echo "no tracked files at $pinned" >&2; exit 3; }

declare -A text=() changed=() rowfiles=()
for f in "${texts[@]}"; do text[${f#"$pinned:"}]=1; done
for f in "${changes[@]}"; do changed[$f]=1; done

tracked=0 unowned=0 unchanged=0 empty=0 binary=0 record=0 fenceless=0 listed=0
for rec in "${tree[@]}"; do
  meta=${rec%%$'\t'*} file=${rec#*$'\t'}
  # A slice list is one name a line, so a name holding a line break cannot be
  # listed; refusing it is the only answer that does not drop it unsaid.
  case "$file" in *$'\n'*|*$'\r'*) echo "a tracked name with a line break" >&2; exit 3 ;; esac
  tracked=$((tracked + 1))
  size=${meta##* }
  row=$(row_of "$file") || { echo "no row owns: $file" >&2; unowned=$((unowned + 1)); continue; }
  [ -z "$since" ] || [ -n "${changed[$file]:-}" ] || { unchanged=$((unchanged + 1)); continue; }
  [ "$size" != 0 ] || { empty=$((empty + 1)); continue; }
  [ -n "${text[$file]:-}" ] || { binary=$((binary + 1)); continue; }
  # Closed records are never edited to match the code (CLAUDE.md), so a defect
  # in their samples has no fix to file; owned by the row, counted, not read.
  case "$file" in
    docs/superpowers/*|docs/pr-decision-log.md|docs/lessons.md) record=$((record + 1)); continue ;;
  esac
  if [ "$row" = samples ]; then
    size=$(g cat-file blob "$pinned:$file" | fenced_bytes)
    [ "$size" -gt 0 ] || { fenceless=$((fenceless + 1)); continue; }
  fi
  listed=$((listed + 1))
  rowfiles[$row]+="$size $file"$'\n'
done
[ "$unowned" -eq 0 ] || { echo "$unowned tracked path(s) no row owns" >&2; exit 3; }
if [ -z "$since" ] && [ "$listed" -eq 0 ]; then
  echo "a full run that lists nothing has read nothing" >&2; exit 3
fi

echo "pinned $pinned"
if [ -n "$since" ]; then echo "mode since $since"; else echo "mode full"; fi
echo "tracked $tracked listed $listed unchanged $unchanged empty $empty binary $binary record $record fenceless $fenceless"
n=0
for row in $(printf '%s\n' "${!rowfiles[@]}" | sort); do
  bytes=0 files=0 list=""
  flush() {
    [ "$files" -gt 0 ] || return 0
    n=$((n + 1))
    printf '%s' "$list" > "$out/$n.txt"
    echo "slice $n $row $files $bytes $out/$n.txt"
    bytes=0 files=0 list=""
  }
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    size=${line%% *} file=${line#* }
    [ $((bytes + size)) -le "$BUDGET" ] || flush
    bytes=$((bytes + size)) files=$((files + 1)) list+="$file"$'\n'
  done <<<"${rowfiles[$row]}"
  flush
done
trap - EXIT
