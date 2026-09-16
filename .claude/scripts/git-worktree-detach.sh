#!/usr/bin/env bash
# Fork the detached, pinned worktree a sweep audits in — /security-sweep or
# /bug-sweep — and nothing else: `git worktree add --detach <path> <commit>`.
#
# A prefix grant on `git worktree add` also buys `-B`, which resets an existing
# branch past the `git branch --force` and `-M` denies, and a prefix rule
# cannot exclude a flag.
#
# --detach is fixed rather than passed, because a sweep worktree carries no
# commits and must never hold a branch: the caller's branch stays checked out
# where it is.
#
# The path is made here rather than passed, so the only path git is handed is
# one this script has just created, and no sweep needs a `mktemp` grant, whose
# arbitrary template creates an empty directory or file anywhere the session
# can write.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-detach.sh <commit-sha>" >&2; exit 2; }
commit="$1"
# A resolved sha, never a ref: the caller reads `git rev-parse HEAD` once and
# passes the result, so HEAD is not resolved a second time under a tree another
# session may have moved. Checked before anything is created, so a bad argument
# leaves no directory behind.
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] ||
  { echo "commit must be a full 40-character sha: $commit" >&2; exit 2; }
git rev-parse --verify --quiet "$commit^{commit}" >/dev/null ||
  { echo "no such commit: $commit" >&2; exit 3; }

tmproot=$(cd "${TMPDIR:-/tmp}" 2>/dev/null && pwd -P) ||
  { echo "cannot resolve the temp root" >&2; exit 4; }
# Six X's, so the name `mktemp` invents is `secsweep-` plus exactly six
# characters — the shape git-worktree-drop.sh will later require before it
# removes anything. The two ends of the sweep's lifetime agree because one of
# them produced the name.
path=$(mktemp -d "$tmproot/secsweep-XXXXXX") ||
  { echo "cannot create a sweep worktree directory under $tmproot" >&2; exit 4; }
resolved=$(cd "$path" && pwd -P)

# Every refusal from here on takes the directory with it, because the next
# run's `mktemp` invents a different name and nothing would revisit this one.
#
# `rmdir`, never `rm -rf`: rmdir refuses a non-empty directory, so a
# half-created worktree is left for inspection rather than deleted by a
# cleanup path.
refuse() {
  echo "$1" >&2
  rmdir "$resolved" 2>/dev/null || true
  exit "$2"
}

# The shape check is kept though this script made the path, because it is the
# contract git-worktree-drop.sh depends on, and a check that holds only while
# the line above is unedited is not a contract. The direct-child test and the
# basename match are separate because a bash `case` does no pathname
# expansion: `?` matches `/`, so one pattern over the whole path would pass
# `$tmproot/secsweep-a/bbbb`.
[ "$(dirname "$resolved")" = "$tmproot" ] ||
  refuse "not a direct child of the temp root: $resolved" 2
case "$(basename "$resolved")" in
  secsweep-??????) : ;;
  *) refuse "not a sweep-shaped temp path: $resolved" 2 ;;
esac

# Redirected because stdout is this script's return value, and
# `git worktree add` writes "HEAD is now at <sha> <subject>" to stdout, which a
# caller capturing the output would take as part of the directory name.
git worktree add --detach "$resolved" "$commit" >&2 ||
  refuse "git worktree add refused $resolved at $commit" 3
# The path, and it is the POSIX spelling — the one the shell and this helper
# share. The caller still reads `git worktree list --porcelain` for the
# host-native spelling its readers need; under MSYS those are two strings for
# one directory, and on some hosts two directories.
printf '%s\n' "$resolved"
