#!/usr/bin/env bash
# Remove a /branch worktree under .claude/worktrees/, without -f, once no
# code-index refresh holds it. A prefix grant on `git worktree remove` also
# buys the `-f` that discards what git's refusal protects; here the command is
# fixed and its one argument shape-checked, so no flag can arrive.
set -euo pipefail
[ "$#" -eq 1 ] || { echo "usage: git-worktree-remove.sh <path>" >&2; exit 2; }
path="$1"
[[ "$path" =~ ^\.claude/worktrees/[A-Za-z0-9][A-Za-z0-9._-]*$ ]] ||
  { echo "path must be .claude/worktrees/<name>" >&2; exit 2; }
git rev-parse --git-dir >/dev/null 2>&1 ||
  { echo "not in a git repository" >&2; exit 2; }
[ -z "$(git rev-parse --show-prefix)" ] ||
  { echo "run from the checkout root" >&2; exit 2; }
[ "$(git rev-parse --git-dir)" = "$(git rev-parse --git-common-dir)" ] ||
  { echo "run from the main checkout, not a linked worktree" >&2; exit 2; }
[ -d "$path" ] || { echo "not an existing directory: $path" >&2; exit 3; }
# Asked of git, because `git worktree list` paths are not comparable with the
# shell's under MSYS. A plain directory answers with this checkout's own git
# directory, found by walking up, and so reads as no linked worktree.
common_here=$(git rev-parse --path-format=absolute --git-common-dir)
common_there=$(git -C "$path" rev-parse --path-format=absolute --git-common-dir) &&
  dir_there=$(git -C "$path" rev-parse --path-format=absolute --git-dir) ||
  { echo "not a git worktree: $path" >&2; exit 3; }
[ "$common_there" = "$common_here" ] && [ "$dir_there" != "$common_there" ] ||
  { echo "not a linked worktree of this repository: $path" >&2; exit 3; }
# Every refresh worker holds this lock for its whole run, the index update
# included, and Windows will not delete a file held open: a remove meanwhile
# deletes part of the tree and fails. So the lock is taken first, with the
# hook's own primitive, and one still held at the bound removes nothing.
cache="$path/.claude/cache/codebase-index"
if [ -f "$cache/refresh.lock" ] || [ -f "$cache/refresh.pending" ]; then
  python=python3
  command -v py >/dev/null 2>&1 && python="py -3.12"
  if ! $python -I - "$cache" <<'PY'; then
import os, sys, time
lock, pending = (os.path.join(sys.argv[1], name) for name in ("refresh.lock", "refresh.pending"))
bound, grace = 30, 2
if sys.platform == "win32":
    import msvcrt
    def grab(handle):
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    def release(handle):
        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
else:
    import fcntl
    def grab(handle):
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    def release(handle):
        fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
start, unclaimed = time.monotonic(), None
while True:
    now = time.monotonic()
    with open(lock, "a+b") as handle:
        try:
            grab(handle)
        except OSError:
            unclaimed = None
        else:
            try:
                if not os.path.exists(pending):
                    sys.exit(0)
                # A request with the lock free is a worker on its way to the
                # lock, or one a failed refresh put back that nobody will take.
                unclaimed = now if unclaimed is None else unclaimed
                if now - unclaimed >= grace:
                    try:
                        os.remove(pending)
                    except FileNotFoundError:
                        pass
                    sys.exit(0)
            finally:
                release(handle)
    if now - start >= bound:
        sys.exit(f"a code-index refresh still holds the worktree after {bound} s")
    time.sleep(0.2)
PY
    echo "nothing removed: $path; run this again once the refresh ends" >&2
    exit 5
  fi
fi
git worktree remove "$path"
