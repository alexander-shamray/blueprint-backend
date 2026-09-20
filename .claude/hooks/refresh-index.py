#!/usr/bin/env python3
"""Refresh the code index of the checkout the edited file belongs to.

Claude Code runs no `update` of its own, so the index describes a tree that
has moved until a query reports it stale. The checkout comes from the edited
path, because after `/branch` the session's directory and the checkout an
edit is admitted against can be different trees. One refresh owns an index at
a time, and it returns 0 whatever happens: it runs on every edit, and an
index that cannot refresh is no reason to fail one.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

# Where the CLI keeps a checkout's index. A checkout without one is not
# stale, it is unindexed, and building one per throwaway worktree is a cost
# nobody asked this hook for — so it is left alone rather than
# initialised.
CACHE = Path(".claude") / "cache" / "codebase-index"

# `codebase-index update` does not serialise: overlapping runs against one
# checkout leave a single winner and `database is locked` for the rest, and
# the loser may be the run carrying the newest edit. Discarded streams make
# that silent, so the lock is what keeps the index honest rather than what
# keeps the machine quiet. The marker is how an edit arriving mid-run
# reaches the worker already running.
LOCK = CACHE / "refresh.lock"
PENDING = CACHE / "refresh.pending"

# A worker killed before it releases would block every later refresh, so a
# lock this old is taken rather than waited on. Longer than any real update
# and shorter than a working session.
STALE_SECONDS = 900

# An update that cannot finish is not allowed to hold the lock for ever.
REFRESH_TIMEOUT = 600


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
            # Resolved before anything walks it. `../main/src/Thing.cs` from a
            # sibling worktree lands in `main`, and the worktree is among that
            # path's lexical parents — so an unresolved walk finds the
            # worktree's `.git` and refreshes the tree the edit did not touch.
            try:
                return path.resolve()
            except OSError:
                return None
    return None


def target(event: dict) -> Path | None:
    """The indexed checkout to refresh: the one holding the file that changed.

    The edited path decides, because after `/branch` the session's directory
    and the checkout an edit is admitted against can be different trees.
    `cwd` answers when the event names no file and `CLAUDE_PROJECT_DIR` when
    it names neither — each in turn, and none of them as a second chance
    after an unindexed tree, which would refresh the one that did not change.
    """
    named = edited(event) or event.get("cwd") or os.environ.get("CLAUDE_PROJECT_DIR")
    if not named:
        return None
    root = checkout_root(Path(str(named)))
    if root is None or not (root / CACHE).is_dir():
        return None
    return root


def request(root: Path) -> None:
    """Record that an edit wants a refresh, whoever ends up running it."""
    try:
        (root / PENDING).write_text("", encoding="utf-8")
    except OSError:
        pass


def claim(root: Path) -> bool:
    """Take the refresh lock, or report that somebody else holds it."""
    lock = root / LOCK
    try:
        handle = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        try:
            if time.time() - lock.stat().st_mtime < STALE_SECONDS:
                return False
            lock.unlink()
            handle = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except OSError:
            return False
    except OSError:
        return False
    os.close(handle)
    return True


def release(root: Path) -> None:
    try:
        (root / LOCK).unlink()
    except OSError:
        pass


def take_request(root: Path) -> bool:
    """Consume an outstanding request, if there is one."""
    try:
        (root / PENDING).unlink()
        return True
    except OSError:
        return False


def refresh(root: Path) -> None:
    """One update, waited on, because the worker is already detached.

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
            subprocess.run(
                command, cwd=str(root), env=environment,
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, timeout=REFRESH_TIMEOUT, check=False)
            return
        except OSError:
            continue
        except subprocess.TimeoutExpired:
            return


def work(root: Path) -> int:
    """Refresh until no request is outstanding, then let the lock go."""
    while True:
        if take_request(root):
            refresh(root)
            continue
        release(root)
        # A request made between the take above and this release found the
        # lock held and left a marker rather than a worker, so it is picked
        # up here instead of waiting for whatever edit comes next.
        if (root / PENDING).exists() and claim(root):
            continue
        return 0


def main() -> int:
    if len(sys.argv) > 2 and sys.argv[1] == "--worker":
        return work(Path(sys.argv[2]))

    try:
        event = json.loads(sys.stdin.read() or "{}")
    except (json.JSONDecodeError, OSError, UnicodeDecodeError):
        event = {}

    root = target(event if isinstance(event, dict) else {})
    if root is None:
        return 0

    request(root)
    if not claim(root):
        # Somebody is refreshing this checkout already. The marker above is
        # what makes them run again, rather than this edit starting a second
        # update against the same index.
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
        # No worker started, so the lock must not be left behind it.
        release(root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
