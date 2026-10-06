#!/usr/bin/env python3
"""Refresh the code index of the checkout an edit or a shell call changed. One
refresh owns an index at a time, and it returns 0 whatever happens: an index
that cannot refresh is no reason to fail the call that provoked it."""

from __future__ import annotations

import json
import os
import sqlite3
import subprocess
import sys
import time
import urllib.parse
from pathlib import Path

# Where the CLI keeps a checkout's index. A checkout without one is not
# stale, it is unindexed, and a full build is minutes inside an agent's turn,
# so the hook never builds one: a linked worktree is seeded from its main
# checkout's instead, and anything else without an index is left alone.
CACHE = Path(".claude") / "cache" / "codebase-index"
INDEX = CACHE / "index.sqlite"

# `codebase-index update` does not serialise: overlapping runs against one
# checkout leave a single winner and `database is locked` for the rest, and
# the loser may be the run carrying the newest edit. Discarded streams make
# that silent, so this lock is what keeps the index honest.

# The operating system holds it, on an open descriptor, and releases it when
# the worker exits however it exits. A lock file read as data needs a
# staleness rule; a staleness rule needs a takeover; and a takeover between
# two claimants needs the very serialisation being asked for. The marker
# beside it is how an edit arriving mid-run reaches the worker running.
LOCK = CACHE / "refresh.lock"
PENDING = CACHE / "refresh.pending"

# An update that cannot finish is not allowed to hold the lock for ever.
REFRESH_TIMEOUT = 600

# A failed update is retried, because the failure worth retrying is the one
# this lock cannot prevent: the CLI errors rather than waits when another
# update holds the index, and the skill's documented `cbx update` runs
# outside the lock entirely. The bound is what keeps a detached worker off
# a broken install, and giving up is not final — the request goes back.
REFRESH_ATTEMPTS = 3
RETRY_PAUSE = 5

if sys.platform == "win32":
    import msvcrt

    def grab(handle) -> bool:
        """Take the lock on `handle`, or answer that somebody else has it."""
        try:
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            return False
        return True
else:
    import fcntl

    def grab(handle) -> bool:
        """Take the lock on `handle`, or answer that somebody else has it."""
        try:
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            return False
        return True


def checkout_root(start: Path) -> Path | None:
    """The checkout `start` sits in, by the `.git` at or above it."""
    try:
        candidates = (start, *start.parents)
    except OSError:
        return None
    for candidate in candidates:
        if (candidate / ".git").exists():
            return candidate
    return None


def edited(event: dict) -> Path | None:
    """The file the tool touched, resolved against the session's directory.

    A relative path in the event is relative to `cwd`, and an absolute one
    may name a different checkout entirely: `guard-edit-target` admits an
    edit against the session's tree or the one it forked from.
    """
    given = event.get("tool_input")
    if not isinstance(given, dict):
        return None
    for key in ("file_path", "notebook_path"):
        value = given.get(key)
        if isinstance(value, str) and value.strip():
            path = Path(value.strip())
            base = event.get("cwd")
            if not path.is_absolute() and base:
                path = Path(str(base)) / path
            # Resolved before anything walks it. `../../../src/Thing.cs` from a
            # worktree under `.claude/worktrees/` lands in the main checkout,
            # and the worktree is among that path's lexical parents — so an
            # unresolved walk finds the worktree's `.git` and refreshes the
            # tree the edit did not touch.
            try:
                return path.resolve()
            except OSError:
                return None
    return None


def main_checkout(root: Path) -> Path | None:
    """The main checkout `root` is a linked worktree of, or None, read from
    the files git writes because this runs on every call: a `gitdir:` file
    whose `commondir` names a `.git` directory. A submodule has no
    `commondir`, and a bare repository's is not named `.git`."""
    try:
        pointer = (root / ".git").read_text(encoding="utf-8").strip()
        if not pointer.startswith("gitdir:"):
            return None
        git_dir = root / pointer[len("gitdir:"):].strip()
        common = (git_dir / (git_dir / "commondir").read_text(encoding="utf-8").strip()).resolve()
        if common.name != ".git" or common.parent == root.resolve():
            return None
    except (OSError, UnicodeDecodeError, ValueError):
        return None
    return common.parent


def seed_source(root: Path) -> Path | None:
    """The index a linked worktree without one is seeded from, or None: only
    a regular file at the main checkout's own index path, so a link planted
    there cannot point the copy at anything else."""
    if (root / INDEX).exists():
        return None
    main = main_checkout(root)
    if main is None:
        return None
    source = main / INDEX
    if source.is_symlink() or not source.is_file():
        return None
    return source


def target(event: dict) -> Path | None:
    """The indexed or seedable checkout holding the file that changed, else
    the one `cwd` names, which follows a shell call into a worktree, then
    `CLAUDE_PROJECT_DIR`, then the working directory `git-worktree-fork.sh`
    runs it in — each in turn, never as a second chance after an unindexed
    tree, which would refresh the one that did not change."""
    named = (edited(event) or event.get("cwd")
             or os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd())
    root = checkout_root(Path(str(named)))
    if root is None:
        return None
    if (root / CACHE).is_dir():
        return root
    if seed_source(root) is None:
        return None
    try:
        (root / CACHE).mkdir(parents=True, exist_ok=True)
    except OSError:
        return None
    return root


