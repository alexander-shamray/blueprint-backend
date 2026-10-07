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

pinned=$(git -C "$resolved" rev-parse --verify HEAD)
since=""
if [ "$mode" != full ] && last=$(git -C "$resolved" rev-parse --verify --quiet "refs/sweeps/$kind^{commit}"); then
  # A ref that is not an ancestor marks a sweep of another line of history, so
  # the diff from it would skip files this commit never had swept.
  if git -C "$resolved" merge-base --is-ancestor "$last" "$pinned"; then since="$last"; fi
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

# Text files only: a binary has nothing an auditor can read, and is counted.
declare -A text=() changed=()
while IFS= read -r f; do text[${f#"$pinned:"}]=1; done \
  < <(git -C "$resolved" grep -I -l -e '' "$pinned" -- .)
if [ -n "$since" ]; then
  while IFS= read -r f; do changed[$f]=1; done \
    < <(git -C "$resolved" diff --name-only --no-renames "$since" "$pinned")
fi

mkdir "$out"
unowned=0 binary=0 record=0
declare -A rowfiles=()
while IFS=$'\t' read -r meta file; do
  size=${meta##* }
  case "$file" in \"*) rm -rf "$out"; echo "a path git had to quote: $file" >&2; exit 3 ;; esac
  [ -z "$since" ] || [ -n "${changed[$file]:-}" ] || continue
  row=$(row_of "$file") || { echo "no row owns: $file" >&2; unowned=$((unowned + 1)); continue; }
  [ -n "${text[$file]:-}" ] || { binary=$((binary + 1)); continue; }
  # Closed records are never edited to match the code (CLAUDE.md), so a defect
  # in their samples has no fix to file; owned by the row, counted, not read.
  case "$file" in
    docs/superpowers/*|docs/pr-decision-log.md|docs/lessons.md) record=$((record + 1)); continue ;;
  esac
  # A sample's size is its fenced lines, the only part the sweeps audit there.
  if [ "$row" = samples ]; then
    size=$(git -C "$resolved" cat-file blob "$pinned:$file" |
      awk '/^[[:space:]]*(```|~~~)/ { f = !f; next } f { n += length($0) + 1 } END { print n + 0 }')
    [ "$size" -gt 0 ] || continue
  fi
  rowfiles[$row]+="$size $file"$'\n'
done < <(git -C "$resolved" ls-tree -r -l --full-tree "$pinned")
[ "$unowned" -eq 0 ] || { rm -rf "$out"; echo "$unowned tracked path(s) no row owns" >&2; exit 3; }

echo "pinned $pinned"
if [ -n "$since" ]; then echo "mode since $since"; else echo "mode full"; fi
echo "binary-skipped $binary"
echo "record-skipped $record"
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
  while read -r size file; do
    [ -n "$file" ] || continue
    [ $((bytes + size)) -le "$BUDGET" ] || flush
    bytes=$((bytes + size)) files=$((files + 1)) list+="$file"$'\n'
  done <<<"${rowfiles[$row]}"
  flush
done
