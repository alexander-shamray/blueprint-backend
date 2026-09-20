#!/usr/bin/env python3
"""Refresh the code index of the checkout that was edited.

Nothing runs `update` on its own, so between an edit and the query that
reports the index stale it describes a tree that has moved. The checkout is
the event's `cwd` walked up to its root, because `/branch` moves a session
into a sibling worktree while `CLAUDE_PROJECT_DIR` names the one it left.
Detached and never waited on, and it returns 0 whatever happens: it runs on
every edit, and an index that cannot refresh is no reason to fail one.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

# Where the CLI keeps a checkout's index. A checkout without one is not
# stale, it is unindexed, and building one per throwaway worktree is a cost
# nobody asked this hook for — so it is left alone rather than
# initialised.
CACHE = Path(".claude") / "cache" / "codebase-index"


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
            return path if path.is_absolute() or not base else Path(str(base)) / path
    return None


def target(event: dict) -> Path | None:
    """The indexed checkout to refresh: the one holding the file that changed.

    The edited path decides, because after `/branch` the session's directory
    and the checkout an edit is admitted against can be different trees.
    `cwd` answers when the event names no file and `CLAUDE_PROJECT_DIR` when
    it names neither -- each in turn, and none of them as a second chance
    after an unindexed tree, which would refresh the one that did not change.
    """
    named = edited(event) or event.get("cwd") or os.environ.get("CLAUDE_PROJECT_DIR")
    if not named:
        return None
    root = checkout_root(Path(str(named)))
    if root is None or not (root / CACHE).is_dir():
        return None
    return root


def main() -> int:
    try:
        event = json.loads(sys.stdin.read() or "{}")
    except (json.JSONDecodeError, OSError, UnicodeDecodeError):
        event = {}

    root = target(event if isinstance(event, dict) else {})
    if root is None:
        return 0

    # CBX_NO_SKILL_AUTO_UPDATE, because an unpinned newer package rewrites the
    # tracked files under .claude/skills/codebase-index/ when it runs. The
    # `cbx` wrapper sets it; this calls the CLI, so it sets it here.
    environment = dict(os.environ, CBX_NO_SKILL_AUTO_UPDATE="1", PYTHONSAFEPATH="1")

    # The console script first, then the module through this interpreter,
    # which is the pinned one because settings.json runs this file with it.
    # `py -3.12 -m pip` is a supported install and leaves the script in a
    # directory that need not be on PATH, so both `cbx` wrappers fall back
    # the same way. `-P` keeps a checkout's own codebase_index.py off the
    # import path.
    for command in (
        ["codebase-index", "update"],
        [sys.executable, "-P", "-m", "codebase_index", "update"],
    ):
        try:
            subprocess.Popen(
                command,
                cwd=str(root),
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            return 0
        except OSError:
            continue

    # Neither form is runnable here. See the docstring on why an index that
    # cannot refresh is not allowed to fail the edit.
    return 0


if __name__ == "__main__":
    sys.exit(main())