def seed(root: Path) -> None:
    """Give a linked worktree with no index its main checkout's, through
    SQLite's backup because a copied file can tear mid-`update`. Renamed in so
    `update` never opens a partial file, after any WAL an earlier seed left;
    only `index.sqlite` crosses, as the rest of that cache is its own state."""
    source = seed_source(root)
    if source is None:
        return
    index = root / INDEX
    partial = index.with_name(f"{index.name}.seed.{os.getpid()}")
    try:
        for leftover in ("-wal", "-shm"):
            index.with_name(index.name + leftover).unlink(missing_ok=True)
        # The authority stays empty: `as_uri()` puts a UNC server or a `?` there,
        # and SQLite refuses both. `mode=rw` refuses a source gone since
        # `seed_source` looked, where a plain connect would create one empty.
        posix = source.as_posix()
        reading = sqlite3.connect(
            "file://" + ("" if posix.startswith("/") else "/") + urllib.parse.quote(posix) + "?mode=rw",
            uri=True)
        try:
            writing = sqlite3.connect(str(partial))
            try:
                reading.backup(writing)
            finally:
                writing.close()
        finally:
            reading.close()
        os.replace(partial, index)
    except (OSError, sqlite3.Error):
        pass
    finally:
        try:
            partial.unlink(missing_ok=True)
        except OSError:
            pass


def request(root: Path) -> None:
    """Record that an edit wants a refresh, whoever ends up running it."""
    try:
        (root / PENDING).write_text("", encoding="utf-8")
    except OSError:
        pass


def take_request(root: Path) -> bool:
    """Consume an outstanding request, if there is one."""
    try:
        (root / PENDING).unlink()
        return True
    except OSError:
        return False


def busy(root: Path) -> bool:
    """Whether a worker already holds this checkout's lock.

    Advisory, and deliberately so: a worker may start between this answer
    and the spawn it saves. That costs one process which takes no lock and
    exits at once, because the lock the worker itself takes is the one that
    decides. Getting this wrong cannot produce two refreshes, only two
    starts.
    """
    try:
        handle = open(root / LOCK, "a+b")
    except OSError:
        return False
    with handle:
        return not grab(handle)


def refresh(root: Path) -> bool:
    """One update, waited on and answered, because the streams are gone.

    The console script first, then the module through this interpreter:
    `py -3.12 -m pip` is a supported install and leaves the script in a
    directory that need not be on PATH, so both `cbx` wrappers fall back the
    same way. CBX_NO_SKILL_AUTO_UPDATE keeps an unpinned package from
    rewriting the tracked skill, and `-P` keeps a checkout's own
    codebase_index.py off the import path.
    """
    environment = dict(os.environ, CBX_NO_SKILL_AUTO_UPDATE="1", PYTHONSAFEPATH="1")
    for command in (
        ["codebase-index", "update"],
        [sys.executable, "-P", "-m", "codebase_index", "update"],
    ):
        try:
            done = subprocess.run(
                command, cwd=str(root), env=environment,
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, timeout=REFRESH_TIMEOUT, check=False)
            return done.returncode == 0
        except OSError:
            continue
        except subprocess.TimeoutExpired:
            return False
    return False


def refreshed(root: Path) -> bool:
    """One request's update, tried again while it keeps failing.

    The pause is what makes a retry worth anything: an update that answered
    a contended index answers the same one immediately afterwards.
    """
    for attempt in range(REFRESH_ATTEMPTS):
        if refresh(root):
            return True
        if attempt + 1 < REFRESH_ATTEMPTS:
            time.sleep(RETRY_PAUSE)
    return False


def drain(root: Path) -> bool:
    """Every outstanding request, refreshed; false when one was given up on.

    The request is put back rather than dropped, so the edit behind it is
    carried by the next worker instead of waiting for an edit that happens
    to arrive. Returning here rather than looping on it is what keeps a put
    back request from becoming a spin against an install that cannot work.
    """
    while take_request(root):
        if not refreshed(root):
            request(root)
            return False
    return True


def work(root: Path) -> int:
    """Refresh until no request is outstanding, holding the lock throughout.

    The lock is held for the whole drain rather than per update, so an edit
    arriving mid-run reaches this worker through the marker instead of
    starting a second one. A worker that cannot take it has nothing to do:
    the marker it was started for belongs to whoever holds the lock.
    """
    while True:
        try:
            handle = open(root / LOCK, "a+b")
        except OSError:
            return 0
        with handle:
            if not grab(handle):
                return 0
            seed(root)
            if not drain(root):
                return 0
        # The lock is gone by here, and that is the point. A request made
        # between the last look above and this release found `busy` true and
        # left a marker rather than a worker, so it is taken now instead of
        # waiting for whatever edit comes next. Another worker may have taken
        # the lock in between, and then the grab above fails and this returns:
        # the marker is theirs.
        if not (root / PENDING).exists():
            return 0


def main() -> int:
    if len(sys.argv) > 2 and sys.argv[1] == "--worker":
        return work(Path(sys.argv[2]))

    # Bytes, decoded here: Python reads a Windows pipe in the ANSI code page,
    # which turns a non-ASCII checkout path into one that does not exist.
    try:
        event = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
    except (json.JSONDecodeError, OSError, UnicodeDecodeError):
        event = {}

    root = target(event if isinstance(event, dict) else {})
    if root is None:
        return 0

    # The marker first, so a worker that is already draining finds this edit
    # whether or not the spawn below happens.
    request(root)
    if busy(root):
        return 0

    try:
        subprocess.Popen(
            [sys.executable, "-P", str(Path(__file__).resolve()), "--worker", str(root)],
            cwd=str(root),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError:
        # Nothing to undo: no lock was taken here, and the marker stays for
        # the next edit or the worker already running.
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
