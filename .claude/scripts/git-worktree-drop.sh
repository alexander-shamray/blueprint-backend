#!/usr/bin/env bash
# Remove the throwaway worktree a sweep created — /security-sweep or
# /bug-sweep — without -f.
#
# A sweep's teardown leans on git refusing to remove a checkout holding
# anything modified or untracked, and `git worktree remove -f`, which a prefix
# grant would license, defeats that guard.
#
# Registration is not ownership: every sibling PR worktree is registered too,
# and the audited tree is prompt-injection input, so a path arriving here may
# have been chosen by it to steer this at someone else's workspace. So the
# path must be a direct child of the canonical temp root named `secsweep-`
# plus six characters. The two halves are tested apart because a bash `case`
# does no pathname expansion, so `?` matches `/`.
#
# This is exclusion, not ownership, and the residual stands: the teardown
# passes the path, and any registered worktree of the right shape passes the
# checks, including one an abandoned sweep left behind. The shape is checked
# here rather than trusted to git-worktree-detach.sh, because a guard that
# holds only while another helper is unedited is no guard.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-drop.sh <path>" >&2; exit 2; }
path="$1"
case "$path" in -*) echo "path may not start with '-'" >&2; exit 2 ;; esac
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 2; }
tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  { echo "not a direct child of the temp root: $path" >&2; exit 2; }
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) echo "not a sweep-shaped temp path: $path" >&2; exit 2 ;;
esac
# Ask git whether this is a linked worktree of this repository, rather than
# comparing path strings against `git worktree list`. Under MSYS those strings
# are not comparable at all — git prints `C:/Users/…/Temp/x` where `pwd -P`
# prints `/tmp/x`, and a textual check refuses the sweep's own worktree on the
# host this repository is developed on. Both values below come from git in one
# format, so they compare.
common_here=$(git rev-parse --path-format=absolute --git-common-dir)
common_there=$(git -C "$path" rev-parse --path-format=absolute --git-common-dir 2>/dev/null) ||
  { echo "not a git worktree: $path" >&2; exit 3; }
[ "$common_there" = "$common_here" ] ||
  { echo "not a worktree of this repository: $path" >&2; exit 3; }
dir_there=$(git -C "$path" rev-parse --path-format=absolute --git-dir)
[ "$dir_there" != "$common_there" ] ||
  { echo "refusing to remove the main worktree: $path" >&2; exit 3; }
# A refusal and a half-finished removal are different failures. On Windows a
# process holding the tree fails git's delete of it, and git then drops the
# registration anyway. The status is captured, so the checks below run whatever
# git exits.
status=0
git worktree remove "$resolved" || status=$?
left=
if [ -e "$resolved" ]; then
  # Asked of the directory, git to git as above. A live worktree still answers
  # with the git directory it had, which is git's refusal, and everything stays.
  # A leftover's `.git` names an admin directory that has gone, or it has none,
  # and then git walks up to whatever repository holds the temp root, if any.
  if there=$(git -C "$resolved" rev-parse --path-format=absolute --git-dir 2>/dev/null) &&
    [ "$there" = "$dir_there" ]; then
    [ "$status" -ne 0 ] || echo "git exited 0 and $path is still a live worktree" >&2
    exit "$((status == 0 ? 1 : status))"
  fi
  # `rmdir` refuses one holding anything, as git-worktree-remove.sh's does: only
  # an empty directory goes, and any other is named.
  if rmdir "$resolved" 2>/dev/null; then
    echo "git exited $status after deleting $path; the empty directory is removed" >&2
  else
    left=1
  fi
elif [ "$status" -ne 0 ]; then
  echo "git exited $status after deleting $path" >&2
fi
# The slice lists sweep-slices.sh wrote beside it, and the reports a target's
# own executing sweep writes there; named from the checked path, never passed,
# and only once git has removed the worktree itself. They answer to no worktree
# now, whether or not a directory is left.
slices="$resolved.slices"
if [ -d "$slices" ] && [ ! -L "$slices" ]; then rm -rf -- "$slices"; fi
mutation="$resolved.mutation"
if [ -d "$mutation" ] && [ ! -L "$mutation" ]; then rm -rf -- "$mutation"; fi
[ -z "$left" ] ||
  { echo "unregistered but not deleted: $path is still there; remove it by hand" >&2; exit 4; }
