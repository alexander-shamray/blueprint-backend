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


def target(event: dict) -> Path | None:
    """The indexed checkout to refresh, which is the one that was edited.

    The event's `cwd` decides alone whenever it has one. In a sibling
    worktree it is the tree that changed and `CLAUDE_PROJECT_DIR` is the tree
    that did not, so trying the variable when that tree turns out to be
    unindexed refreshes the wrong checkout rather than none. The variable
    answers only when the event is silent.
    """
    named = event.get("cwd") or os.environ.get("CLAUDE_PROJECT_DIR")
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

    try:
        subprocess.Popen(
            ["codebase-index", "update"],
            cwd=str(root),
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError:
        # The CLI is absent from this PATH. See the docstring on why an index
        # that cannot refresh is not allowed to fail the edit.
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
